using System.Diagnostics;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.TraceContext;

/// <summary>
/// Outbound middleware that stamps the current <see cref="Activity"/>'s W3C
/// <c>traceparent</c>/<c>tracestate</c> onto <see cref="OutboundContext.Headers"/>, so the receiving
/// service can continue the same distributed trace. The middleware-pipeline replacement for
/// <see cref="TraceContextBenzeneMessageClient"/> - see
/// <c>work/archive/benzene-clients-redesign-plan-2026-07.md</c> §2.4.
/// </summary>
/// <remarks>
/// For an application that runs OpenTelemetry (or any <see cref="Activity"/>-based tracing) <b>without</b>
/// the mesh. A meshed service forwards the mesh span instead - <c>Benzene.Mesh.Wire</c>'s
/// <c>UseMeshTraceContext()</c> - because mesh.md §3 / M2 requires the forwarded <c>traceparent</c> to
/// carry the span the invocation's TraceEvent records, and <c>Activity.Current</c> is a different span.
/// When both run on a route, this middleware leaves a trace context the mesh pinned
/// (<see cref="OutboundTraceContext"/>) untouched, whichever runs first.
/// </remarks>
public class W3CTraceContextMiddleware : IMiddleware<OutboundContext>
{
    /// <summary>Gets the name of this middleware.</summary>
    public string Name => nameof(W3CTraceContextMiddleware);

    /// <summary>
    /// Stamps the current activity's trace context onto the outbound headers, then continues the pipeline.
    /// </summary>
    /// <param name="context">The outbound context to stamp headers onto.</param>
    /// <param name="next">The next middleware in the pipeline.</param>
    public Task HandleAsync(OutboundContext context, Func<Task> next)
    {
        var activity = Activity.Current;
        if (activity != null && !OutboundTraceContext.IsPinned(context))
        {
            context.Headers["traceparent"] = activity.Id!;
            if (!string.IsNullOrEmpty(activity.TraceStateString))
            {
                context.Headers["tracestate"] = activity.TraceStateString;
            }
        }

        return next();
    }
}
