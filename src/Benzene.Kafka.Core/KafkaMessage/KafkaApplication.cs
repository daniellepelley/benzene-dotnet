using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Middleware;
using Confluent.Kafka;

namespace Benzene.Kafka.Core.KafkaMessage;

/// <summary>
/// Runs a single consumed Kafka record through the message pipeline, returning the handler's
/// <see cref="IBenzeneResult"/> (<c>null</c> when nothing established one - most commonly an unrouted
/// record) so the caller can settle the record on its outcome.
/// </summary>
public class KafkaApplication<TKey, TValue> : MiddlewareApplication<ConsumeResult<TKey, TValue>, KafkaRecordContext<TKey, TValue>, IBenzeneResult?>
{
    /// <summary>Initializes a new instance of the <see cref="KafkaApplication{TKey,TValue}"/> class.</summary>
    /// <param name="pipeline">The message pipeline each consumed record is dispatched through.</param>
    public KafkaApplication(IMiddlewarePipeline<KafkaRecordContext<TKey, TValue>> pipeline)
        :base(new TransportMiddlewarePipeline<KafkaRecordContext<TKey, TValue>>(TransportNames.Kafka, pipeline),
            @event => new KafkaRecordContext<TKey, TValue>(@event),
            context => context.MessageResult)
    { }
}
