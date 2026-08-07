namespace Benzene.EventSourcing;

/// <summary>
/// An event to append to a stream, in its serialized form. The store is deliberately
/// serialization-agnostic: the caller decides how a domain event becomes <see cref="Payload"/>
/// (JSON, MessagePack, Avro), and how to turn it back on read.
/// </summary>
public class EventEnvelope
{
    /// <summary>Initializes a new instance of the <see cref="EventEnvelope"/> class.</summary>
    /// <param name="eventType">A stable discriminator for the event's type (used to deserialize on read).</param>
    /// <param name="payload">The serialized event body.</param>
    public EventEnvelope(string eventType, string payload)
    {
        EventType = eventType;
        Payload = payload;
    }

    /// <summary>A stable discriminator for the event's type.</summary>
    public string EventType { get; }

    /// <summary>The serialized event body.</summary>
    public string Payload { get; }
}
