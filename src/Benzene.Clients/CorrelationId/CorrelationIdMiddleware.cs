using Benzene.Abstractions;
using Benzene.Abstractions.Middleware;

namespace Benzene.Clients.CorrelationId;

/// <summary>
/// Outbound middleware that stamps the current <see cref="ICorrelationId"/> value onto
/// <see cref="OutboundContext.Headers"/>. The middleware-pipeline replacement for
/// <see cref="CorrelationIdBenzeneMessageClient"/> - see
/// <c>work/archive/benzene-clients-redesign-plan-2026-07.md</c> §2.4.
/// </summary>
public class CorrelationIdMiddleware : IMiddleware<OutboundContext>
{
    private readonly ICorrelationId _correlationId;
    private readonly string _correlationKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    /// <param name="correlationId">The correlation ID provider to read from.</param>
    /// <param name="correlationKey">
    /// The header key to stamp the correlation ID onto. Defaults to
    /// <see cref="CorrelationHeaderDefaults.HeaderKey"/> - the same default the inbound diagnostics
    /// trace tag reads, so the two directions join up without configuration.
    /// </param>
    public CorrelationIdMiddleware(ICorrelationId correlationId, string correlationKey = CorrelationHeaderDefaults.HeaderKey)
    {
        _correlationId = correlationId;
        _correlationKey = correlationKey;
    }

    /// <summary>Gets the name of this middleware.</summary>
    public string Name => nameof(CorrelationIdMiddleware);

    /// <summary>
    /// Stamps the current correlation ID onto the outbound headers, then continues the pipeline.
    /// An explicit per-call header under the same key wins: <see cref="ICorrelationId"/> always has
    /// a value (it self-generates a GUID when nothing seeded it), so unconditionally overwriting
    /// would silently replace a correlation id the caller deliberately forwarded with a random one.
    /// </summary>
    /// <param name="context">The outbound context to stamp headers onto.</param>
    /// <param name="next">The next middleware in the pipeline.</param>
    public Task HandleAsync(OutboundContext context, Func<Task> next)
    {
        if (!context.Headers.ContainsKey(_correlationKey))
        {
            context.Headers[_correlationKey] = _correlationId.Get();
        }

        return next();
    }
}
