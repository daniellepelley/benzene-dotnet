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
    // Mirrors DynamoDbEventStore's MaxEventsPerAppend so app code written against either store sees
    // the same limit; the in-memory store has no transaction-size constraint of its own to enforce.
    private const int MaxEventsPerAppend = 100;

    private readonly Dictionary<string, List<StoredEvent>> _streams = new();
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Initializes a new instance of the <see cref="InMemoryEventStore"/> class.</summary>
    /// <param name="now">Clock, injectable for testing. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryEventStore(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public Task<long> AppendAsync(string streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (expectedVersion < 0)
        {
            // Mirrors DynamoDbEventStore's guard (round 11's #121 fix) - without it, a negative
            // expectedVersion falls through to a mismatched-version EventStoreConcurrencyException
            // instead, a test-vs-prod divergence in exception type for the same caller mistake.
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), expectedVersion, "Expected version cannot be negative.");
        }

        if (events.Count > MaxEventsPerAppend)
        {
            throw new ArgumentException(
                $"Cannot append {events.Count} events in a single call; the maximum is {MaxEventsPerAppend}. Split the append.",
                nameof(events));
        }

        lock (_gate)
        {
            _streams.TryGetValue(streamId, out var stream);
            var current = stream is null || stream.Count == 0 ? 0 : stream[^1].Version;
            if (current != expectedVersion)
            {
                throw new EventStoreConcurrencyException(streamId, expectedVersion, current);
            }

            // Build the new events in a local list first, and only splice them into the stream (and
            // register a brand-new stream in the dictionary) once the whole batch has been built
            // without error — so a mid-batch failure (e.g. a null event) can never leave a partial
            // append visible to readers.
            var now = _now();
            var version = current;
            var toAppend = new List<StoredEvent>(events.Count);
            foreach (var e in events)
            {
                version++;
                toAppend.Add(new StoredEvent(streamId, version, e.EventType, e.Payload, now));
            }

            if (stream is null)
            {
                stream = new List<StoredEvent>();
                _streams[streamId] = stream;
            }

            stream.AddRange(toAppend);
            return Task.FromResult(version);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredEvent>> ReadAsync(string streamId, long fromVersion = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
