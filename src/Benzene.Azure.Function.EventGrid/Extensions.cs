using System.Threading;
using Benzene.Azure.Function.Core;

namespace Benzene.Azure.Function.EventGrid;

/// <summary>
/// Provides extension methods for dispatching Event Grid trigger deliveries to a built
/// <see cref="IAzureFunctionApp"/>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Dispatches Event Grid events to the Azure Function app's Event Grid entry point application.
    /// The trigger delivers one event per invocation by default; the <c>params</c> shape covers
    /// batched delivery and tests.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="events">The events to handle.</param>
    /// <returns>A task that completes when the events have been handled.</returns>
    public static Task HandleEventGridEvents(this IAzureFunctionApp source, params EventGridTriggerEvent[] events)
    {
        return source.HandleAsync(events);
    }

    /// <summary>
    /// Dispatches Event Grid events to the Azure Function app's Event Grid entry point application,
    /// forwarding <paramref name="cancellationToken"/> so any component resolved during the pipeline
    /// can observe it via <c>ICancellationTokenAccessor</c>. A leading (rather than optional trailing)
    /// parameter - a <c>params</c> array must be last, so the token can't default after it; bind the
    /// isolated worker's <see cref="CancellationToken"/> trigger method parameter and pass it here.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="cancellationToken">The isolated worker's cancellation token for this invocation.</param>
    /// <param name="events">The events to handle.</param>
    /// <returns>A task that completes when the events have been handled.</returns>
    public static Task HandleEventGridEvents(this IAzureFunctionApp source, CancellationToken cancellationToken, params EventGridTriggerEvent[] events)
    {
        return source.HandleAsync(events, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Dispatches Event Grid events to the <paramref name="name"/>-keyed entry point - use when more
    /// than one Event Grid function is registered (each via <c>UseEventGrid(..., name: "fn")</c>).
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="name">The discriminator name matching the registered <c>UseEventGrid(..., name)</c>.</param>
    /// <param name="events">The events to handle.</param>
    /// <returns>A task that completes when the events have been handled.</returns>
    public static Task HandleEventGridEvents(this IAzureFunctionApp source, string name, params EventGridTriggerEvent[] events)
    {
        return source.HandleAsync(events, name);
    }

    /// <summary>
    /// Dispatches Event Grid events to the <paramref name="name"/>-keyed entry point, forwarding
    /// <paramref name="cancellationToken"/> so any component resolved during the pipeline can observe
    /// it via <c>ICancellationTokenAccessor</c>.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="name">The discriminator name matching the registered <c>UseEventGrid(..., name)</c>.</param>
    /// <param name="cancellationToken">The isolated worker's cancellation token for this invocation.</param>
    /// <param name="events">The events to handle.</param>
    /// <returns>A task that completes when the events have been handled.</returns>
    public static Task HandleEventGridEvents(this IAzureFunctionApp source, string name, CancellationToken cancellationToken, params EventGridTriggerEvent[] events)
    {
        return source.HandleAsync(events, name, cancellationToken);
    }

    /// <summary>
    /// Dispatches a raw Event Grid delivery - the <c>[EventGridTrigger] string</c> binding - to the
    /// Azure Function app's Event Grid entry point application. Parsing (either the Event Grid schema
    /// or CloudEvents 1.0 - see <see cref="EventGridTriggerEvent.Parse"/>) happens inside the
    /// dispatched entry point's own per-event pipeline execution, not here - round 14-15 #235: a
    /// malformed <paramref name="eventJson"/> now surfaces as an ordinary per-event failure governed
    /// by <see cref="EventGridOptions.CatchExceptions"/>, rather than an unguarded throw before
    /// dispatch even starts. See <see cref="EventGridContext"/>'s raw-JSON constructor.
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="eventJson">The event JSON as delivered to the trigger.</param>
    /// <param name="cancellationToken">
    /// The isolated worker's cancellation token for this invocation, forwarded so any component
    /// resolved during the pipeline can observe it via <c>ICancellationTokenAccessor</c>. Defaults to
    /// <see cref="CancellationToken.None"/> if the trigger doesn't bind one.
    /// </param>
    /// <returns>A task that completes when the event has been handled.</returns>
    public static Task HandleEventGridEvent(this IAzureFunctionApp source, string eventJson, CancellationToken cancellationToken = default)
    {
        return source.HandleAsync(new[] { eventJson }, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Dispatches a raw Event Grid delivery to the <paramref name="name"/>-keyed entry point - use
    /// when more than one Event Grid function is registered. See the unkeyed overload's doc comment
    /// for why parsing happens inside the dispatched entry point rather than here (round 14-15 #235).
    /// </summary>
    /// <param name="source">The built Azure Function app to dispatch to.</param>
    /// <param name="name">The discriminator name matching the registered <c>UseEventGrid(..., name)</c>.</param>
    /// <param name="eventJson">The event JSON as delivered to the trigger.</param>
    /// <param name="cancellationToken">
    /// The isolated worker's cancellation token for this invocation, forwarded so any component
    /// resolved during the pipeline can observe it via <c>ICancellationTokenAccessor</c>. Defaults to
    /// <see cref="CancellationToken.None"/> if the trigger doesn't bind one.
    /// </param>
    /// <returns>A task that completes when the event has been handled.</returns>
    public static Task HandleEventGridEvent(this IAzureFunctionApp source, string name, string eventJson, CancellationToken cancellationToken = default)
    {
        return source.HandleAsync(new[] { eventJson }, name, cancellationToken);
    }
}
