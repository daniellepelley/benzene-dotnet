using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Core;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Middleware;

namespace Benzene.Aws.Lambda.EventBridge;

/// <summary>
/// Runs one EventBridge event through the <see cref="EventBridgeContext"/> middleware pipeline —
/// a single-context application (one pipeline invocation per event), since EventBridge invokes a
/// Lambda target with exactly one event, not a batch. Exception/failure-status behavior is
/// configurable via <see cref="EventBridgeOptions"/>, mirroring <c>SnsApplication</c>.
/// </summary>
public class EventBridgeApplication : SingleContextEscalatingApplicationBase<EventBridgeApplication, EventBridgeContext>, IMiddlewareApplication<EventBridgeEvent>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EventBridgeApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built EventBridge middleware pipeline to run each event through.</param>
    /// <param name="options">
    /// Configures how a handler's exceptions and failure results are handled. Defaults to a new
    /// <see cref="EventBridgeOptions"/> instance (safe-by-default:
    /// <see cref="EventBridgeOptions.RaiseOnFailureStatus"/> on,
    /// <see cref="EventBridgeOptions.CatchExceptions"/> off) if omitted.
    /// </param>
    public EventBridgeApplication(IMiddlewarePipeline<EventBridgeContext> pipeline, EventBridgeOptions? options = null)
        : base(
            new TransportMiddlewarePipeline<EventBridgeContext>(TransportNames.EventBridge, pipeline),
            (options ??= new EventBridgeOptions()).CatchExceptions,
            options.RaiseOnFailureStatus,
            context => context.Event.Id,
            eventId => new EventBridgeMessageProcessingException(eventId),
            "Processing EventBridge event {id} failed")
    {
    }

    /// <summary>
    /// Handles a single EventBridge event, running it through the pipeline in its own service scope.
    /// Whether a failure result propagates out of this call (and therefore fails the Lambda
    /// invocation, so the rule target's retry policy applies) is governed by
    /// <see cref="EventBridgeOptions.RaiseOnFailureStatus"/>/<see cref="EventBridgeOptions.CatchExceptions"/>.
    /// </summary>
    /// <param name="event">The EventBridge event to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to create the per-event scope.</param>
    public Task HandleAsync(EventBridgeEvent @event, IServiceResolverFactory serviceResolverFactory)
        => ProcessAsync(new EventBridgeContext(@event), serviceResolverFactory);
}
