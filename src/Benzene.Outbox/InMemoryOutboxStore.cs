namespace Benzene.Outbox;

/// <summary>
/// An in-process <see cref="IOutboxStore"/> backed by a dictionary, suitable for a single worker
/// instance, tests, and local development.
/// </summary>
/// <remarks>
/// State lives in this process only. In a multi-instance deployment each instance keeps its own map,
/// so envelopes captured on one instance are only ever dispatched by that same instance - use a
/// shared store (e.g. <c>Benzene.Outbox.DynamoDb</c>, Phase 2) for a fleet. This store also cannot
/// provide the transactional (<see cref="OutboxWriteMode.Transactional"/>) atomic-commit story -
/// that needs a store that shares a transaction with the application's own state write.
/// <para>
/// <b>Claim fencing.</b> Every successful <see cref="ClaimDueAsync"/>/<see cref="ClaimAsync"/> mints a
/// fresh lease token, stored on the entry and stamped onto the returned <see cref="OutboxEnvelope.LeaseToken"/>.
/// <see cref="MarkDispatchedAsync"/>/<see cref="RescheduleAsync"/>/<see cref="ParkAsync"/> compare the
/// presented token against the entry's current one under the same lock the claim itself uses, and
/// refuse (return <see langword="false"/>, write nothing) on a mismatch - see
/// <see cref="IOutboxStore.MarkDispatchedAsync"/>'s remarks for what this closes.
/// </para>
/// </remarks>
public class InMemoryOutboxStore : IOutboxStore
{
    private sealed class Entry
    {
        public required OutboxEnvelope Envelope { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
        public string? LeaseToken { get; set; }
        public DateTimeOffset? DispatchedAtUtc { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryOutboxStore"/> class.
    /// </summary>
    /// <param name="now">A clock, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryOutboxStore(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task AddAsync(IEnumerable<OutboxEnvelope> envelopes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (var envelope in envelopes)
            {
                _entries[envelope.Id] = new Entry { Envelope = envelope };
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEnvelope>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = _now();
            var due = _entries.Values
                .Where(entry => IsDue(entry, now))
                .OrderBy(entry => entry.Envelope.CreatedAtUtc)
                .Take(batchSize)
                .ToList();

            var claimed = new List<OutboxEnvelope>(due.Count);
            foreach (var entry in due)
            {
                var token = Guid.NewGuid().ToString();
                entry.LeaseUntil = now + lease;
                entry.LeaseToken = token;
                claimed.Add(entry.Envelope.WithLeaseToken(token));
            }

            return Task.FromResult<IReadOnlyList<OutboxEnvelope>>(claimed);
        }
    }

    /// <inheritdoc />
    public Task<OutboxEnvelope?> ClaimAsync(string id, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = _now();
            if (!_entries.TryGetValue(id, out var entry) || !IsDue(entry, now))
            {
                return Task.FromResult<OutboxEnvelope?>(null);
            }

            var token = Guid.NewGuid().ToString();
            entry.LeaseUntil = now + lease;
            entry.LeaseToken = token;
            return Task.FromResult<OutboxEnvelope?>(entry.Envelope.WithLeaseToken(token));
        }
    }

    /// <inheritdoc />
    public Task<bool> MarkDispatchedAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!IsCurrentLeaseHolder(id, leaseToken, out var entry))
            {
                return Task.FromResult(false);
            }

            var now = _now();
            entry.Envelope = Rebuild(entry.Envelope, OutboxStatus.Dispatched, entry.Envelope.AttemptCount, null, entry.Envelope.LastError);
            entry.LeaseUntil = null;
            entry.LeaseToken = null;
            entry.DispatchedAtUtc = now;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> RescheduleAsync(string id, int attemptCount, TimeSpan delay, string error, string leaseToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!IsCurrentLeaseHolder(id, leaseToken, out var entry))
            {
                return Task.FromResult(false);
            }

            var now = _now();
            entry.Envelope = Rebuild(entry.Envelope, OutboxStatus.Pending, attemptCount, now + delay, error);
            entry.LeaseUntil = null;
            entry.LeaseToken = null;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> ParkAsync(string id, string error, string leaseToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!IsCurrentLeaseHolder(id, leaseToken, out var entry))
            {
                return Task.FromResult(false);
            }

            entry.Envelope = Rebuild(entry.Envelope, OutboxStatus.Parked, entry.Envelope.AttemptCount, null, error);
            entry.LeaseUntil = null;
            entry.LeaseToken = null;
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> exists and its current lease token is <paramref name="leaseToken"/>.
    /// Must be called under <see cref="_gate"/>. This is the fencing check every settle method uses -
    /// see <see cref="IOutboxStore.MarkDispatchedAsync"/>'s remarks.
    /// </summary>
    private bool IsCurrentLeaseHolder(string id, string leaseToken, out Entry entry)
    {
        if (_entries.TryGetValue(id, out var found) && found.LeaseToken == leaseToken)
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    /// <inheritdoc />
    public Task<int> DeleteDispatchedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var toRemove = _entries
                .Where(kvp => kvp.Value.Envelope.Status == OutboxStatus.Dispatched
                              && kvp.Value.DispatchedAtUtc != null
                              && kvp.Value.DispatchedAtUtc < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _entries.Remove(key);
            }

            return Task.FromResult(toRemove.Count);
        }
    }

    private static bool IsDue(Entry entry, DateTimeOffset now)
    {
        return entry.Envelope.Status == OutboxStatus.Pending
            && (entry.Envelope.NextAttemptAtUtc == null || entry.Envelope.NextAttemptAtUtc <= now)
            && (entry.LeaseUntil == null || entry.LeaseUntil <= now);
    }

    private static OutboxEnvelope Rebuild(OutboxEnvelope source, OutboxStatus status, int attemptCount, DateTimeOffset? nextAttemptAtUtc, string? lastError)
    {
        return new OutboxEnvelope(
            source.Id, source.Topic, source.Payload, source.PayloadType, source.Headers, source.CreatedAtUtc,
            attemptCount, nextAttemptAtUtc, status, lastError);
    }
}
