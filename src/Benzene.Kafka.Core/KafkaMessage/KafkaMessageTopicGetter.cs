using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages;
using Benzene.Core.Messages;

namespace Benzene.Kafka.Core.KafkaMessage;

/// <summary>Reads the inbound topic (the Kafka topic the record was consumed from) from a <see cref="KafkaRecordContext{TKey,TValue}"/>.</summary>
public class KafkaMessageTopicGetter <TKey, TValue>: IMessageTopicGetter<KafkaRecordContext<TKey, TValue>>
{
    /// <inheritdoc />
    public ITopic GetTopic(KafkaRecordContext<TKey, TValue> context)
    {
        return new Topic(context.ConsumeResult.Topic);
    }
}