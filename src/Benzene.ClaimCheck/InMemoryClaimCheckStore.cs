namespace Benzene.ClaimCheck;

/// <summary>
/// An in-process <see cref="IClaimCheckStore"/> backed by a dictionary, suitable for a single worker
/// instance, tests, and local development. Issues references with the reserved <c>memory</c> scheme
/// (<c>memory://{topic}/{key}</c>).
/// </summary>
/// <remarks>
/// <para>
/// State lives in this process only. In a multi-instance deployment each instance keeps its own map,
/// so a payload offloaded by one instance is invisible to another - use a shared/durable store there
/// (e.g. the S3 or Azure Blob Storage claim-check store packages). Entries are held for a
/// configurable time-to-live.
/// </para>
/// <para>
/// <b>Reclamation, deliberately without a background thread:</b> an entry is removed from the backing
/// dictionary in two ways. (1) <see cref="GetAsync"/> removes an entry it finds expired at read time,
/// so a re-read of the same reference never resurrects it. (2) Because a payload that is never read
/// back at all (a fan-out sibling nobody consumes, an undelivered message) would otherwise sit in the
/// dictionary forever, <see cref="PutAsync"/> also runs a sweep - purging every entry whose
/// <c>ExpiresAt</c> has passed - but only at most once per <see cref="SweepInterval"/>, tracked via the
/// last-swept timestamp, so a busy producer does not pay a full-dictionary scan on every put. A
/// background timer was considered and rejected: a dev/single-worker store should not own a thread,
/// and sweeping on put bounds unbounded growth wherever that growth actually originates (puts), without
/// one. Until its next sweep (or a read) reclaims it, an expired entry that is never read back still
/// occupies memory - this is a bound on growth, not an eviction latency guarantee.
/// </para>
/// </remarks>
public class InMemoryClaimCheckStore : IClaimCheckStore
{
    /// <summary>The reference scheme this store issues and accepts: <c>memory</c>.</summary>
    public const string Scheme = "memory";

    /// <summary>The minimum time between two expired-entry sweeps triggered by <see cref="PutAsync"/>.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private sealed class Entry
    {
        public required string Body { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _gate = new();
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _lastSweptAt = DateTimeOffset.MinValue;

    // Test-only seam (visible to Benzene.Test via InternalsVisibleTo): lets tests assert entries are
    // actually reclaimed rather than merely reporting null, without a public API for it.
    internal int EntryCount
    {
        get { lock (_gate) { return _entries.Count; } }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryClaimCheckStore"/> class.
    /// </summary>
    /// <param name="timeToLive">How long a stored payload is retained. Defaults to 24 hours.</param>
    /// <param name="now">A clock, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryClaimCheckStore(TimeSpan? timeToLive = null, Func<DateTimeOffset>? now = null)
    {
        _timeToLive = timeToLive ?? TimeSpan.FromHours(24);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task<string> PutAsync(string body, ClaimCheckPutContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = Guid.NewGuid().ToString("n");
        var reference = $"{Scheme}://{Uri.EscapeDataString(context.Topic)}/{key}";

        lock (_gate)
        {
            _entries[reference] = new Entry { Body = body, ExpiresAt = _now() + _timeToLive };
            SweepExpired_NoLock();
        }

        return Task.FromResult(reference);
    }

    /// <inheritdoc />
    /// <exception cref="ClaimCheckStoreMismatchException">
    /// <paramref name="reference"/> does not use the <see cref="Scheme"/> this store issues.
    /// </exception>
    public Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!reference.StartsWith(Scheme + "://", StringComparison.Ordinal))
        {
            throw new ClaimCheckStoreMismatchException(reference);
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(reference, out var entry))
            {
                if (entry.ExpiresAt > _now())
                {
                    return Task.FromResult<string?>(entry.Body);
                }

                // Found expired: reclaim it now rather than leaving it for the next put's sweep, so a
                // re-read of the same reference can never resurrect it.
                _entries.Remove(reference);
            }
        }

        return Task.FromResult<string?>(null);
    }

    // Purges every entry whose ExpiresAt has passed, at most once per SweepInterval (tracked via
    // _lastSweptAt) so a busy producer does not pay a full-dictionary scan on every put. Caller must
    // hold _gate.
    private void SweepExpired_NoLock()
    {
        var now = _now();
        if (now - _lastSweptAt < SweepInterval)
        {
            return;
        }

        _lastSweptAt = now;

        List<string>? expiredKeys = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                (expiredKeys ??= new List<string>()).Add(pair.Key);
            }
        }

        if (expiredKeys == null)
        {
            return;
        }

        foreach (var key in expiredKeys)
        {
            _entries.Remove(key);
        }
    }
}
