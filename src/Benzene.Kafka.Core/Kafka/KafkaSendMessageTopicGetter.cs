using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages;
using Benzene.Core.Messages;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>Reads the outbound produce topic from a <see cref="KafkaSendMessageContext"/>.</summary>
public class KafkaSendMessageTopicGetter : IMessageTopicGetter<KafkaSendMessageContext>
{
    /// <inheritdoc />
    public ITopic GetTopic(KafkaSendMessageContext context)
    {
        return new Topic(context.Topic);
    }
}