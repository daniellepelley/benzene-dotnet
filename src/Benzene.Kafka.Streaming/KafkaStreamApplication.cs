using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.Middleware;
using Confluent.Kafka;

namespace Benzene.Kafka.Streaming;

/// <summary>
/// Runs one accumulated Kafka batch through the streaming pipeline as a single
/// <see cref="StreamContext{TItem}"/> (fan-in): the whole window is exposed as one ordered
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="ConsumeResult{TKey,TValue}"/>, processed by one
/// pipeline run in one DI scope — the same shape as <c>KinesisStreamApplication</c> and
/// <c>CosmosChangeFeedApplication</c>. Handlers window and re-order the batch with the stream
/// operators (<c>Window(n)</c>, <c>PartitionBy(r =&gt; r.TopicPartition)</c>) rather than receiving
/// one context per record, which is what the per-record <c>BenzeneKafkaWorker</c> does.
/// </summary>
/// <remarks>
/// <c>HandleAsync</c> returns whether the handler checkpointed anything, so
/// <see cref="BenzeneKafkaStreamWorker{TKey,TValue}"/> can decide auto-checkpoint behavior.
/// A pipeline exception is deliberately <em>not</em> caught here (matching
/// <c>CosmosChangeFeedApplication</c>, unlike <c>KinesisStreamApplication</c>): the worker owns the
/// retry-vs-skip decision because only it can seek the consumer back.
/// </remarks>
/// <typeparam name="TKey">The consumer's key type.</typeparam>
/// <typeparam name="TValue">The consumer's value type.</typeparam>
public class KafkaStreamApplication<TKey, TValue>
    : StreamMiddlewareApplication<KafkaStreamBatch<TKey, TValue>, ConsumeResult<TKey, TValue>, bool>
{
    /// <summary>
    /// The <see cref="StreamContext{TItem}.Metadata"/> key holding the batch's distinct
    /// <see cref="TopicPartition"/>s, in first-seen order — the Kafka counterpart of Cosmos's lease
    /// token, for a handler that wants to know which partitions a window spans without walking it.
    /// </summary>
    public const string TopicPartitionsMetadataKey = "kafka.topicPartitions";

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaStreamApplication{TKey,TValue}"/> class.
    /// </summary>
    /// <param name="pipeline">The built stream pipeline to run each batch through.</param>
    public KafkaStreamApplication(IMiddlewarePipeline<StreamContext<ConsumeResult<TKey, TValue>>> pipeline)
        : base(
            new TransportMiddlewarePipeline<StreamContext<ConsumeResult<TKey, TValue>>>(TransportNames.Kafka, pipeline),
            BuildContext,
            context => ((KafkaStreamCheckpointer<TKey, TValue>)context.Checkpointer).HasCheckpointed)
    { }

    private static StreamContext<ConsumeResult<TKey, TValue>> BuildContext(KafkaStreamBatch<TKey, TValue> batch)
    {
        return new StreamContext<ConsumeResult<TKey, TValue>>(
            ToAsyncEnumerable(batch.Records),
            checkpointer: batch.Checkpointer,
            cancellationToken: batch.CancellationToken,
            metadata: new Dictionary<string, object> { [TopicPartitionsMetadataKey] = batch.TopicPartitions });
    }

    private static async IAsyncEnumerable<ConsumeResult<TKey, TValue>> ToAsyncEnumerable(
        IReadOnlyList<ConsumeResult<TKey, TValue>> records)
    {
        foreach (var record in records)
        {
            yield return record;
        }

        await Task.CompletedTask;
    }
}
