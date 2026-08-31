using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS.Model;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.Aws.Sqs.Consumer;

/// <summary>
/// Processes a batch of received SQS messages by mapping each to an <see cref="SqsConsumerMessageContext"/>
/// and running them all through the middleware pipeline concurrently, tagging the transport as
/// <c>"sqs"</c> for the duration.
/// </summary>
public class SqsConsumerApplication : IMiddlewareApplication<ReceiveMessageResponse, SqsConsumerBatchResult>
{
    private readonly IMiddlewarePipeline<SqsConsumerMessageContext> _pipeline;
    private readonly SqsConsumerOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqsConsumerApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built SQS middleware pipeline to run each message through.</param>
    /// <param name="options">
    /// Configures how the batch is acknowledged. Defaults to a new <see cref="SqsConsumerOptions"/>
    /// instance (<see cref="SqsConsumerAckMode.PerMessage"/>) if omitted.
    /// </param>
    public SqsConsumerApplication(IMiddlewarePipeline<SqsConsumerMessageContext> pipeline, SqsConsumerOptions? options = null)
    {
        _pipeline = new TransportMiddlewarePipeline<SqsConsumerMessageContext>(TransportNames.Sqs, pipeline);
        _options = options ?? new SqsConsumerOptions();
    }

    /// <summary>
    /// Handles a poll batch, running each message through the pipeline in its own service scope.
    /// </summary>
    /// <param name="event">The poll batch to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to create a scope per message.</param>
    /// <returns>
    /// A task that resolves to the batch's per-message outcome. Every caught exception is logged
    /// against its message id (see <see cref="ILogger{TCategoryName}"/> for <see cref="SqsConsumerApplication"/>)
    /// before anything else happens to it - matching every other self-hosted worker
    /// (Kafka/RabbitMQ/Event Hub) and the AWS Lambda SQS trigger, none of which fail a message
    /// silently. Under <see cref="SqsConsumerAckMode.PerMessage"/> (the default), the message is then
    /// reported as failed and the exception is contained - it never propagates out of this call. Under
    /// <see cref="SqsConsumerAckMode.WholeBatch"/>, it's rethrown after logging, so it still propagates
    /// out of this call - the returned result is only reached when every message ran without throwing.
    /// </returns>
    public Task<SqsConsumerBatchResult> HandleAsync(ReceiveMessageResponse @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Handles a poll batch, running each message through the pipeline in its own service scope,
    /// additionally seeding <b>each</b> per-message scope's ambient cancellation token so any
    /// component resolved during that message's pipeline run can observe cancellation via
    /// <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    /// <param name="event">The poll batch to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to create a scope per message.</param>
    /// <param name="cancellationToken">
    /// The consumer's run/shutdown token for this poll, or <see cref="CancellationToken.None"/> if it
    /// has no signal. The same token is seeded into every message's own scope - one invocation per
    /// message even within a batch, so there is one token to seed per scope. A handler that observes
    /// the token and throws <see cref="OperationCanceledException"/> once it has fired is caught below
    /// like any other exception and reported as failed (not deleted), so the message is redelivered
    /// instead of lost.
    /// </param>
    /// <returns>
    /// A task that resolves to the batch's per-message outcome. Every caught exception is logged
    /// against its message id before anything else happens to it - matching every other self-hosted
    /// worker (Kafka/RabbitMQ/Event Hub) and the AWS Lambda SQS trigger, none of which fail a message
    /// silently. Under <see cref="SqsConsumerAckMode.PerMessage"/> (the default), the message is then
    /// reported as failed and the exception is contained - it never propagates out of this call. Under
    /// <see cref="SqsConsumerAckMode.WholeBatch"/>, it's rethrown after logging, so it still propagates
    /// out of this call - the returned result is only reached when every message ran without throwing.
    /// </returns>
    public async Task<SqsConsumerBatchResult> HandleAsync(ReceiveMessageResponse @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
    {
        // Each message's task RETURNS its failed Message (or null) rather than appending to a shared
        // List: the continuations run concurrently under Task.WhenAll and List<T>.Add is not
        // thread-safe, so a shared-list append raced - it could drop a failed message's entry (which
        // then lands in successfulMessages below and gets DELETED from the queue despite failing -
        // silent message loss), duplicate one, or throw mid-resize. BoundedFanOut optionally caps how
        // many messages run at once (SqsConsumerOptions.MaxDegreeOfParallelism); unset leaves the
        // fan-out unbounded, exactly as before.
        var pairs = @event.Messages.Select(message => (Message: message, Context: SqsConsumerMessageContext.CreateInstance(message)));
        var results = await BoundedFanOut.WhenAllAsync(pairs, async pair =>
            {
                try
                {
                    using (var scope = serviceResolverFactory.CreateScope())
                    {
                        scope.SeedCancellationToken(cancellationToken);
                        await _pipeline.HandleAsync(pair.Context, scope);
                    }

                    // Only an explicit success is deleted. A failure result OR an unset outcome
                    // (null MessageResult - e.g. an unroutable message whose result setter never ran)
                    // is reported as failed, so it stays on the queue for redelivery/DLQ redrive
                    // instead of being silently deleted. At-least-once: err toward redelivery.
                    if (pair.Context.MessageResult?.IsSuccessful != true)
                    {
                        return pair.Message;
                    }
                }
                catch (Exception ex)
                {
                    // Logged before anything else happens to the exception - regardless of AckMode -
                    // so a failure is never silent: matching SqsApplication (the Lambda-triggered SQS
                    // sibling) and every other self-hosted worker (Kafka/RabbitMQ/Event Hub), all of
                    // which log a per-message catch. A fresh scope is used rather than the (already
                    // disposed) per-message one, exactly like SingleContextEscalatingApplicationBase's
                    // own logging scope.
                    using (var loggingScope = serviceResolverFactory.CreateScope())
                    {
                        loggingScope.GetService<ILogger<SqsConsumerApplication>>()
                            .LogError(ex, BenzeneFailure.IsInfrastructure(ex)
                                ? BenzeneFailure.InfrastructureLogPrefix + " Processing SQS message {messageId} failed — this service is mis-wired; the message is not at fault"
                                : "Processing SQS message {messageId} failed", pair.Message.MessageId);
                    }

                    if (_options.AckMode == SqsConsumerAckMode.WholeBatch)
                    {
                        throw;
                    }

                    return pair.Message;
                }

                return null;
            }, _options.MaxDegreeOfParallelism, cancellationToken);

        var failedMessages = results
            .Where(message => message != null)
            .Select(message => message!)
            .ToList();

        var successfulMessages = @event.Messages.Except(failedMessages).ToArray();
        return new SqsConsumerBatchResult(successfulMessages, failedMessages);
    }
}
