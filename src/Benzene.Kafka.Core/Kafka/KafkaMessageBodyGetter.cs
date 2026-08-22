using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>Reads the outbound message body from a <see cref="KafkaSendMessageContext"/>.</summary>
public class KafkaSendMessageBodyGetter : IMessageBodyGetter<KafkaSendMessageContext>
{
    /// <inheritdoc />
    public string? GetBody(KafkaSendMessageContext context)
    {
        return context.Message.Value;
    }
}