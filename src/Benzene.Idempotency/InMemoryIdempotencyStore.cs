namespace Benzene.Idempotency;

/// <summary>
/// An in-process <see cref="IIdempotencyStore"/> backed by a dictionary, suitable for a single
/// worker instance, tests, and local development.
/// </summary>
/// <remarks>
/// State lives in this process only. In a multi-instance deployment each instance keeps its own map,
/// so a duplicate redelivered to a different instance is NOT de-duplicated — use a shared store
/// (e.g. Redis) there. Records are held for a configurable time-to-live and expired lazily on the
/// next access to a key.
/// <para>
/// <b>Claim fencing.</b> Every winning <see cref="TryClaimAsync"/> mints a fresh opaque
/// <see cref="ClaimResult.ClaimToken"/>. <see cref="CompleteAsync"/>/<see cref="ReleaseAsync"/>
/// compare the presented token against the live entry under the same lock the claim itself uses, and
/// refuse (return <see langword="false"/>, write nothing) when it doesn't match a still-in-progress,
/// unexpired entry - the case where this caller's claim already lapsed and a different caller won a
/// fresh claim on the same key. This closes the stale-writer-clobbers-the-new-holder hole a bare
/// key-only settle API would have.
/// </para>
/// </remarks>
public class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed class Entry
    {
        public IdempotencyStatus Status { get; init; }
        public bool WasSuccessful { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public string? ClaimToken { get; init; }
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _gate = new();
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryIdempotencyStore"/> class.
    /// </summary>
    /// <param name="timeToLive">How long a record is retained. Defaults to 24 hours.</param>
    /// <param name="now">
    /// A clock, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    public InMemoryIdempotencyStore(TimeSpan? timeToLive = null, Func<DateTimeOffset>? now = null)
    {
        _timeToLive = timeToLive ?? TimeSpan.FromHours(24);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task<ClaimResult> TryClaimAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = _now();
            if (_entries.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
            {
                var record = new IdempotencyRecord(key, existing.Status, existing.WasSuccessful);
                return Task.FromResult(ClaimResult.AlreadyExists(record));
            }

            var claimToken = Guid.NewGuid().ToString();
            _entries[key] = new Entry
            {
                Status = IdempotencyStatus.InProgress,
                ExpiresAt = now + _timeToLive,
                ClaimToken = claimToken
            };
            return Task.FromResult(ClaimResult.Won(claimToken));
        }
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(string key, string claimToken, bool wasSuccessful, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!IsLiveClaim(key, claimToken, _now()))
            {
                // The claim lapsed and was reclaimed by another worker, or was already settled -
                // refuse the write rather than clobbering whoever holds the claim now.
                return Task.FromResult(false);
            }

            _entries[key] = new Entry
            {
                Status = IdempotencyStatus.Completed,
                WasSuccessful = wasSuccessful,
                ExpiresAt = _now() + _timeToLive,
                ClaimToken = claimToken
            };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!IsLiveClaim(key, claimToken, _now()))
            {
                return Task.FromResult(false);
            }

            _entries.Remove(key);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Whether <paramref name="key"/> currently has a live, still-<see cref="IdempotencyStatus.InProgress"/>
    /// claim whose token is <paramref name="claimToken"/>. Must be called under <see cref="_gate"/>.
    /// The same liveness definition <see cref="TryClaimAsync"/> uses to decide whether an existing
    /// record blocks a new claim (unexpired), plus the token match that makes a settle call fenced.
    /// </summary>
    private bool IsLiveClaim(string key, string claimToken, DateTimeOffset now)
        => _entries.TryGetValue(key, out var entry)
            && entry.Status == IdempotencyStatus.InProgress
            && entry.ExpiresAt > now
            && entry.ClaimToken == claimToken;
}
