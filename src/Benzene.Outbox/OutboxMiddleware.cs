using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Outbox;

/// <summary>
/// The capture point of the outbox: an outbound-route <see cref="IMiddleware{TContext}"/> added via
/// <c>UseOutbox()</c>, placed after cross-cutting stamping middleware
/// (<c>UseW3CTraceContext()</c>/<c>UseCorrelationId()</c>) and before the terminal transport
/// converter (<c>UseSqs(...)</c> etc.).
/// </summary>
/// <remarks>
/// <para>
/// <b>Capture semantics.</b> When no dispatch marker is present on the current message's
/// <see cref="OutboxDispatchScope"/>, this middleware is terminal: it builds an
/// <see cref="OutboxEnvelope"/> from the context (serializing <c>Request</c> with the scope's
/// <c>ISerializer</c>), hands it to the configured <see cref="OutboxOptions.WriteMode"/>, sets
/// <c>context.Response</c> to <c>BenzeneResult.Accepted&lt;Void&gt;()</c>, and does NOT call
/// <c>next</c>. A persistence failure propagates as an exception - the caller sees the failure exactly
/// as a transport failure today; never silent loss.
/// </para>
/// <para>
/// Because capture always sets an <c>IBenzeneResult&lt;Void&gt;</c> response, an outboxed route is
/// fire-and-forget only: a caller that asks for any <c>TResponse</c> other than <c>Void</c> gets the
/// existing <see cref="OutboundResponseTypeMismatchException"/> from <c>DefaultBenzeneMessageSender</c>
/// - exactly the same behavior a send-acknowledgement-only transport (SQS/SNS/...) already produces,
/// with no extra code needed here.
/// </para>
/// <para>
/// <b>Pass-through (dispatch) semantics.</b> When <see cref="OutboxDispatchScope.IsDispatching"/> is
/// <see langword="true"/> (set by <see cref="IOutboxDispatcher"/> before it re-sends a previously
/// captured envelope), this middleware re-applies the envelope's stored headers onto
/// <c>context.Headers</c> - stored values win over anything an earlier middleware in this same
/// dispatch just stamped from the relay host's own ambient trace context - and calls <c>next()</c> so
/// the send proceeds to the real transport.
/// </para>
/// </remarks>
public class OutboxMiddleware : IMiddleware<OutboundContext>
{
    private readonly IOutboxStore _store;
    private readonly IOutboxStage _stage;
    private readonly OutboxDispatchScope _dispatchScope;
    private readonly ISerializer _serializer;
    private readonly OutboxOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxMiddleware"/> class.
    /// </summary>
    public OutboxMiddleware(
        IOutboxStore store,
        IOutboxStage stage,
        OutboxDispatchScope dispatchScope,
        ISerializer serializer,
        OutboxOptions options)
    {
        _store = store;
        _stage = stage;
        _dispatchScope = dispatchScope;
        _serializer = serializer;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => nameof(OutboxMiddleware);

    /// <inheritdoc />
    public async Task HandleAsync(OutboundContext context, Func<Task> next)
    {
        if (_dispatchScope.IsDispatching)
        {
            // Relay dispatch of a previously captured envelope: replay the envelope's own stored
            // headers (stored business-time context wins) and let the send proceed to the real
            // transport, rather than capturing it again.
            foreach (var header in _dispatchScope.Headers!)
            {
                context.Headers[header.Key] = header.Value;
            }

            await next();
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var headers = new Dictionary<string, string>(context.Headers, StringComparer.OrdinalIgnoreCase);
        if (_options.StampIdempotencyKey && !headers.ContainsKey(OutboxDefaults.IdempotencyKeyHeaderName))
        {
            headers[OutboxDefaults.IdempotencyKeyHeaderName] = id;
        }

        var requestType = context.Request.GetType();
        var payloadTypeName = requestType.AssemblyQualifiedName
            ?? throw new InvalidOperationException(
                $"Cannot capture an outbox envelope for topic '{context.Topic}': request type " +
                $"'{requestType}' has no assembly-qualified name (e.g. an open generic type), so it " +
                "cannot be recovered at dispatch time.");
        var payload = _serializer.Serialize(requestType, context.Request);

        var envelope = new OutboxEnvelope(id, context.Topic, payload, payloadTypeName, headers, DateTimeOffset.UtcNow);

        if (_options.WriteMode == OutboxWriteMode.Immediate)
        {
            await _store.AddAsync([envelope]);
        }
        else
        {
            await _stage.StageAsync(envelope);
        }

        // Terminal: never call next(). The caller sees an accepted send now; the real transport send
        // happens later, out of band, when the dispatcher relays this envelope.
        context.Response = BenzeneResult.Accepted<Void>();
    }
}
