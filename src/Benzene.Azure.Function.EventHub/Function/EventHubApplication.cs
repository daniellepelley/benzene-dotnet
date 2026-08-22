using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Core;
using Benzene.Core.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.Azure.Function.EventHub.Function;

/// <summary>
/// The entry point application for an Event Hub-triggered Azure Function. Maps each event in the
/// triggered batch to an <see cref="EventHubContext"/> and runs them all through the middleware
/// pipeline, tagging the transport as <c>"event-hub"</c> for the duration. Exception/failure-status
/// behavior and fan-out concurrency are configurable via <see cref="EventHubOptions"/>, mirroring
/// <c>Benzene.Azure.Function.EventGrid</c> and <c>Benzene.Azure.Function.QueueStorage</c>.
/// </summary>
public class EventHubApplication : EntryPointMiddlewareApplication<EventData[]>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EventHubApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built Event Hub middleware pipeline to run each event through.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each batch.</param>
    /// <param name="maxDegreeOfParallelism">
    /// Optionally caps how many events from a batch run at once; <c>null</c> (the default) leaves the
    /// fan-out unbounded - the original behavior. Preserved for backward compatibility; prefer the
    /// <see cref="EventHubApplication(IMiddlewarePipeline{EventHubContext}, IServiceResolverFactory, EventHubOptions)"/>
    /// overload for the exception/failure-status knobs too.
    /// </param>
    public EventHubApplication(IMiddlewarePipeline<EventHubContext> pipeline, IServiceResolverFactory serviceResolverFactory, int? maxDegreeOfParallelism = null)
        : base(new EventHubBatchApplication(pipeline, new EventHubOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }), serviceResolverFactory)
    { }

    /// <summary>
    /// Initializes a new instance of the <see cref="EventHubApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built Event Hub middleware pipeline to run each event through.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to process each batch.</param>
    /// <param name="options">
    /// Configures how a handler's exceptions and failure results are handled, and the batch fan-out
    /// concurrency. Defaults to a new <see cref="EventHubOptions"/> instance (safe-by-default:
    /// <see cref="EventHubOptions.RaiseOnFailureStatus"/> on, <see cref="EventHubOptions.CatchExceptions"/>
    /// off, unbounded fan-out) if omitted.
    /// </param>
    public EventHubApplication(IMiddlewarePipeline<EventHubContext> pipeline, IServiceResolverFactory serviceResolverFactory, EventHubOptions options)
        : base(new EventHubBatchApplication(pipeline, options), serviceResolverFactory)
    { }
}

/// <summary>
/// Runs every event in an Event Hub triggered batch through the middleware pipeline concurrently, each
/// in its own service scope, applying <see cref="EventHubOptions"/> to decide whether an event's
/// exception or failure result is contained (logged, so its siblings still complete) or left to
/// cascade and fail the whole Functions invocation (so the Event Hubs trigger re-delivers the entire
/// batch). Mirrors <c>EventGridBatchApplication</c> and <c>QueueStorageBatchApplication</c>. The
/// fan-out/settle/escalate/log skeleton itself lives in
/// <see cref="AzureFunctionBatchApplicationBase{TContext, TState}"/>; this class plugs in the
/// Event Hub-specific bits (context creation, sequence-number id, and
/// <see cref="EventHubMessageProcessingException"/>) - Event Hub uses no extra per-item state, so
/// <c>TState</c> is <c>object?</c>.
/// </summary>
public class EventHubBatchApplication : AzureFunctionBatchApplicationBase<EventHubContext, object?>, IMiddlewareApplication<EventData[]>
{
    public EventHubBatchApplication(IMiddlewarePipeline<EventHubContext> pipeline, EventHubOptions? options = null)
        : base(pipeline, TransportNames.EventHub, (options ??= new EventHubOptions()).CatchExceptions, options.RaiseOnFailureStatus, options.MaxDegreeOfParallelism)
    { }

    public Task HandleAsync(EventData[] @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Runs every event in the triggered batch through the pipeline, additionally seeding <b>each</b>
    /// event's own scope with the ambient cancellation token so any component resolved during that
    /// event's pipeline run can observe cancellation via <see cref="ICancellationTokenAccessor"/>.
    /// </summary>
    public Task HandleAsync(EventData[] @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
        => HandleBatchAsync(@event.Select(item => (EventHubContext.CreateInstance(item), (object?)null)), serviceResolverFactory, cancellationToken);

    /// <inheritdoc/>
    protected override Exception CreateProcessingException(EventHubContext context)
        => new EventHubMessageProcessingException(context.EventData.SequenceNumber.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    protected override object GetLogId(EventHubContext context) => context.EventData.SequenceNumber;

    /// <inheritdoc/>
    protected override string FailureLogMessageTemplate => "Processing Event Hub event {sequenceNumber} failed";

    /// <inheritdoc/>
    protected override ILogger GetLogger(IServiceResolver serviceResolver)
        => serviceResolver.GetService<ILogger<EventHubApplication>>();
}
