using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Core.MessageHandlers;

namespace Benzene.Kafka.Core.KafkaMessage;

/// <summary>Records the handler's outcome onto a <see cref="KafkaRecordContext{TKey,TValue}"/>.</summary>
public class KafkaMessageHandlerResultSetter<TKey, TValue> : IMessageHandlerResultSetter<KafkaRecordContext<TKey, TValue>>
{
    /// <inheritdoc />
    public Task SetResultAsync(KafkaRecordContext<TKey, TValue> context, IMessageHandlerResult messageHandlerResult)
    {
        context.MessageResult = messageHandlerResult.BenzeneResult;
        return Task.CompletedTask;
    }
}