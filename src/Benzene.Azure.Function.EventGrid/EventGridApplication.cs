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
    /// concurrency. Defaults to a new <see cref="EventGridOptions"/> instance (both flags off) if omitted.
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
public class EventGridBatchApplication : AzureFunctionBatchApplicationBase<EventGridContext, object?>, IMiddlewareApplication<EventGridTriggerEvent[]>, IMiddlewareApplication<string[]>
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

    public Task HandleAsync(string[] @event, IServiceResolverFactory serviceResolverFactory)
        => HandleAsync(@event, serviceResolverFactory, CancellationToken.None);

    /// <summary>
    /// Round 14-15 #235: the raw-JSON delivery path (<c>[EventGridTrigger] string</c> binding, via
    /// <c>Extensions.HandleEventGridEvent(string)</c>). Unlike the already-parsed-events overload
    /// above, each <see cref="EventGridContext"/> here is built from the raw JSON directly
    /// (<see cref="EventGridContext(string)"/>) rather than from a pre-parsed
    /// <see cref="EventGridTriggerEvent"/> - <see cref="EventGridTriggerEvent.Parse"/> only runs once
    /// this context's item reaches the pipeline inside the base class's own guarded
    /// <c>ProcessItemAsync</c>, so a <see cref="System.Text.Json.JsonException"/> from malformed input
    /// becomes an ordinary per-event failure - caught and logged under
    /// <see cref="EventGridOptions.CatchExceptions"/>, or left to cascade (Event Grid's own
    /// retry/dead-letter machinery engages, the same as any other unhandled handler exception) when
    /// it's off, matching this transport's retain-on-failure settlement default. Registered as a
    /// second entry point over the same request shape's dispatch, alongside the array-of-events
    /// overload - see <c>DependencyInjectionExtensions.UseEventGrid</c>.
    /// </summary>
    public Task HandleAsync(string[] @event, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
        => HandleBatchAsync(@event.Select(json => (new EventGridContext(json), (object?)null)), serviceResolverFactory, cancellationToken);

    /// <inheritdoc/>
    protected override Exception CreateProcessingException(EventGridContext context)
    {
        // Unreachable for a malformed-JSON context in practice (the pipeline itself would already
        // have thrown from inside context.Event before this line, per the settlement checks in
        // AzureFunctionBatchApplicationBase.ProcessItemAsync - this only runs after the pipeline
        // completed successfully) - guarded anyway, on the same principle as GetLogId below, rather
        // than relying on that being true forever.
        string? id;
        try
        {
            id = context.Event.Id ?? context.Event.EventType;
        }
        catch
        {
            id = null;
        }

        return new EventGridMessageProcessingException(id ?? "unknown");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="EventGridContext.Event"/> can itself throw (a raw-JSON context whose delivery is
    /// malformed - see that constructor's own doc comment) - reading <c>context.Event.Id</c> directly
    /// here would then throw again while merely trying to log/report the ORIGINAL parse failure this
    /// method exists to identify, which - called as it is from inside the
    /// <c>catch (Exception ex) when (catchExceptions)</c> block's own log-argument evaluation - would
    /// itself escape uncaught and defeat <c>CatchExceptions</c> for exactly the malformed-input case
    /// #235 exists to fix. Falls back to null (matching this method's existing nullable contract -
    /// unchanged from before #235 for every other, non-malformed case) rather than throw a second
    /// time.
    /// </remarks>
    protected override object? GetLogId(EventGridContext context)
    {
        try
        {
            return context.Event.Id;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    protected override string FailureLogMessageTemplate => "Processing Event Grid event {id} failed";

    /// <inheritdoc/>
    protected override ILogger GetLogger(IServiceResolver serviceResolver)
        => serviceResolver.GetService<ILogger<EventGridApplication>>();
}
