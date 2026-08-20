using System.Collections.Concurrent;

namespace Benzene.Mesh.Dispatch;

/// <summary>
/// A fixed-window request counter, keyed by whatever the caller keys it by (an identity, a target
/// service).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this actually guarantees, stated plainly.</b> The counters live in this process. On a
/// Lambda host that means <em>per warm instance</em>: a cold start resets them, and N concurrent
/// instances keep N independent counts, so the real ceiling is N × the limit with N unbounded by
/// default. Calling this a rate limit without that sentence would be the kind of claim this codebase
/// exists to avoid.
/// </para>
/// <para>
/// It is the right tool anyway, because of what it is for. Every caller that reaches it has already
/// passed the login gate and the CSRF check, so this is not the flood defence — it is the bound on a
/// stuck retry loop, an over-enthusiastic tester, or one compromised session, whose requests land
/// overwhelmingly on warm instances at these rates. The <em>hard</em> guarantee belongs at the edge,
/// where API Gateway counts atomically across every instance and refuses before the invoke is billed.
/// A DynamoDB-backed distributed limiter would buy a stronger promise here at the cost of a table, a
/// write per dispatch, and a new failure mode on the path of a test console; that trade is not worth
/// making, and the same reasoning is already written into the refresh guard.
/// </para>
/// </remarks>
public class MeshDispatchRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Initializes a new instance of the <see cref="MeshDispatchRateLimiter"/> class.</summary>
    /// <param name="clock">Supplies the current time; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public MeshDispatchRateLimiter(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Counts one request against <paramref name="key"/> and reports whether it is within
    /// <paramref name="limit"/> for the current minute.
    /// </summary>
    /// <param name="key">The bucket — an identity, or a target service.</param>
    /// <param name="limit">Requests permitted per minute. Zero or less disables the check entirely.</param>
    /// <param name="retryAfterSeconds">Seconds until the window rolls, when refused.</param>
    /// <returns>True when the request is permitted.</returns>
    public bool TryAcquire(string key, int limit, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (limit <= 0)
        {
            return true;
        }

        var now = _clock();
        // The window start is the minute boundary, not the first request's timestamp: a rolling window
        // would need a per-key request log, and the boundary version is the one whose behaviour a
        // reader can predict from the clock on the wall.
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);

        var window = _windows.AddOrUpdate(
            key,
            _ => new Window(windowStart, 1),
            (_, existing) => existing.Start == windowStart
                ? new Window(windowStart, existing.Count + 1)
                : new Window(windowStart, 1));

        if (window.Count <= limit)
        {
            return true;
        }

        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((windowStart.AddMinutes(1) - now).TotalSeconds));
        return false;
    }

    /// <summary>
    /// Drops windows that have rolled. Called opportunistically rather than on a timer — the map is
    /// bounded by the number of distinct identities and targets a single warm instance sees, which is
    /// small, but an unbounded map on a long-lived host is a leak waiting to be found in production.
    /// </summary>
    public void Prune()
    {
        var now = _clock();
        var cutoff = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        foreach (var pair in _windows)
        {
            if (pair.Value.Start < cutoff)
            {
                _windows.TryRemove(pair.Key, out _);
            }
        }
    }

    private readonly record struct Window(DateTimeOffset Start, int Count);
}
