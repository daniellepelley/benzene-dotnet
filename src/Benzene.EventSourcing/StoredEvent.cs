using System;

namespace Benzene.EventSourcing;

/// <summary>
/// An event as read back from a stream: the envelope plus its position (<see cref="Version"/>) and
/// when it was appended. Rehydrate an aggregate by folding a stream's stored events in order.
/// </summary>
public class StoredEvent
{
    /// <summary>Initializes a new instance of the <see cref="StoredEvent"/> class.</summary>
    /// <param name="streamId">The stream the event belongs to.</param>
    /// <param name="version">The event's 1-based position in the stream.</param>
    /// <param name="eventType">The event's type discriminator.</param>
    /// <param name="payload">The serialized event body.</param>
    /// <param name="timestamp">When the event was appended.</param>
    public StoredEvent(string streamId, long version, string eventType, string payload, DateTimeOffset timestamp)
    {
        StreamId = streamId;
        Version = version;
        EventType = eventType;
        Payload = payload;
        Timestamp = timestamp;
    }

    /// <summary>The stream the event belongs to (typically the aggregate id).</summary>
    public string StreamId { get; }

    /// <summary>The event's 1-based position in the stream.</summary>
    public long Version { get; }

    /// <summary>The event's type discriminator.</summary>
    public string EventType { get; }

    /// <summary>The serialized event body.</summary>
    public string Payload { get; }

    /// <summary>When the event was appended.</summary>
    public DateTimeOffset Timestamp { get; }
}
