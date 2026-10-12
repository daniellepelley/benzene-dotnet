using System.Runtime.CompilerServices;

namespace Benzene.Clients.TraceContext;

/// <summary>
/// Marks an outbound send's <c>traceparent</c>/<c>tracestate</c> as <b>authoritative</b>, so trace-context
/// middleware later in the same route leaves them alone. The mesh's trace-context middleware
/// (<c>Benzene.Mesh.Wire</c>'s <c>UseMeshTraceContext</c>) pins the mesh span it stamps - mesh.md §3 / M2:
/// the forwarded <c>traceparent</c> MUST carry the span the invocation's TraceEvent records, "whatever
/// other tracing the port also runs" - so a route that also runs <see cref="W3CTraceContextMiddleware"/>
/// (OpenTelemetry's <c>Activity.Current</c>) cannot overwrite it, whichever of the two runs first.
/// </summary>
/// <remarks>
/// Held beside the context rather than on it (the context stays the message's shape - see
/// <c>Benzene.Abstractions.Middleware</c>'s CLAUDE.md), keyed weakly by the <see cref="OutboundContext"/>
/// instance, so it lives exactly as long as the send.
/// </remarks>
public static class OutboundTraceContext
{
    private static readonly ConditionalWeakTable<OutboundContext, object> Pinned = new();

    /// <summary>
    /// Sets <paramref name="traceparent"/> (and <paramref name="tracestate"/>, or removes a stale one when
    /// null) on <paramref name="context"/>'s headers and pins them.
    /// </summary>
    /// <param name="context">The outbound send.</param>
    /// <param name="traceparent">The W3C <c>traceparent</c> to forward.</param>
    /// <param name="tracestate">The W3C <c>tracestate</c> to forward, if any.</param>
    public static void Pin(OutboundContext context, string traceparent, string? tracestate)
    {
        ArgumentNullException.ThrowIfNull(context);
        // OutboundContext.Headers is case-insensitive, so this replaces a "Traceparent" too.
        context.Headers["traceparent"] = traceparent;
        if (string.IsNullOrEmpty(tracestate))
        {
            // A tracestate belongs to the traceparent it travelled with; never forward another parent's.
            context.Headers.Remove("tracestate");
        }
        else
        {
            context.Headers["tracestate"] = tracestate;
        }

        Pinned.AddOrUpdate(context, Pinned);
    }

    /// <summary>Whether <paramref name="context"/>'s trace context was pinned by <see cref="Pin"/>.</summary>
    /// <param name="context">The outbound send.</param>
    public static bool IsPinned(OutboundContext context) => Pinned.TryGetValue(context, out _);

    /// <summary>Carries <paramref name="source"/>'s pin, if any, onto <paramref name="copy"/> (e.g. a parallel branch's clone).</summary>
    /// <param name="source">The original send.</param>
    /// <param name="copy">Its copy.</param>
    public static void CopyPin(OutboundContext source, OutboundContext copy)
    {
        if (IsPinned(source))
        {
            Pinned.AddOrUpdate(copy, Pinned);
        }
    }
}
