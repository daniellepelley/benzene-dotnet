using Confluent.Kafka;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>Middleware pipeline context for producing a single message to Kafka.</summary>
public class KafkaSendMessageContext
{
    /// <summary>Initializes a new instance of the <see cref="KafkaSendMessageContext"/> class.</summary>
    /// <param name="topic">The Kafka topic to produce to.</param>
    /// <param name="message">The message to produce.</param>
    public KafkaSendMessageContext(string topic, Message<string, string> message)
    {
        Message = message;
        Topic = topic;
    }

    /// <summary>Gets the Kafka topic to produce to.</summary>
    public string Topic { get; }

    /// <summary>Gets the message to produce.</summary>
    public Message<string, string> Message { get; }

    /// <summary>Gets or sets the delivery result, set by <see cref="KafkaClientMiddleware"/> once the produce completes.</summary>
    public DeliveryResult<string, string> Response { get; set; }
}