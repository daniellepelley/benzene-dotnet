using System;

namespace Benzene.EventSourcing;

/// <summary>
/// Thrown by <see cref="IEventStore.AppendAsync"/> when the stream moved since the caller last read it
/// — another writer appended events, so the optimistic-concurrency check failed. The caller should
/// re-read the stream, re-decide, and retry.
/// </summary>
public class EventStoreConcurrencyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="EventStoreConcurrencyException"/> class.</summary>
    /// <param name="streamId">The stream that conflicted.</param>
    /// <param name="expectedVersion">The version the caller expected.</param>
    /// <param name="actualVersion">The stream's actual current version.</param>
    public EventStoreConcurrencyException(string streamId, long expectedVersion, long actualVersion)
        : base($"Concurrency conflict appending to stream '{streamId}': expected version {expectedVersion} but the stream is at {actualVersion}.")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>The stream that conflicted.</summary>
    public string StreamId { get; }

    /// <summary>The version the caller expected.</summary>
    public long ExpectedVersion { get; }

    /// <summary>The stream's actual current version (-1 if unknown, e.g. a store that can't report it cheaply).</summary>
    public long ActualVersion { get; }
}
