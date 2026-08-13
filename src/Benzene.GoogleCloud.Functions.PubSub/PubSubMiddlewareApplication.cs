using System.Threading;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.Info;
using Google.Events.Protobuf.Cloud.PubSub.V1;
using Benzene.Core;
using Microsoft.Extensions.Logging;

namespace Benzene.GoogleCloud.Functions.PubSub;

/// <summary>
/// Runs a single Pub/Sub message through the middleware pipeline, applying <see cref="PubSubOptions"/>
/// to decide whether the message's exception or failure result is contained (logged, doesn't fail
/// the invocation) or left to cascade and fail it. Unlike AWS/Azure's batch-oriented trigger
/// applications, there is no fan-out here - Cloud Functions delivers exactly one Pub/Sub message per
/// invocation.
/// </summary>
public class PubSubMiddlewareApplication : IMiddlewareApplication<MessagePublishedData>
{
    private readonly IMiddlewarePipeline<PubSubContext> _pipeline;
    private readonly PubSubOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="PubSubMiddlewareApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built Pub/Sub middleware pipeline to run the message through.</param>
    /// <param name="options">
    /// Configures how a handler's exceptions and failure results are handled. Defaults to a new
    /// <see cref="PubSubOptions"/> instance (safe-by-default:
    /// <see cref="PubSubOptions.RaiseOnFailureStatus"/> on, <see cref="PubSubOptions.CatchExceptions"/>
    /// off) if omitted.
    /// </param>
    public PubSubMiddlewareApplication(IMiddlewarePipeline<PubSubContext> pipeline, PubSubOptions? options = null)
    {
        _pipeline = new TransportMiddlewarePipeline<PubSubContext>(TransportNames.PubSub, pipeline);
        _options = options ?? new PubSubOptions();
    }

    /// <summary>
    /// Handles the Pub/Sub message delivered for this invocation.
    /// </summary>
    /// <param name="event">The Pub/Sub CloudEvent payload.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process the message.</param>
    public Task HandleAsync(MessagePublishedData @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Handles the Pub/Sub message delivered for this invocation, additionally seeding the
    /// per-invocation scope's ambient cancellation token so any component resolved during the
    /// pipeline can observe cancellation via <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    /// <param name="event">The Pub/Sub CloudEvent payload.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process the message.</param>
    /// <param name="cancellationToken">
    /// The Cloud Functions Framework's cancellation token for this invocation, or
    /// <see cref="CancellationToken.None"/> if it has no signal.
    /// </param>
    public async Task HandleAsync(MessagePublishedData @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
    {
        var context = new PubSubContext(@event);

        try
        {
            using (var scope = serviceResolverFactory.CreateScope())
            {
                scope.SeedCancellationToken(cancellationToken);
                await _pipeline.HandleAsync(context, scope);
            }

            if (_options.RaiseOnFailureStatus && context.MessageResult?.IsSuccessful == false)
            {
                throw new PubSubMessageProcessingException(context.Message.MessageId);
            }
        }
        catch (Exception ex) when (_options.CatchExceptions)
        {
            using (var loggingScope = serviceResolverFactory.CreateScope())
            {
                loggingScope.GetService<ILogger<PubSubMiddlewareApplication>>()
                    .LogError(ex, BenzeneFailure.IsInfrastructure(ex)
                    ? BenzeneFailure.InfrastructureLogPrefix + " Processing Pub/Sub message {messageId} failed — this service is mis-wired; the message is not at fault"
                    : "Processing Pub/Sub message {messageId} failed", context.Message.MessageId);
            }
        }
    }
}
