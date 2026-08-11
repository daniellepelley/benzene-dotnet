using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Kafka.Core;
using Benzene.Kafka.Streaming;
using Benzene.Microsoft.Dependencies;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Kafka.Streaming;

/// <summary>
/// Covers <see cref="BenzeneKafkaStreamWorker{TKey,TValue}"/>'s batching loop, offset settlement and
/// lifecycle against a faked <see cref="IConsumer{TKey,TValue}"/>, in the same Moq style
/// <c>BenzeneKafkaWorkerTest</c>/<c>KafkaConsumerFactoryTest</c> use for the per-record worker.
/// </summary>
public class BenzeneKafkaStreamWorkerTest
{
    private static ConsumeResult<string, string> Record(int partition, long offset, string value = "v")
        => new()
        {
            Message = new Message<string, string> { Key = "k", Value = value },
            TopicPartitionOffset = new TopicPartitionOffset("orders", new Partition(partition), new Offset(offset)),
        };

    /// <summary>
    /// Drives a real worker over a scripted consumer. The script is a queue of "what the next
    /// <c>Consume(timeout)</c> returns": a record, or <c>null</c> for an expired poll window (which is
    /// how a time-triggered flush is provoked deterministically, without sleeping).
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentQueue<Func<ConsumeResult<string, string>>> _script = new();
        private readonly MicrosoftServiceResolverFactory _resolverFactory;
        public Mock<IConsumer<string, string>> Consumer { get; } = new();
        public BenzeneKafkaStreamWorker<string, string> Worker { get; }
        public List<IReadOnlyList<ConsumeResult<string, string>>> Batches { get; } = new();
        public List<IReadOnlyList<TopicPartition>> BatchTopicPartitions { get; } = new();
        public List<IReadOnlyList<TopicPartitionOffset>> Commits { get; } = new();
        public List<TopicPartitionOffset> Seeks { get; } = new();

        /// <summary>Set to make the pipeline throw for the nth (1-based) batch it sees.</summary>
        public Func<int, bool> ThrowOnBatch { get; set; } = _ => false;

        /// <summary>Set to have the handler checkpoint explicitly; receives the batch's records.</summary>
        public Func<StreamContext<ConsumeResult<string, string>>, Task> OnBatch { get; set; }

        public Harness(KafkaStreamOptions options, BenzeneKafkaConfig config = null)
        {
            Consumer.Setup(x => x.Consume(It.IsAny<TimeSpan>()))
                .Returns(() => _script.TryDequeue(out var next) ? next() : null);
            Consumer.Setup(x => x.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()))
                .Callback<IEnumerable<TopicPartitionOffset>>(offsets => Commits.Add(offsets.ToList()));
            Consumer.Setup(x => x.Seek(It.IsAny<TopicPartitionOffset>()))
                .Callback<TopicPartitionOffset>(offset => Seeks.Add(offset));

            var services = new ServiceCollection();
            var container = new MicrosoftBenzeneServiceContainer(services);
            var builder = new MiddlewarePipelineBuilder<StreamContext<ConsumeResult<string, string>>>(container);
            builder.UseStream<ConsumeResult<string, string>>(async context =>
            {
                var records = new List<ConsumeResult<string, string>>();
                await foreach (var record in context.Items)
                {
                    records.Add(record);
                }

                Batches.Add(records);
                BatchTopicPartitions.Add(
                    (IReadOnlyList<TopicPartition>)context.Metadata[KafkaStreamApplication<string, string>.TopicPartitionsMetadataKey]);

                if (OnBatch != null)
                {
                    await OnBatch(context);
                }

                if (ThrowOnBatch(Batches.Count))
                {
                    throw new InvalidOperationException("boom");
                }
            });

            _resolverFactory = new MicrosoftServiceResolverFactory(services);

            var factory = new Mock<IKafkaConsumerFactory<string, string>>();
            factory.Setup(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>>()))
                .Returns(Consumer.Object);
            factory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(Consumer.Object);
            ConsumerFactory = factory;

            Config = config ?? new BenzeneKafkaConfig
            {
                ConsumerConfig = new ConsumerConfig { GroupId = "test-group", BootstrapServers = "localhost:9092" },
                Topics = new[] { "orders" },
                DrainTimeout = TimeSpan.FromSeconds(10),
                ConsumeExceptionRetryDelay = TimeSpan.Zero,
            };

            Worker = new BenzeneKafkaStreamWorker<string, string>(_resolverFactory,
                new KafkaStreamApplication<string, string>(builder.Build()), Config, options,
                Mock.Of<ILogger<BenzeneKafkaStreamWorker<string, string>>>(), factory.Object);
        }

        public BenzeneKafkaConfig Config { get; }

        public Mock<IKafkaConsumerFactory<string, string>> ConsumerFactory { get; }

        public Harness Serve(params ConsumeResult<string, string>[] records)
        {
            foreach (var record in records)
            {
                _script.Enqueue(() => record);
            }

            return this;
        }

        /// <summary>Scripts an expired poll window — what a quiet topic looks like to the loop.</summary>
        public Harness ServeIdlePoll(int times = 1)
        {
            for (var i = 0; i < times; i++)
            {
                _script.Enqueue(() => null);
            }

            return this;
        }

        public Harness ServeConsumeError()
        {
            _script.Enqueue(() => throw new ConsumeException(
                new ConsumeResult<byte[], byte[]>(), new Error(ErrorCode.Local_Transport, "broker down")));
            return this;
        }

        /// <summary>Runs the worker until <paramref name="batches"/> batches have been flushed, then stops it.</summary>
        public async Task RunUntilAsync(int batches, TimeSpan? timeout = null)
        {
            await Worker.StartAsync(CancellationToken.None);

            var deadline = Stopwatch.StartNew();
            var limit = timeout ?? TimeSpan.FromSeconds(10);
            while (Batches.Count < batches && deadline.Elapsed < limit)
            {
                await Task.Delay(10);
            }

            await Worker.StopAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            // The worker owns the resolver factory (same contract as BenzeneKafkaWorker) and disposes
            // it, so this only has to dispose the worker.
            Worker.Dispose();
        }
    }

    private static KafkaStreamOptions Options(int maxBatchSize = 3, int maxBatchWaitMs = 50) => new()
    {
        MaxBatchSize = maxBatchSize,
        MaxBatchWait = TimeSpan.FromMilliseconds(maxBatchWaitMs),
        PollTimeout = TimeSpan.FromMilliseconds(5),
        FailedBatchRetryDelay = TimeSpan.Zero,
    };

    [Fact]
    public void KafkaStreamOptions_Defaults()
    {
        var options = new KafkaStreamOptions();

        Assert.Equal(500, options.MaxBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(1), options.MaxBatchWait);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.PollTimeout);
        Assert.True(options.AutoCheckpointOnSuccess);
        Assert.False(options.CatchHandlerExceptions);
        Assert.Equal(TimeSpan.FromSeconds(1), options.FailedBatchRetryDelay);
    }

    [Theory]
    [InlineData(0, 1000, 250)]
    [InlineData(10, 0, 250)]
    [InlineData(10, 1000, 0)]
    public async Task StartAsync_WithAnOutOfRangeOption_Throws(int maxBatchSize, int maxBatchWaitMs, int pollTimeoutMs)
    {
        using var harness = new Harness(new KafkaStreamOptions
        {
            MaxBatchSize = maxBatchSize,
            MaxBatchWait = TimeSpan.FromMilliseconds(maxBatchWaitMs),
            PollTimeout = TimeSpan.FromMilliseconds(pollTimeoutMs),
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Worker.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_AlwaysForcesManualOffsetStorage()
    {
        using var harness = new Harness(Options());
        harness.ServeIdlePoll(50);

        await harness.Worker.StartAsync(CancellationToken.None);

        // Streaming never lets Confluent.Kafka auto-store an offset on Consume - the checkpointer is
        // the only thing that may advance one. Observable synchronously, before the loop runs.
        Assert.False(harness.Config.ConsumerConfig.EnableAutoOffsetStore);

        await harness.Worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_SubscribesAndStopAsync_ClosesAndDisposesTheConsumer()
    {
        using var harness = new Harness(Options());
        harness.ServeIdlePoll(50);

        await harness.Worker.StartAsync(CancellationToken.None);
        await harness.Worker.StopAsync(CancellationToken.None);

        harness.Consumer.Verify(x => x.Subscribe(harness.Config.Topics), Times.Once);
        harness.Consumer.Verify(x => x.Close(), Times.Once);
        harness.Consumer.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task StartAsync_BuildsTheConsumerThroughTheFactory_WithARebalanceHandler()
    {
        using var harness = new Harness(Options());
        harness.ServeIdlePoll(50);

        await harness.Worker.StartAsync(CancellationToken.None);
        await harness.Worker.StopAsync(CancellationToken.None);

        harness.ConsumerFactory.Verify(
            x => x.Create(harness.Config.ConsumerConfig, It.IsNotNull<Action<ConsumerBuilder<string, string>>>()),
            Times.Once);
    }

    [Fact]
    public async Task Batch_IsFlushedAsSoonAsMaxBatchSizeIsReached()
    {
        using var harness = new Harness(Options(maxBatchSize: 3, maxBatchWaitMs: 60_000));
        harness.Serve(Record(0, 1), Record(0, 2), Record(0, 3), Record(0, 4));

        // MaxBatchWait is a minute away, so only the size trigger can produce a batch here.
        await harness.RunUntilAsync(1);

        Assert.Equal(new long[] { 1, 2, 3 }, harness.Batches[0].Select(r => r.Offset.Value));
    }

    [Fact]
    public async Task Batch_IsFlushedOnMaxBatchWait_WhenItIsNotYetFull()
    {
        using var harness = new Harness(Options(maxBatchSize: 100, maxBatchWaitMs: 30));
        harness.Serve(Record(0, 1), Record(0, 2));
        harness.ServeIdlePoll(1_000);

        // Only two records for a hundred-record window: the batch can only ever flush on age.
        await harness.RunUntilAsync(1);

        Assert.Equal(new long[] { 1, 2 }, harness.Batches[0].Select(r => r.Offset.Value));
    }

    [Fact]
    public async Task MaxBatchWait_IsMeasuredFromTheBatchsFirstRecord_NotResetByLaterOnes()
    {
        using var harness = new Harness(Options(maxBatchSize: 100, maxBatchWaitMs: 60));
        harness.Serve(Record(0, 1));
        harness.ServeIdlePoll(2);
        harness.Serve(Record(0, 2));
        harness.ServeIdlePoll(2);
        harness.Serve(Record(0, 3));
        harness.ServeIdlePoll(1_000);

        var stopwatch = Stopwatch.StartNew();
        await harness.RunUntilAsync(1);
        stopwatch.Stop();

        // A rolling idle timer would have restarted on records 2 and 3 and taken ~3x as long; a
        // first-record deadline caps the oldest record's wait at MaxBatchWait regardless of arrivals.
        Assert.Equal(new long[] { 1, 2, 3 }, harness.Batches[0].Select(r => r.Offset.Value));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1_500),
            $"the first batch took {stopwatch.ElapsedMilliseconds}ms, which suggests the deadline was being extended");
    }

    [Fact]
    public async Task EmptyPollWindows_DoNotProduceEmptyBatches()
    {
        using var harness = new Harness(Options(maxBatchSize: 5, maxBatchWaitMs: 10));
        harness.ServeIdlePoll(50);
        harness.Serve(Record(0, 1));
        harness.ServeIdlePoll(1_000);

        await harness.RunUntilAsync(1);

        // The quiet stretch before the first record must not run the pipeline on nothing.
        Assert.Single(harness.Batches);
        Assert.Single(harness.Batches[0]);
    }

    [Fact]
    public async Task PartitionEofMarkers_AreNotTreatedAsRecords()
    {
        using var harness = new Harness(Options(maxBatchSize: 2, maxBatchWaitMs: 60_000));
        harness.Serve(new ConsumeResult<string, string>
        {
            IsPartitionEOF = true,
            TopicPartitionOffset = new TopicPartitionOffset("orders", new Partition(0), Offset.End),
        });
        harness.Serve(Record(0, 1), Record(0, 2));

        await harness.RunUntilAsync(1);

        Assert.Equal(new long[] { 1, 2 }, harness.Batches[0].Select(r => r.Offset.Value));
    }

    [Fact]
    public async Task ConsumeException_WithRecordsInHand_FlushesThemRatherThanHoldingThem()
    {
        using var harness = new Harness(Options(maxBatchSize: 100, maxBatchWaitMs: 60_000));
        harness.Serve(Record(0, 1));
        harness.ServeConsumeError();
        harness.ServeIdlePoll(1_000);

        await harness.RunUntilAsync(1);

        Assert.Equal(new long[] { 1 }, harness.Batches[0].Select(r => r.Offset.Value));
    }

    [Fact]
    public async Task ConsumeException_WithNothingInHand_BacksOffAndKeepsConsuming()
    {
        using var harness = new Harness(Options(maxBatchSize: 1, maxBatchWaitMs: 60_000));
        harness.ServeConsumeError();
        harness.Serve(Record(0, 1));
        harness.ServeIdlePoll(1_000);

        await harness.RunUntilAsync(1);

        Assert.Equal(new long[] { 1 }, harness.Batches[0].Select(r => r.Offset.Value));
    }

    [Fact]
    public async Task SuccessfulBatch_AutoCheckpointsEveryPartitionToItsLastRecord()
    {
        using var harness = new Harness(Options(maxBatchSize: 4, maxBatchWaitMs: 60_000));
        harness.Serve(Record(0, 10), Record(1, 70), Record(0, 11), Record(1, 71));

        await harness.RunUntilAsync(1);

        var committed = harness.Commits.Single()
            .Select(x => ((long)x.Partition.Value, x.Offset.Value)).OrderBy(x => x.Item1).ToArray();
        Assert.Equal(new[] { (0L, 12L), (1L, 72L) }, committed);
    }

    [Fact]
    public async Task SuccessfulBatch_WithAutoCheckpointOff_CommitsNothingTheHandlerDidNotCheckpoint()
    {
        var options = Options(maxBatchSize: 2, maxBatchWaitMs: 60_000);
        options.AutoCheckpointOnSuccess = false;

        using var harness = new Harness(options);
        harness.Serve(Record(0, 10), Record(0, 11));

        await harness.RunUntilAsync(1);

        Assert.Empty(harness.Commits);
        harness.Consumer.Verify(x => x.StoreOffset(It.IsAny<TopicPartitionOffset>()), Times.Never);
    }

    [Fact]
    public async Task SuccessfulBatch_WhereTheHandlerCheckpointed_KeepsExactlyItsFrontier()
    {
        using var harness = new Harness(Options(maxBatchSize: 3, maxBatchWaitMs: 60_000));
        harness.Serve(Record(0, 10), Record(0, 11), Record(0, 12));
        harness.OnBatch = async context =>
        {
            // Checkpoint only the first record: auto-checkpoint must NOT then run and quietly
            // acknowledge the two records the handler said nothing about.
            await context.Checkpointer.CheckpointAsync(Record(0, 10));
        };

        await harness.RunUntilAsync(1);

        Assert.Equal(new[] { (0, 11L) },
            harness.Commits.Single().Select(x => (x.Partition.Value, x.Offset.Value)).ToArray());
        Assert.Empty(harness.Seeks);
    }

    [Fact]
    public async Task FailedBatch_UnderTheRetryDefault_CommitsProgressThenRewindsTheTail()
    {
        using var harness = new Harness(Options(maxBatchSize: 4, maxBatchWaitMs: 60_000));
        harness.Serve(Record(0, 10), Record(1, 70), Record(0, 11), Record(1, 71));
        harness.OnBatch = async context => await context.Checkpointer.CheckpointAsync(Record(0, 10));
        harness.ThrowOnBatch = batch => batch == 1;

        await harness.RunUntilAsync(1);

        // Partition 0 keeps its progress and restarts at 11; partition 1 checkpointed nothing and
        // restarts at its first record in the batch. Each partition resumes independently.
        Assert.Equal(new[] { (0, 11L) },
            harness.Commits.Single().Select(x => (x.Partition.Value, x.Offset.Value)).ToArray());
        Assert.Equal(new[] { (0, 11L), (1, 70L) },
            harness.Seeks.Select(x => (x.Partition.Value, x.Offset.Value)).OrderBy(x => x.Item1).ToArray());
    }

    [Fact]
    public async Task FailedBatch_UnderTheRetryDefault_IsNeverAutoCheckpointed()
    {
        using var harness = new Harness(Options(maxBatchSize: 2, maxBatchWaitMs: 60_000));
        harness.Serve(Record(0, 10), Record(0, 11));
        harness.ThrowOnBatch = batch => batch == 1;

        await harness.RunUntilAsync(1);

        // Nothing was checkpointed, so nothing is committed - the batch is redelivered in full.
        Assert.Empty(harness.Commits);
        Assert.Equal(new[] { (0, 10L) }, harness.Seeks.Select(x => (x.Partition.Value, x.Offset.Value)).ToArray());
    }

    [Fact]
    public async Task FailedBatch_WithCatchHandlerExceptions_SkipsTheWholeBatchAndMovesOn()
    {
        var options = Options(maxBatchSize: 2, maxBatchWaitMs: 60_000);
        options.CatchHandlerExceptions = true;

        using var harness = new Harness(options);
        harness.Serve(Record(0, 10), Record(0, 11));
        harness.ThrowOnBatch = batch => batch == 1;

        await harness.RunUntilAsync(1);

        // Skip mode acknowledges the poison window so the partition keeps moving, and never rewinds.
        Assert.Equal(new[] { (0, 12L) },
            harness.Commits.Single().Select(x => (x.Partition.Value, x.Offset.Value)).ToArray());
        Assert.Empty(harness.Seeks);
    }

    [Fact]
    public async Task FailedBatch_IsRetried_AndSucceedsOnTheSecondAttempt()
    {
        using var harness = new Harness(Options(maxBatchSize: 2, maxBatchWaitMs: 60_000));
        // Re-serve the same records, standing in for the broker replaying them after the Seek.
        harness.Serve(Record(0, 10), Record(0, 11), Record(0, 10), Record(0, 11));
        harness.ThrowOnBatch = batch => batch == 1;

        await harness.RunUntilAsync(2);

        Assert.Equal(2, harness.Batches.Count);
        Assert.Equal(new[] { (0, 12L) },
            harness.Commits.Single().Select(x => (x.Partition.Value, x.Offset.Value)).ToArray());
    }

    [Fact]
    public async Task Batch_ExposesItsTopicPartitionsAsStreamMetadata()
    {
        using var harness = new Harness(Options(maxBatchSize: 3, maxBatchWaitMs: 60_000));
        harness.Serve(Record(0, 10), Record(1, 70), Record(0, 11));

        await harness.RunUntilAsync(1);

        Assert.Equal(new[] { 0, 1 },
            harness.BatchTopicPartitions.Single().Select(tp => tp.Partition.Value).ToArray());
    }

    [Fact]
    public async Task StopAsync_WithARecordHeldInAnUnflushedBatch_AbandonsItUncheckpointed()
    {
        using var harness = new Harness(Options(maxBatchSize: 100, maxBatchWaitMs: 60_000));
        harness.Serve(Record(0, 10));
        harness.ServeIdlePoll(100_000);

        await harness.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await harness.Worker.StopAsync(CancellationToken.None);

        // Acknowledging work that was never done would silently lose it; the record is simply
        // redelivered on restart.
        Assert.Empty(harness.Batches);
        Assert.Empty(harness.Commits);
        harness.Consumer.Verify(x => x.Close(), Times.Once);
    }

    [Fact]
    public async Task StopAsync_WithoutStartAsync_IsANoOp()
    {
        using var harness = new Harness(Options());

        await harness.Worker.StopAsync(CancellationToken.None);

        harness.Consumer.Verify(x => x.Close(), Times.Never);
    }

    [Fact]
    public async Task StopAsync_WhenTheBatchOverrunsTheDrainTimeout_ReturnsAnyway()
    {
        var config = new BenzeneKafkaConfig
        {
            ConsumerConfig = new ConsumerConfig { GroupId = "test-group", BootstrapServers = "localhost:9092" },
            Topics = new[] { "orders" },
            DrainTimeout = TimeSpan.FromMilliseconds(50),
            ConsumeExceptionRetryDelay = TimeSpan.Zero,
        };

        var release = new SemaphoreSlim(0);
        using var harness = new Harness(Options(maxBatchSize: 1, maxBatchWaitMs: 60_000), config);
        harness.Serve(Record(0, 10));
        harness.ServeIdlePoll(100_000);
        harness.OnBatch = async _ => await release.WaitAsync(TimeSpan.FromSeconds(30));

        await harness.Worker.StartAsync(CancellationToken.None);
        while (harness.Batches.Count == 0)
        {
            await Task.Delay(5);
        }

        var stopwatch = Stopwatch.StartNew();
        await harness.Worker.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        // StopAsync must not hang on a wedged handler - DrainTimeout bounds it.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"StopAsync took {stopwatch.ElapsedMilliseconds}ms, which is well past the 50ms drain timeout");
        release.Release();
    }
}
