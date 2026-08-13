namespace Benzene.Outbox;

/// <summary>
/// Carries the "this outbound send is a relay dispatch, not a fresh capture" marker for the current
/// message, set by <see cref="IOutboxDispatcher"/> before it invokes <c>IBenzeneMessageSender</c> and
/// read back by <see cref="OutboxMiddleware"/>.
/// </summary>
/// <remarks>
/// Registered scoped (one instance per message/dispatch, alongside the rest of that message's DI
/// scope) - deliberately NOT carried on <c>OutboundContext</c>. A context type describes the shape of
/// an outbound send; it should stay free of an optional, cross-cutting marker only one specific
/// middleware in one specific route cares about. Scoped DI state is the seam for that instead: a
/// route that never adds <c>UseOutbox()</c> never constructs <see cref="OutboxMiddleware"/>, so this
/// holder's <see cref="IsDispatching"/> simply stays <see langword="false"/> for that message - no
/// context coupling, no per-route DI registration conflicts. See
/// <c>Benzene.Abstractions.Middleware/CLAUDE.md</c>'s "Context purity" section for the general
/// pattern this follows (the same one <c>Benzene.Core.MessageHandlers</c>' <c>PresetTopicHolder</c>
/// uses).
/// </remarks>
public class OutboxDispatchScope
{
    /// <summary>
    /// Gets whether the current outbound send is a relay dispatch of a previously captured envelope,
    /// rather than a fresh send that should be captured.
    /// </summary>
    public bool IsDispatching { get; private set; }

    /// <summary>
    /// Gets the envelope's stored headers (the post-stamping snapshot captured at capture time), or
    /// <see langword="null"/> when <see cref="IsDispatching"/> is <see langword="false"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; private set; }

    /// <summary>
    /// Marks the current message as a relay dispatch, carrying the envelope's stored headers.
    /// </summary>
    /// <param name="headers">The envelope's stored headers, which <see cref="OutboxMiddleware"/> re-applies onto the outbound context (stored values win over anything a relay host's own ambient stamping middleware just set).</param>
    public void Begin(IReadOnlyDictionary<string, string> headers)
    {
        IsDispatching = true;
        Headers = headers;
    }
}
