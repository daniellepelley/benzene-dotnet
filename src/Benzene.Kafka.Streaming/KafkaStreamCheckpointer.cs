using Benzene.Core.Middleware;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Benzene.Kafka.Streaming;

/// <summary>
/// The Kafka streaming binding's <see cref="IStreamCheckpointer{TItem}"/>: one monotonic,
/// never-rewinding offset watermark <em>per topic-partition</em> in the batch, written through to
/// the consumer with <c>StoreOffset</c> and made durable with an explicit <c>Commit</c> at the end
/// of the batch.
/// </summary>
/// <remarks>
/// <para><strong>Why per-partition, and what "checkpoint this record" therefore means.</strong>
/// Kinesis's checkpointer keeps a single watermark because a Lambda Kinesis batch comes from one
/// shard and its retry contract is one resume sequence number. A Kafka batch is different: it can
/// span several topic-partitions, and Kafka's commit unit is <c>(topic, partition) → offset</c>.
/// There is no cross-partition ordering to preserve — records on different partitions are, by
/// definition, unordered relative to one another — so Kinesis's single shard-order frontier
/// collapses here into one independent frontier per partition.
/// <c>CheckpointAsync(record)</c> therefore means "everything up to and including this record
/// <em>on this record's own partition</em> is processed", and says nothing about any other
/// partition. That is both the safe reading (it can never mark an untouched record on another
/// partition as done) and the Kafka-native one (it maps exactly onto the offset that gets
/// committed).</para>
/// <para><strong>The caveat this inherits from Kafka (and from Kinesis).</strong> A committed
/// offset is a watermark with no gap tracking: committing offset 10 on a partition marks 0–10 done
/// even if 7 failed. So a handler must checkpoint a partition's <em>frontier</em> — the highest
/// offset such that every earlier offset on that partition is complete — not simply the last record
/// it happened to touch. Processing a partition's records in offset order (which
/// <c>PartitionBy(r =&gt; r.TopicPartition)</c> gives you) makes that automatic. Checkpoints that
/// would move a partition's watermark backwards are ignored rather than honored, so an
/// out-of-order or projected-copy checkpoint can't silently rewind the resume point; forward gaps
/// are the handler's responsibility, exactly as they are on Kinesis.</para>
/// </remarks>
/// <typeparam name="TKey">The consumer's key type.</typeparam>
/// <typeparam name="TValue">The consumer's value type.</typeparam>
public class KafkaStreamCheckpointer<TKey, TValue> : IStreamCheckpointer<ConsumeResult<TKey, TValue>>
{
    private readonly IConsumer<TKey, TValue> _consumer;
    private readonly IReadOnlyList<ConsumeResult<TKey, TValue>> _records;
    private readonly ILogger _logger;
    private readonly Dictionary<TopicPartition, long> _watermarks = new();
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaStreamCheckpointer{TKey,TValue}"/> class.
    /// </summary>
    /// <param name="consumer">The consumer whose offsets this checkpointer stores and commits.</param>
    /// <param name="records">The batch's records, in the order they were consumed.</param>
    /// <param name="logger">Used to report offset operations the broker rejects (e.g. after a rebalance).</param>
    public KafkaStreamCheckpointer(IConsumer<TKey, TValue> consumer,
        IReadOnlyList<ConsumeResult<TKey, TValue>> records, ILogger logger)
    {
        _consumer = consumer;
        _records = records;
        _logger = logger;
    }

    /// <summary>Whether the handler has checkpointed at least one record in this batch.</summary>
    public bool HasCheckpointed
    {
        get
        {
            lock (_gate)
            {
                return _watermarks.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Advances <paramref name="lastProcessed"/>'s own partition to its offset, if that moves the
    /// partition's watermark forward; a checkpoint that would move it backwards (or an item that
    /// carries no topic-partition, e.g. a projected copy) is ignored.
    /// </remarks>
    public Task CheckpointAsync(ConsumeResult<TKey, TValue> lastProcessed)
    {
        Advance(lastProcessed);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The synchronous core of <see cref="CheckpointAsync"/>: advances one partition's watermark and
    /// stores the resulting offset. Nothing here is genuinely asynchronous — <c>StoreOffset</c> is a
    /// local, non-blocking librdkafka call — so the batch-wide <see cref="CheckpointAll"/> can reuse
    /// it directly instead of blocking on an already-completed task.
    /// </summary>
    private void Advance(ConsumeResult<TKey, TValue> record)
    {
        // Confluent.Kafka computes TopicPartitionOffset from Topic/Partition/Offset, so it is never
        // null - Topic is the field that tells a real consumed record apart from a projected or
        // default-constructed copy a handler might pass back. A null topic would also throw out of
        // TopicPartition.GetHashCode the moment it was used as a dictionary key.
        if (string.IsNullOrEmpty(record?.Topic))
        {
            return;
        }

        var topicPartition = record.TopicPartition;
        var offset = record.Offset.Value;

        lock (_gate)
        {
            // Only ever advance a partition's watermark, never rewind it - the same rule
            // KinesisStreamCheckpointer applies to its single shard frontier, applied per partition.
            if (_watermarks.TryGetValue(topicPartition, out var current) && offset <= current)
            {
                return;
            }

            _watermarks[topicPartition] = offset;
        }

        // StoreOffset takes the offset to resume FROM, i.e. one past the last processed record.
        // Storing (rather than only committing at the end of the batch) keeps librdkafka's own
        // auto-commit and rebalance-time commit in step with what has genuinely been processed.
        StoreOffset(new TopicPartitionOffset(topicPartition, new Offset(offset + 1)));
    }

    /// <summary>
    /// Advances every partition in the batch to its last record — the whole batch is processed.
    /// Used by <see cref="KafkaStreamOptions.AutoCheckpointOnSuccess"/> when a batch completes
    /// without the handler checkpointing anything itself, and by the skip policy
    /// (<see cref="KafkaStreamOptions.CatchHandlerExceptions"/>) to pass a poison batch over.
    /// </summary>
    public void CheckpointAll()
    {
        foreach (var record in _records)
        {
            Advance(record);
        }
    }

    /// <summary>
    /// Gets the offsets to commit: for each checkpointed partition, one past its watermark. Empty
    /// when nothing has been checkpointed.
    /// </summary>
    public IReadOnlyList<TopicPartitionOffset> CommitOffsets
    {
        get
        {
            lock (_gate)
            {
                return _watermarks
                    .Select(watermark => new TopicPartitionOffset(watermark.Key, new Offset(watermark.Value + 1)))
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Commits the checkpointed offsets, making the batch's progress durable at the broker rather
    /// than only stored locally. A no-op when nothing was checkpointed.
    /// </summary>
    /// <returns>The offsets committed (empty if there were none).</returns>
    public IReadOnlyList<TopicPartitionOffset> Commit()
    {
        var offsets = CommitOffsets;
        if (offsets.Count == 0)
        {
            return offsets;
        }

        try
        {
            _consumer.Commit(offsets);
        }
        catch (KafkaException ex)
        {
            // Typically "Local: Erroneous state" for a partition revoked mid-batch. The offsets were
            // also StoreOffset'd, so the partition's next owner picks up from the last committed
            // point instead - at worst the uncommitted tail is reprocessed. Never fatal.
            _logger.LogWarning(ex, "Committing {OffsetCount} checkpointed Kafka offset(s) failed; the affected records will be redelivered.",
                offsets.Count);
        }

        return offsets;
    }

    /// <summary>
    /// Gets the offsets to rewind each of the batch's partitions to so its <em>uncheckpointed</em>
    /// records are consumed again: one past a partition's watermark where the handler checkpointed,
    /// or the partition's first offset in this batch where it didn't. Drives the retry policy
    /// (<see cref="KafkaStreamOptions.CatchHandlerExceptions"/> = <c>false</c>).
    /// </summary>
    /// <remarks>
    /// This is what lets a Kafka batch resume mid-partition, which is the thing Kinesis's
    /// single-sequence-number contract cannot do for a multi-shard batch: seeking is per partition,
    /// so each partition independently rewinds to exactly its own first unprocessed record.
    /// </remarks>
    public IReadOnlyList<TopicPartitionOffset> ResumeOffsets()
    {
        var resume = new Dictionary<TopicPartition, long>();

        lock (_gate)
        {
            foreach (var record in _records)
            {
                if (string.IsNullOrEmpty(record?.Topic))
                {
                    continue;
                }

                // Records arrive in consume order, so the first one seen for a partition is its
                // lowest offset in this batch - the furthest back a retry ever needs to go.
                if (!resume.ContainsKey(record.TopicPartition))
                {
                    resume[record.TopicPartition] = record.Offset.Value;
                }
            }

            foreach (var watermark in _watermarks)
            {
                if (resume.TryGetValue(watermark.Key, out var first) && watermark.Value + 1 > first)
                {
                    resume[watermark.Key] = watermark.Value + 1;
                }
            }
        }

        return resume.Select(entry => new TopicPartitionOffset(entry.Key, new Offset(entry.Value))).ToList();
    }

    /// <summary>
    /// Rewinds the consumer to <see cref="ResumeOffsets"/> so the batch's unprocessed tail is
    /// re-consumed. A partition the consumer no longer owns is skipped with a warning — its next
    /// owner resumes from the committed offset anyway.
    /// </summary>
    /// <returns>The offsets the consumer was rewound to.</returns>
    public IReadOnlyList<TopicPartitionOffset> SeekToResumeOffsets()
    {
        var offsets = ResumeOffsets();

        foreach (var offset in offsets)
        {
            try
            {
                _consumer.Seek(offset);
            }
            catch (KafkaException ex)
            {
                _logger.LogWarning(ex, "Rewinding {TopicPartitionOffset} to retry the failed batch was rejected; " +
                    "the partition was most likely revoked. Its next owner resumes from the last committed offset.",
                    offset);
            }
        }

        return offsets;
    }

    private void StoreOffset(TopicPartitionOffset offset)
    {
        try
        {
            _consumer.StoreOffset(offset);
        }
        catch (KafkaException ex)
        {
            // A partition revoked mid-batch rejects StoreOffset. The watermark is still tracked so
            // Commit/Seek behave consistently; the record is simply redelivered to the new owner.
            _logger.LogWarning(ex, "Storing checkpointed offset {TopicPartitionOffset} was rejected; " +
                "the partition was most likely revoked and the record will be redelivered.", offset);
        }
    }
}
