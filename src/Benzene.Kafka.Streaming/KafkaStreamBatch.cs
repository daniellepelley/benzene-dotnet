using Confluent.Kafka;

namespace Benzene.Kafka.Streaming;

/// <summary>
/// One accumulated window of consumed Kafka records: the raw "event"
/// <see cref="KafkaStreamApplication{TKey,TValue}"/> maps into a
/// <c>StreamContext&lt;ConsumeResult&lt;TKey,TValue&gt;&gt;</c>, mirroring
/// <c>CosmosChangeFeedBatch&lt;TDocument&gt;</c>. Unlike Cosmos (where the SDK hands the worker a
/// batch), the batch is assembled by <see cref="BenzeneKafkaStreamWorker{TKey,TValue}"/>'s own
/// consume loop once either <see cref="KafkaStreamOptions.MaxBatchSize"/> or
/// <see cref="KafkaStreamOptions.MaxBatchWait"/> is reached.
/// </summary>
/// <typeparam name="TKey">The consumer's key type.</typeparam>
/// <typeparam name="TValue">The consumer's value type.</typeparam>
public class KafkaStreamBatch<TKey, TValue>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaStreamBatch{TKey,TValue}"/> class.
    /// </summary>
    /// <param name="records">The batch's records, in the order they were consumed.</param>
    /// <param name="checkpointer">The batch's per-partition offset checkpointer.</param>
    /// <param name="cancellationToken">The worker's shutdown token, surfaced to the handler.</param>
    public KafkaStreamBatch(IReadOnlyList<ConsumeResult<TKey, TValue>> records,
        KafkaStreamCheckpointer<TKey, TValue> checkpointer, CancellationToken cancellationToken)
    {
        Records = records;
        Checkpointer = checkpointer;
        CancellationToken = cancellationToken;
    }

    /// <summary>The batch's records, in the order they were consumed (interleaved across partitions).</summary>
    public IReadOnlyList<ConsumeResult<TKey, TValue>> Records { get; }

    /// <summary>The batch's per-partition offset checkpointer.</summary>
    public KafkaStreamCheckpointer<TKey, TValue> Checkpointer { get; }

    /// <summary>The worker's shutdown token, so a long-running handler can observe stop requests.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// The distinct topic-partitions the batch's records came from, in first-seen order — surfaced
    /// to the pipeline via <see cref="KafkaStreamApplication{TKey,TValue}.TopicPartitionsMetadataKey"/>.
    /// </summary>
    public IReadOnlyList<TopicPartition> TopicPartitions =>
        Records.Where(record => !string.IsNullOrEmpty(record?.Topic))
            .Select(record => record.TopicPartition)
            .Distinct()
            .ToList();
}
