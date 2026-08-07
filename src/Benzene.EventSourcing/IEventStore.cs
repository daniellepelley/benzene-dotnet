using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Benzene.EventSourcing;

/// <summary>
/// An append-only, ordered event log, keyed by stream (typically one stream per aggregate). The store
/// is the source of truth; an aggregate's state is a fold of its stream's events. Benzene deliberately
/// keeps this surface tiny and unopinionated — rehydration, snapshots, and replay are composed on top.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Appends <paramref name="events"/> to <paramref name="streamId"/> with optimistic concurrency:
    /// the append succeeds only if the stream's current version equals <paramref name="expectedVersion"/>
    /// (0 for a new stream). Concurrent writers therefore cannot interleave a stream's history.
    /// </summary>
    /// <param name="streamId">The stream to append to (typically the aggregate id).</param>
    /// <param name="expectedVersion">The version the caller last saw (0 = expect an empty/new stream).</param>
    /// <param name="events">The events to append, in order.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stream's new version after the append.</returns>
    /// <exception cref="EventStoreConcurrencyException">The stream's current version was not <paramref name="expectedVersion"/>.</exception>
    Task<long> AppendAsync(string streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a stream's events in order, from <paramref name="fromVersion"/> exclusive (0 = from the
    /// start). An empty stream returns no events.
    /// </summary>
    /// <param name="streamId">The stream to read.</param>
    /// <param name="fromVersion">Read events after this version (0 = from the start; use a snapshot's version to read forward).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stream's events in ascending version order.</returns>
    Task<IReadOnlyList<StoredEvent>> ReadAsync(string streamId, long fromVersion = 0, CancellationToken cancellationToken = default);
}
