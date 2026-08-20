namespace Benzene.Clients;

/// <summary>
/// The pipeline context for one outbound send: the topic being sent on, the request payload, and a
/// settable slot for the response - the outbound mirror of how inbound transport contexts carry a
/// request and a result. Deliberately non-generic (matching every other <c>IMiddleware&lt;TContext&gt;</c>
/// in this codebase, e.g. <c>SqsClientMiddleware</c>/<c>SnsClientMiddleware</c>) rather than
/// <c>OutboundContext&lt;TRequest&gt;</c> - see <c>work/archive/benzene-clients-redesign-plan-2026-07.md</c> §2.2/§5.
/// </summary>
public class OutboundContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundContext"/> class.
    /// </summary>
    /// <param name="topic">The topic being sent on.</param>
    /// <param name="request">The request payload.</param>
    /// <param name="headers">Per-call headers supplied by the caller (see
    /// <see cref="IBenzeneMessageSender.SendAsync{TRequest,TResponse}"/>); never null.</param>
    public OutboundContext(string topic, object request, IDictionary<string, string>? headers = null)
    {
        Topic = topic;
        Request = request;
        // Copy, don't alias: the outbound middleware (correlation id, W3C trace context) write onto
        // Headers, so holding the caller's own dictionary would mutate it across sends. A caller that
        // reuses one dictionary would otherwise leak a stale traceparent/tracestate from a previous
        // send onto the next (and concurrent sends sharing a dict would race a non-thread-safe map).
        Headers = headers is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the topic this send was routed to.</summary>
    public string Topic { get; }

    /// <summary>
    /// Gets or sets the request payload being sent. Settable so pre-converter outbound middleware can
    /// substitute what actually gets serialized/sent - e.g. <c>Benzene.ClaimCheck</c>'s offload
    /// middleware replaces an oversized request with a small placeholder that carries a claim-check
    /// reference, after having stored the real serialized body out-of-band. Middleware that does this
    /// must run before the terminal transport converter, which is the only thing that reads
    /// <see cref="Request"/> downstream.
    /// </summary>
    public object Request { get; set; }

    /// <summary>Gets the per-call headers supplied by the caller.</summary>
    public IDictionary<string, string> Headers { get; }

    /// <summary>
    /// Gets or sets the response, set by the outbound pipeline's transport middleware (e.g.
    /// <c>SqsClientMiddleware</c>) once the send completes. Read back by
    /// <see cref="DefaultBenzeneMessageSender"/> after the pipeline finishes.
    /// </summary>
    public object? Response { get; set; }
}
