using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Clients.TraceContext;

namespace Benzene.Mesh.Wire;

/// <summary>
/// Outbound route middleware that forwards the mesh span (mesh.md §3, §7.1 M2): stamps
/// <see cref="MeshSpan.Current"/>'s <c>traceparent</c> and <c>tracestate</c> onto the send's headers,
/// replacing any already there, and pins them (<see cref="OutboundTraceContext"/>) so OpenTelemetry's
/// <see cref="W3CTraceContextMiddleware"/> cannot overwrite them. No mesh span: nothing is stamped.
/// Wired by <c>UseMeshTraceContext()</c> (per route, or on every route through the routing builder).
/// </summary>
public class MeshTraceContextMiddleware : IMiddleware<OutboundContext>
{
    /// <summary>Gets the name of this middleware.</summary>
    public string Name => "MeshTraceContext";

    /// <summary>Stamps and pins the mesh span's trace context, then continues the route.</summary>
    /// <param name="context">The outbound send.</param>
    /// <param name="next">The rest of the route.</param>
    public Task HandleAsync(OutboundContext context, Func<Task> next)
    {
        if (MeshSpan.Current is { } span)
        {
            OutboundTraceContext.Pin(context, span.ToTraceparent(), span.TraceState);
        }

        return next();
    }
}

/// <summary>
/// Registered by the routing-wide <c>OutboundRoutingBuilder.UseMeshTraceContext()</c>: every outbound route
/// forwards the mesh span. <c>UseBenzeneCloudService</c>'s meshed-service self-check (M2) reads it.
/// </summary>
public sealed class MeshTraceContextForwarding
{
    /// <summary>The single instance.</summary>
    public static readonly MeshTraceContextForwarding Instance = new();

    private MeshTraceContextForwarding()
    {
    }
}
