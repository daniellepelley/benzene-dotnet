using System;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;

namespace Benzene.Azure.Function.EventGrid;

/// <summary>
/// Provides the middleware pipeline context for a single event within an Azure Functions Event Grid
/// trigger invocation.
/// </summary>
public class EventGridContext : IHasMessageResult
{
    private readonly string? _rawJson;
    private EventGridTriggerEvent? _event;
    private Exception? _parseException;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventGridContext"/> class from an already-parsed
    /// event - used when the caller hands over <see cref="EventGridTriggerEvent"/> objects directly
    /// (<c>HandleEventGridEvents(params EventGridTriggerEvent[])</c>), so there is nothing left to
    /// parse and <see cref="Event"/> can never throw.
    /// </summary>
    /// <param name="event">The delivered event.</param>
    public EventGridContext(EventGridTriggerEvent @event)
    {
        _event = @event ?? throw new ArgumentNullException(nameof(@event));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EventGridContext"/> class from a raw, not-yet-parsed
    /// delivery - used for the <c>[EventGridTrigger] string</c> binding path
    /// (<c>HandleEventGridEvent(string)</c>). <see cref="EventGridTriggerEvent.Parse"/> is deferred to
    /// the first <see cref="Event"/> access, which happens while this context's own item is being run
    /// through the middleware pipeline (a topic/body/headers getter reading <c>context.Event.X</c>) -
    /// i.e. <em>inside</em> <c>AzureFunctionBatchApplicationBase.ProcessItemAsync</c>'s try block,
    /// not before it. Round 14-15 #235: parsing used to happen eagerly, as a method argument, before
    /// that try block was ever reached - a <see cref="System.Text.Json.JsonException"/> from malformed
    /// input was then an unguarded throw <c>EventGridOptions.CatchExceptions</c> never got a chance to
    /// govern. Deferring the parse to here routes a malformed delivery through the exact same
    /// catch/escalate/log machinery any other per-event failure goes through.
    /// </summary>
    /// <param name="rawJson">The event JSON as delivered to the trigger, not yet parsed.</param>
    public EventGridContext(string rawJson)
    {
        _rawJson = rawJson;
    }

    /// <summary>
    /// Gets the delivered event. For a context built from raw JSON, this parses (and caches the
    /// result, success or failure, so a retry of a failed parse doesn't reparse and every access
    /// after the first throws the same exception instance) on first access rather than at
    /// construction - see the raw-JSON constructor's own doc comment for why.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">
    /// The raw JSON this context was built from is malformed. Only possible for a context built via
    /// the raw-JSON constructor.
    /// </exception>
    public EventGridTriggerEvent Event
    {
        get
        {
            if (_event != null)
            {
                return _event;
            }

            if (_parseException != null)
            {
                throw _parseException;
            }

            try
            {
                _event = EventGridTriggerEvent.Parse(_rawJson!);
                return _event;
            }
            catch (Exception ex)
            {
                _parseException = ex;
                throw;
            }
        }
    }

    /// <summary>
    /// Gets or sets the result of handling this event. Event Grid deliveries are fire-and-forget
    /// from the handler's perspective (a thrown exception is what triggers Event Grid's own
    /// retry/dead-letter machinery), so this is recorded for middleware/diagnostics only.
    /// </summary>
    public IBenzeneResult MessageResult { get; set; } = null!;
}
