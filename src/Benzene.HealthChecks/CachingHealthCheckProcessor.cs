using System.Collections.Concurrent;
using Benzene.Abstractions.Results;
using Benzene.HealthChecks.Core;

namespace Benzene.HealthChecks;

/// <summary>
/// An <see cref="IHealthCheckProcessor"/> decorator that caches the aggregated result for a short TTL,
/// so a busy load balancer or Kubernetes probe polling every few seconds does not re-run every check
/// (and re-hit every external dependency) on every request. Opt-in: register this as the
/// <see cref="IHealthCheckProcessor"/> wrapping a <see cref="HealthCheckProcessor"/> - do NOT use it
/// for a liveness probe that must reflect the instant state.
/// </summary>
/// <remarks>
/// The cache is keyed by the set of check <see cref="IHealthCheck.Type"/>s, so different probes
/// (e.g. liveness vs readiness) that run different check sets cache independently rather than sharing
/// one stale entry. A cold-cache (or just-expired) race is single-flighted: concurrent callers for the
/// same key share one in-flight run of the inner processor rather than each triggering their own - so
/// the inner checks (and whatever external dependencies they hit) run exactly once per cache miss, no
/// matter how many callers arrive while that run is in progress. The in-flight entry is removed as soon
/// as its run completes (success or failure), so a faulted run never poisons later calls and the next
/// cache miss after TTL expiry always starts a fresh single-flight window.
/// </remarks>
public class CachingHealthCheckProcessor : IHealthCheckProcessor
{
    private readonly IHealthCheckProcessor _inner;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, (DateTime CachedAt, IBenzeneResult Result)> _cache = new();

    // Per-key single-flight guard: concurrent callers that miss the cache for the same key share one
    // in-flight Task<IBenzeneResult> instead of each running the inner processor themselves.
    // LazyThreadSafetyMode.ExecutionAndPublication guarantees the factory (which starts the inner run)
    // executes exactly once even under concurrent GetOrAdd races.
    private readonly ConcurrentDictionary<string, Lazy<Task<IBenzeneResult>>> _inFlight = new();

    /// <summary>Initializes a new instance.</summary>
    /// <param name="inner">The processor that actually runs the checks on a cache miss.</param>
    /// <param name="ttl">How long an aggregated result is served from cache before the checks are re-run.</param>
    public CachingHealthCheckProcessor(IHealthCheckProcessor inner, TimeSpan ttl)
        : this(inner, ttl, () => DateTime.UtcNow)
    {
    }

    /// <summary>Initializes a new instance with an injectable clock (for testing).</summary>
    /// <param name="inner">The processor that actually runs the checks on a cache miss.</param>
    /// <param name="ttl">How long an aggregated result is served from cache before the checks are re-run.</param>
    /// <param name="now">The clock used to age cache entries.</param>
    public CachingHealthCheckProcessor(IHealthCheckProcessor inner, TimeSpan ttl, Func<DateTime> now)
    {
        _inner = inner;
        _ttl = ttl;
        _now = now;
    }

    /// <inheritdoc />
    public async Task<IBenzeneResult> PerformHealthChecksAsync(IHealthCheck[] healthChecks)
    {
        var key = string.Join(",", healthChecks.Select(x => x.Type).OrderBy(x => x, StringComparer.Ordinal));

        if (_cache.TryGetValue(key, out var entry) && _now() - entry.CachedAt < _ttl)
        {
            return entry.Result;
        }

        // Single-flight: every caller that misses the cache for this key awaits the SAME Lazy<Task<...>>,
        // so the inner processor runs exactly once no matter how many callers arrive concurrently.
        var inFlight = _inFlight.GetOrAdd(key,
            _ => new Lazy<Task<IBenzeneResult>>(() => RunAndCacheAsync(key, healthChecks), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await inFlight.Value;
        }
        finally
        {
            // Remove the in-flight entry once its run has settled (success or failure) - via the atomic
            // KeyValuePair overload so this only removes the entry THIS call created/observed, never a
            // newer one added by a later cache-miss. This is what lets a faulted run be retried on the
            // next call instead of poisoning every future call with the same cached exception, and what
            // gives the next cache miss after TTL expiry a fresh single-flight window instead of forever
            // replaying this one.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<IBenzeneResult>>>(key, inFlight));
        }
    }

    private async Task<IBenzeneResult> RunAndCacheAsync(string key, IHealthCheck[] healthChecks)
    {
        var result = await _inner.PerformHealthChecksAsync(healthChecks);
        _cache[key] = (_now(), result);
        return result;
    }
}
