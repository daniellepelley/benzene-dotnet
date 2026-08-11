using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Kafka.Streaming;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Kafka.Streaming;

/// <summary>
/// Covers <see cref="KafkaStreamCheckpointer{TKey,TValue}"/>'s offset watermark rules: one monotonic
/// never-rewinding frontier per topic-partition, written through as <c>StoreOffset(offset + 1)</c>,
/// committed as a set at the end of a batch, and rewound per partition when a batch is retried.
/// </summary>
public class KafkaStreamCheckpointerTest
{
    private static ConsumeResult<string, string> Record(string topic, int partition, long offset, string value = "v")
        => new()
        {
            Message = new Message<string, string> { Key = "k", Value = value },
            TopicPartitionOffset = new TopicPartitionOffset(topic, new Partition(partition), new Offset(offset)),
        };

    private static KafkaStreamCheckpointer<string, string> Checkpointer(Mock<IConsumer<string, string>> consumer,
        params ConsumeResult<string, string>[] records)
        => new(consumer.Object, records, Mock.Of<ILogger>());

    private static (long Partition, long Offset)[] Sorted(IEnumerable<TopicPartitionOffset> offsets)
        => offsets.Select(x => ((long)x.Partition.Value, x.Offset.Value)).OrderBy(x => x.Item1).ToArray();

    [Fact]
    public async Task CheckpointAsync_StoresOnePastTheRecordsOffset()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var record = Record("orders", 0, 41);
        var checkpointer = Checkpointer(consumer, record);

        await checkpointer.CheckpointAsync(record);

        // Kafka commits the offset to resume FROM, so processing offset 41 stores 42.
        consumer.Verify(x => x.StoreOffset(
            It.Is<TopicPartitionOffset>(tpo => tpo.Topic == "orders" && tpo.Partition.Value == 0 && tpo.Offset.Value == 42)),
            Times.Once);
        Assert.True(checkpointer.HasCheckpointed);
    }

    [Fact]
    public void HasCheckpointed_IsFalseBeforeAnyCheckpoint()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var checkpointer = Checkpointer(consumer, Record("orders", 0, 1));

        Assert.False(checkpointer.HasCheckpointed);
        Assert.Empty(checkpointer.CommitOffsets);
    }

    [Fact]
    public async Task CheckpointAsync_OnlyEverAdvances_NeverRewinds()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var first = Record("orders", 0, 10);
        var later = Record("orders", 0, 20);
        var checkpointer = Checkpointer(consumer, first, later);

        await checkpointer.CheckpointAsync(later);
        await checkpointer.CheckpointAsync(first);

        // The out-of-order checkpoint back to offset 10 is ignored - the watermark stays at 20.
        Assert.Equal(new[] { (0L, 21L) }, Sorted(checkpointer.CommitOffsets));
        consumer.Verify(x => x.StoreOffset(It.Is<TopicPartitionOffset>(tpo => tpo.Offset.Value == 11)), Times.Never);
    }

    [Fact]
    public async Task CheckpointAsync_SameOffsetTwice_IsIdempotent()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var record = Record("orders", 0, 7);
        var checkpointer = Checkpointer(consumer, record);

        await checkpointer.CheckpointAsync(record);
        await checkpointer.CheckpointAsync(record);

        consumer.Verify(x => x.StoreOffset(It.IsAny<TopicPartitionOffset>()), Times.Once);
    }

    [Fact]
    public async Task CheckpointAsync_OnOnePartition_LeavesOtherPartitionsUntouched()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var p0 = Record("orders", 0, 5);
        var p1 = Record("orders", 1, 100);
        var checkpointer = Checkpointer(consumer, p0, p1);

        // Checkpointing the LATER record in batch order must not mark the earlier record on the
        // other partition as done - that is the whole point of a per-partition frontier, and the
        // behavior a single Kinesis-style batch-order watermark would get wrong.
        await checkpointer.CheckpointAsync(p1);

        Assert.Equal(new[] { (1L, 101L) }, Sorted(checkpointer.CommitOffsets));
    }

    [Fact]
    public async Task CheckpointAsync_TracksEachPartitionIndependently()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var records = new[]
        {
            Record("orders", 0, 1), Record("orders", 1, 50), Record("orders", 0, 2), Record("orders", 2, 9),
        };
        var checkpointer = Checkpointer(consumer, records);

        await checkpointer.CheckpointAsync(records[2]); // partition 0 -> 2
        await checkpointer.CheckpointAsync(records[1]); // partition 1 -> 50

        Assert.Equal(new[] { (0L, 3L), (1L, 51L) }, Sorted(checkpointer.CommitOffsets));
    }

    [Fact]
    public async Task CheckpointAsync_IgnoresAnItemWithNoTopicPartitionOffset()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var checkpointer = Checkpointer(consumer, Record("orders", 0, 1));

        // A projected/transformed copy the handler passes back carries no TopicPartitionOffset.
        await checkpointer.CheckpointAsync(new ConsumeResult<string, string>());
        await checkpointer.CheckpointAsync(null);

        Assert.False(checkpointer.HasCheckpointed);
        consumer.Verify(x => x.StoreOffset(It.IsAny<TopicPartitionOffset>()), Times.Never);
    }

    [Fact]
    public void CheckpointAll_AdvancesEveryPartitionToItsLastRecord()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var checkpointer = Checkpointer(consumer,
            Record("orders", 0, 1), Record("orders", 1, 50), Record("orders", 0, 4), Record("orders", 1, 51));

        checkpointer.CheckpointAll();

        Assert.Equal(new[] { (0L, 5L), (1L, 52L) }, Sorted(checkpointer.CommitOffsets));
    }

    [Fact]
    public void Commit_WithNothingCheckpointed_DoesNotCallTheConsumer()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var checkpointer = Checkpointer(consumer, Record("orders", 0, 1));

        Assert.Empty(checkpointer.Commit());

        consumer.Verify(x => x.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()), Times.Never);
    }

    [Fact]
    public void Commit_SendsEveryPartitionsWatermarkAsOneSet()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        IEnumerable<TopicPartitionOffset> committed = null;
        consumer.Setup(x => x.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()))
            .Callback<IEnumerable<TopicPartitionOffset>>(offsets => committed = offsets);

        var checkpointer = Checkpointer(consumer, Record("orders", 0, 3), Record("orders", 1, 8));
        checkpointer.CheckpointAll();
        checkpointer.Commit();

        Assert.Equal(new[] { (0L, 4L), (1L, 9L) }, Sorted(committed));
    }

    [Fact]
    public void Commit_WhenTheBrokerRejectsIt_IsSwallowedSoTheWorkerKeepsRunning()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        consumer.Setup(x => x.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()))
            .Throws(new KafkaException(ErrorCode.Local_State));

        var checkpointer = Checkpointer(consumer, Record("orders", 0, 3));
        checkpointer.CheckpointAll();

        // A partition revoked mid-batch rejects the commit; the records are simply redelivered.
        Assert.Equal(new[] { (0L, 4L) }, Sorted(checkpointer.Commit()));
    }

    [Fact]
    public async Task CheckpointAsync_WhenStoreOffsetIsRejected_StillTracksTheWatermark()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        consumer.Setup(x => x.StoreOffset(It.IsAny<TopicPartitionOffset>()))
            .Throws(new KafkaException(ErrorCode.Local_State));

        var record = Record("orders", 0, 3);
        var checkpointer = Checkpointer(consumer, record);

        await checkpointer.CheckpointAsync(record);

        Assert.True(checkpointer.HasCheckpointed);
        Assert.Equal(new[] { (0L, 4L) }, Sorted(checkpointer.CommitOffsets));
    }

    [Fact]
    public void ResumeOffsets_WithNothingCheckpointed_RewindsEachPartitionToItsFirstRecord()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var checkpointer = Checkpointer(consumer,
            Record("orders", 0, 10), Record("orders", 1, 70), Record("orders", 0, 11));

        Assert.Equal(new[] { (0L, 10L), (1L, 70L) }, Sorted(checkpointer.ResumeOffsets()));
    }

    [Fact]
    public async Task ResumeOffsets_RewindsEachPartitionToItsOwnFirstUnprocessedRecord()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var records = new[]
        {
            Record("orders", 0, 10), Record("orders", 1, 70), Record("orders", 0, 11), Record("orders", 1, 71),
        };
        var checkpointer = Checkpointer(consumer, records);

        // Partition 0 got through offset 10; partition 1 got nowhere.
        await checkpointer.CheckpointAsync(records[0]);

        // This is the mid-partition resume Kinesis's single sequence number cannot express: partition
        // 0 restarts at 11 while partition 1 restarts at 70, independently.
        Assert.Equal(new[] { (0L, 11L), (1L, 70L) }, Sorted(checkpointer.ResumeOffsets()));
    }

    [Fact]
    public void ResumeOffsets_WithEveryRecordCheckpointed_RewindsPastTheBatch()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var checkpointer = Checkpointer(consumer, Record("orders", 0, 10), Record("orders", 0, 11));
        checkpointer.CheckpointAll();

        // Nothing to redo: the resume point is the record after the batch's last.
        Assert.Equal(new[] { (0L, 12L) }, Sorted(checkpointer.ResumeOffsets()));
    }

    [Fact]
    public void SeekToResumeOffsets_SeeksEveryPartitionAndSurvivesARejection()
    {
        var consumer = new Mock<IConsumer<string, string>>();
        consumer.Setup(x => x.Seek(It.Is<TopicPartitionOffset>(tpo => tpo.Partition.Value == 1)))
            .Throws(new KafkaException(ErrorCode.Local_State));

        var checkpointer = Checkpointer(consumer, Record("orders", 0, 10), Record("orders", 1, 70));

        Assert.Equal(new[] { (0L, 10L), (1L, 70L) }, Sorted(checkpointer.SeekToResumeOffsets()));

        // A revoked partition's rejected Seek is logged, not fatal - the other partition still rewinds.
        consumer.Verify(x => x.Seek(It.Is<TopicPartitionOffset>(tpo => tpo.Partition.Value == 0 && tpo.Offset.Value == 10)), Times.Once);
    }
}
