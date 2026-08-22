using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Confluent.Kafka;

namespace Benzene.Kafka.Core.KafkaMessage;

/// <summary>Message pipeline context wrapping a single consumed Kafka <see cref="ConsumeResult{TKey,TValue}"/>.</summary>
public class KafkaRecordContext<TKey, TValue> :  IHasMessageResult
{
    /// <summary>Initializes a new instance of the <see cref="KafkaRecordContext{TKey,TValue}"/> class.</summary>
    /// <param name="consumeResult">The consumed Kafka record.</param>
    public KafkaRecordContext(ConsumeResult<TKey, TValue> consumeResult)
    {
        ConsumeResult = consumeResult;
    }

    /// <summary>Gets the consumed Kafka record.</summary>
    public ConsumeResult<TKey, TValue> ConsumeResult { get; }

    /// <inheritdoc />
    public IBenzeneResult MessageResult { get; set; }
}