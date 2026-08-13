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
/// </remarks>
public class InMemoryOutboxStore : IOutboxStore
{
    private sealed class Entry
    {
        public required OutboxEnvelope Envelope { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
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
                entry.LeaseUntil = now + lease;
                claimed.Add(entry.Envelope);
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

            entry.LeaseUntil = now + lease;
            return Task.FromResult<OutboxEnvelope?>(entry.Envelope);
        }
    }

    /// <inheritdoc />
    public Task MarkDispatchedAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                var now = _now();
                entry.Envelope = Rebuild(entry.Envelope, OutboxStatus.Dispatched, entry.Envelope.AttemptCount, null, entry.Envelope.LastError);
                entry.LeaseUntil = null;
                entry.DispatchedAtUtc = now;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RescheduleAsync(string id, int attemptCount, TimeSpan delay, string error, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                var now = _now();
                entry.Envelope = Rebuild(entry.Envelope, OutboxStatus.Pending, attemptCount, now + delay, error);
                entry.LeaseUntil = null;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ParkAsync(string id, string error, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Envelope = Rebuild(entry.Envelope, OutboxStatus.Parked, entry.Envelope.AttemptCount, null, error);
                entry.LeaseUntil = null;
            }
        }

        return Task.CompletedTask;
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
