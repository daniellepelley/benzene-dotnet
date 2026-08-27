using System.Threading.RateLimiting;
using Benzene.Abstractions.DI;
using Microsoft.Extensions.Logging;

namespace Benzene.RateLimiting;

/// <summary>
/// Best-effort, per-instance, <b>per-partition</b> rate limiting over a
/// <see cref="PartitionedRateLimiter{TContext}"/>: each caller (however the limiter's own
/// partitioner keys them — by IP, API key, tenant, ...) draws from its own share of permits,
/// instead of every caller sharing one limiter (see <see cref="RateLimitingMiddleware{TContext}"/>,
/// where one abusive caller can exhaust the whole budget for everyone else). Otherwise behaves
/// identically to <see cref="RateLimitingMiddleware{TContext}"/>: no queuing, the lease is held
/// across <c>next()</c>, and a rejection short-circuits with <c>TooManyRequests</c>.
/// </summary>
/// <remarks>
/// <para>
/// The partitioner itself is baked into <paramref name="partitionedLimiter"/> when the caller
/// constructs it (via <see cref="PartitionedRateLimiter.Create{TResource,TKey}"/>) — this
/// middleware only calls <see cref="PartitionedRateLimiter{TResource}.AttemptAcquire"/> with the
/// message's own <typeparamref name="TContext"/> as the resource, exactly as the partitioner
/// expects. That keeps the partition-key extraction next to the caller's own knowledge of their
/// transport (an HTTP context's client IP, an API key header, a tenant claim, ...) rather than
/// this package inventing a one-size-fits-all key shape.
/// </para>
/// <para>
/// <b>Honesty rule:</b> a client-supplied partition key (an API key, a tenant id from an
/// unauthenticated claim) is spoofable — a caller who can vary it can still get a fresh share
/// each time. It is still strictly better than no partitioning at all: it costs an attacker
/// active effort to defeat, rather than costing them nothing (today, one accidental retry storm
/// from a single caller exhausts the shared bucket for every other caller with zero effort). A
/// key derived from something the caller can't freely choose — an authenticated identity, the
/// peer IP a trusted proxy set — is not spoofable this way.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The transport-specific context type, also the limiter's partition resource type.</typeparam>
public class PartitionedRateLimitingMiddleware<TContext> : RateLimitingMiddlewareBase<TContext>, IAsyncDisposable
    where TContext : class
{
    private readonly PartitionedRateLimiter<TContext> _partitionedLimiter;
    private readonly Func<TContext, string?>? _partitionKeyForLogging;
    private readonly bool _ownsLimiter;

    /// <summary>Initializes the middleware over a caller-supplied partitioned limiter and a per-message permit cost.</summary>
    /// <param name="partitionedLimiter">
    /// The partitioned limiter, with its partition-key selector already baked in via
    /// <see cref="PartitionedRateLimiter.Create{TResource,TKey}"/>. Shared across every message on
    /// the pipeline.
    /// </param>
    /// <param name="permitCost">Computes the permit cost of the current message (e.g. 1, or the payload size in bytes).</param>
    /// <param name="serviceResolver">The current message's scope, used to compute the cost and write the rejection result.</param>
    /// <param name="partitionKeyForLogging">
    /// Optional. Since <paramref name="partitionedLimiter"/> does not expose the partition key it
    /// derived, supply the same extraction here purely so a rejection's log line can name which
    /// partition was throttled (see #138 in <c>work/outstanding-bugs.md</c>).
    /// </param>
    /// <param name="ownsLimiter">
    /// Whether this middleware owns <paramref name="partitionedLimiter"/>'s disposal. Defaults to
    /// <c>false</c> — a partitioned limiter is always caller-supplied (there is no built-in
    /// convenience entry point for it, since the partition key is inherently caller-specific), so
    /// its disposal belongs to the caller unless they explicitly opt in.
    /// </param>
    /// <param name="logger">Optional; logs a warning naming the limiter, partition, and cost when a message is rejected.</param>
    public PartitionedRateLimitingMiddleware(PartitionedRateLimiter<TContext> partitionedLimiter,
        Func<IServiceResolver, TContext, int> permitCost, IServiceResolver serviceResolver,
        Func<TContext, string?>? partitionKeyForLogging = null, bool ownsLimiter = false,
        ILogger<PartitionedRateLimitingMiddleware<TContext>>? logger = null)
        : base(permitCost, serviceResolver, logger)
    {
        _partitionedLimiter = partitionedLimiter;
        _partitionKeyForLogging = partitionKeyForLogging;
        _ownsLimiter = ownsLimiter;
    }

    /// <inheritdoc />
    public override string Name => "PartitionedRateLimiting";

    /// <inheritdoc />
    protected override RateLimitLease Acquire(TContext context, int cost) =>
        _partitionedLimiter.AttemptAcquire(context, cost);

    /// <inheritdoc />
    protected override string LimiterDescription(TContext context)
    {
        var key = _partitionKeyForLogging?.Invoke(context);
        return key != null ? $"partitioned, partition={key}" : "partitioned";
    }

    /// <summary>Disposes the limiter when <c>ownsLimiter</c> was set; a no-op otherwise (see the constructor's remarks).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_ownsLimiter)
        {
            await _partitionedLimiter.DisposeAsync();
        }
    }
}
