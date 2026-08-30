using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Core;
using Benzene.Core.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.Azure.Function.EventGrid;

/// <summary>
/// The entry point application for an Event Grid-triggered Azure Function. Maps each event to an
/// <see cref="EventGridContext"/> and runs it through the middleware pipeline, tagging the transport
/// as <c>"event-grid"</c> for the duration. Exception/failure-status behavior is configurable via
/// <see cref="EventGridOptions"/>, mirroring <c>Benzene.Azure.Function.Kafka</c>.
/// </summary>
public class EventGridApplication : EntryPointMiddlewareApplication<EventGridTriggerEvent[]>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EventGridApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built Event Grid middleware pipeline to run each event through.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each invocation.</param>
    /// <param name="options">
    /// Configures how a handler's exceptions and failure results are handled, and the batch fan-out
    /// concurrency. Defaults to a new <see cref="EventGridOptions"/> instance (safe-by-default:
    /// <see cref="EventGridOptions.RaiseOnFailureStatus"/> on, <see cref="EventGridOptions.CatchExceptions"/>
    /// off) if omitted.
    /// </param>
    public EventGridApplication(IMiddlewarePipeline<EventGridContext> pipeline, IServiceResolverFactory serviceResolverFactory, EventGridOptions options = null)
        : base(new EventGridBatchApplication(pipeline, options), serviceResolverFactory)
    { }
}

/// <summary>
/// Runs every event in an Event Grid delivery through the middleware pipeline concurrently, each in
/// its own service scope, applying <see cref="EventGridOptions"/> to decide whether an event's
/// exception or failure result is contained (logged) or left to cascade and fail the invocation (so
/// Event Grid's retry/dead-letter policy engages). The fan-out/settle/escalate/log skeleton itself
/// lives in <see cref="AzureFunctionBatchApplicationBase{TContext, TState}"/>; this class plugs
/// in the Event Grid-specific bits - Event Grid uses no extra per-item state, so <c>TState</c> is
/// <c>object?</c>.
/// </summary>
public class EventGridBatchApplication : AzureFunctionBatchApplicationBase<EventGridContext, object?>, IMiddlewareApplication<EventGridTriggerEvent[]>
{
    public EventGridBatchApplication(IMiddlewarePipeline<EventGridContext> pipeline, EventGridOptions? options = null)
        : base(pipeline, TransportNames.EventGrid, (options ??= new EventGridOptions()).CatchExceptions, options.RaiseOnFailureStatus, options.MaxDegreeOfParallelism)
    { }

    public Task HandleAsync(EventGridTriggerEvent[] @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Runs every event in the delivery through the pipeline, additionally seeding <b>each</b> event's
    /// own scope with the ambient cancellation token so any component resolved during that event's
    /// pipeline run can observe cancellation via <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    public Task HandleAsync(EventGridTriggerEvent[] @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
        => HandleBatchAsync(@event.Select(item => (new EventGridContext(item), (object?)null)), serviceResolverFactory, cancellationToken);

    /// <inheritdoc/>
    protected override Exception CreateProcessingException(EventGridContext context)
        => new EventGridMessageProcessingException(context.Event.Id ?? context.Event.EventType ?? "unknown");

    /// <inheritdoc/>
    protected override object? GetLogId(EventGridContext context) => context.Event.Id;

    /// <inheritdoc/>
    protected override string FailureLogMessageTemplate => "Processing Event Grid event {id} failed";

    /// <inheritdoc/>
    protected override ILogger GetLogger(IServiceResolver serviceResolver)
        => serviceResolver.GetService<ILogger<EventGridApplication>>();
}
