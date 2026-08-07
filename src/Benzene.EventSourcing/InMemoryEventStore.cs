using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Benzene.EventSourcing;

/// <summary>
/// An in-memory <see cref="IEventStore"/> for a single process (tests, or a single-host service). Not
/// durable and not shared across instances — use a distributed store (e.g. the DynamoDB event store)
/// in a fleet. Appends are serialized under a lock so the optimistic-concurrency check is atomic.
/// </summary>
public class InMemoryEventStore : IEventStore
{
    private readonly Dictionary<string, List<StoredEvent>> _streams = new();
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Initializes a new instance of the <see cref="InMemoryEventStore"/> class.</summary>
    /// <param name="now">Clock, injectable for testing. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryEventStore(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public Task<long> AppendAsync(string streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                stream = new List<StoredEvent>();
                _streams[streamId] = stream;
            }

            var current = stream.Count == 0 ? 0 : stream[^1].Version;
            if (current != expectedVersion)
            {
                throw new EventStoreConcurrencyException(streamId, expectedVersion, current);
            }

            var now = _now();
            var version = current;
            foreach (var e in events)
            {
                version++;
                stream.Add(new StoredEvent(streamId, version, e.EventType, e.Payload, now));
            }

            return Task.FromResult(version);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredEvent>> ReadAsync(string streamId, long fromVersion = 0, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                return Task.FromResult<IReadOnlyList<StoredEvent>>(Array.Empty<StoredEvent>());
            }

            IReadOnlyList<StoredEvent> result = stream.Where(e => e.Version > fromVersion).ToList();
            return Task.FromResult(result);
        }
    }
}
