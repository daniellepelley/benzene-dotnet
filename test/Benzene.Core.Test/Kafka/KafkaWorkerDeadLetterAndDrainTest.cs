using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Core.Middleware;
using Benzene.Kafka.Core;
using Benzene.Kafka.Core.KafkaMessage;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Kafka;

/// <summary>
/// Covers the Kafka worker's retry-then-dead-letter path (#29.1b) and the rebalance-drain wiring
/// (#29.1a): a persistently failing record is retried and routed to the dead-letter topic with
/// diagnostic headers, and <c>DrainOnRevoke</c> controls whether the consumer is built with the
/// partitions-revoked handler.
/// </summary>
public class KafkaWorkerDeadLetterAndDrainTest
{
    private static BenzeneKafkaConfig Config(bool commitOnlyOnSuccess = false, bool? drainOnRevoke = null) => new()
    {
        ConsumerConfig = new ConsumerConfig { GroupId = "test-group", BootstrapServers = "localhost:9092" },
        Topics = new[] { "orders" },
        CommitOnlyOnSuccess = commitOnlyOnSuccess,
        CatchHandlerExceptions = !commitOnlyOnSuccess,
        DrainOnRevoke = drainOnRevoke,
    };

    private static Mock<IConsumer<string, string>> ConsumerYielding(ConsumeResult<string, string> record)
    {
        var mockConsumer = new Mock<IConsumer<string, string>>();
        var served = 0;
        mockConsumer.Setup(x => x.Consume(It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref served) == 1 ? record : throw new OperationCanceledException());
        return mockConsumer;
    }

    [Fact]
    public void ShouldDrainOnRevoke_DefaultsToCommitOnlyOnSuccess()
    {
        Assert.False(Config(commitOnlyOnSuccess: false).ShouldDrainOnRevoke);
        Assert.True(Config(commitOnlyOnSuccess: true).ShouldDrainOnRevoke);
        Assert.False(Config(commitOnlyOnSuccess: true, drainOnRevoke: false).ShouldDrainOnRevoke);
        Assert.True(Config(commitOnlyOnSuccess: false, drainOnRevoke: true).ShouldDrainOnRevoke);
    }

    [Fact]
    public async Task DrainOnRevokeOff_BuildsConsumerWithoutARebalanceHandler()
    {
        var mockConsumer = ConsumerYielding(null!);
        // No record served (null would NRE the pipeline) - end immediately.
        mockConsumer.Setup(x => x.Consume(It.IsAny<CancellationToken>())).Throws(new OperationCanceledException());

        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(mockConsumer.Object);

        using var worker = new BenzeneKafkaWorker<string, string>(Mock.Of<Benzene.Abstractions.DI.IServiceResolverFactory>(),
            new KafkaApplication<string, string>(Mock.Of<Benzene.Abstractions.Middleware.IMiddlewarePipeline<KafkaRecordContext<string, string>>>()),
            Config(drainOnRevoke: false), Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        mockFactory.Verify(x => x.Create(It.IsAny<ConsumerConfig>()), Times.Once);
        mockFactory.Verify(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>?>()), Times.Never);
    }

    [Fact]
    public async Task DrainOnRevokeOn_BuildsConsumerWithARebalanceHandler()
    {
        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(x => x.Consume(It.IsAny<CancellationToken>())).Throws(new OperationCanceledException());

        Action<ConsumerBuilder<string, string>>? capturedConfigure = null;
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>?>()))
            .Callback<ConsumerConfig, Action<ConsumerBuilder<string, string>>?>((_, configure) => capturedConfigure = configure)
            .Returns(mockConsumer.Object);

        using var worker = new BenzeneKafkaWorker<string, string>(Mock.Of<Benzene.Abstractions.DI.IServiceResolverFactory>(),
            new KafkaApplication<string, string>(Mock.Of<Benzene.Abstractions.Middleware.IMiddlewarePipeline<KafkaRecordContext<string, string>>>()),
            Config(drainOnRevoke: true), Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        mockFactory.Verify(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>?>()), Times.Once);
        Assert.NotNull(capturedConfigure); // the worker supplied a rebalance-handler configuration step
    }

    [Fact]
    public async Task DeadLetter_RetriesThenProducesOriginalRecordWithDiagnosticHeaders()
    {
        var attempts = 0;
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var builder = new MiddlewarePipelineBuilder<KafkaRecordContext<string, string>>(container);
        builder.Use((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("boom");
        });
        var pipeline = builder.Build();
        using var resolverFactory = new MicrosoftServiceResolverFactory(services);
        var kafkaApplication = new KafkaApplication<string, string>(pipeline);

        var record = new ConsumeResult<string, string>
        {
            Message = new Message<string, string>
            {
                Key = "k",
                Value = "v",
                Headers = new Headers { { "orig-h", Encoding.UTF8.GetBytes("1") } },
            },
            TopicPartitionOffset = new TopicPartitionOffset("orders", new Partition(2), new Offset(42)),
        };
        var mockConsumer = ConsumerYielding(record);
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(mockConsumer.Object);

        string producedTopic = null;
        Message<string, string> producedMessage = null;
        var produced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockProducer = new Mock<IProducer<string, string>>();
        mockProducer.Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Message<string, string>, CancellationToken>((t, m, _) =>
            {
                producedTopic = t;
                producedMessage = m;
                produced.TrySetResult();
            })
            .ReturnsAsync(new DeliveryResult<string, string>());

        var deadLetter = new KafkaDeadLetterOptions<string, string>
        {
            DeadLetterTopic = "orders.DLT",
            MaxAttempts = 2,
            Producer = mockProducer.Object,
        };

        using var worker = new BenzeneKafkaWorker<string, string>(resolverFactory, kafkaApplication,
            Config(), Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object, deadLetter);

        await worker.StartAsync(CancellationToken.None);
        // Wait for the dead-letter produce (the record must be consumed and dispatched first), then stop.
        var completed = await Task.WhenAny(produced.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(produced.Task, completed);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, attempts); // retried up to MaxAttempts before dead-lettering
        mockProducer.Verify(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("orders.DLT", producedTopic);
        Assert.NotNull(producedMessage);
        Assert.Equal("k", producedMessage.Key);
        Assert.Equal("v", producedMessage.Value);

        string HeaderValue(string key) => Encoding.UTF8.GetString(GetHeaderBytes(producedMessage.Headers, key));
        Assert.Equal("InvalidOperationException", HeaderValue(KafkaDeadLetterOptions<string, string>.ReasonHeader));
        Assert.Equal("orders", HeaderValue(KafkaDeadLetterOptions<string, string>.OriginalTopicHeader));
        Assert.Equal("2", HeaderValue(KafkaDeadLetterOptions<string, string>.OriginalPartitionHeader));
        Assert.Equal("42", HeaderValue(KafkaDeadLetterOptions<string, string>.OriginalOffsetHeader));
        Assert.Equal("1", HeaderValue("orig-h")); // original headers preserved
    }

    [Fact]
    public async Task DeadLetter_WhenProduceFails_StopsWorkerWithoutStoringTheOffset()
    {
        // Auto-store is off under dead-lettering, and the offset is stored only AFTER a successful
        // produce. If the dead-letter produce fails, the record's offset must never be stored (so it is
        // redelivered on restart, not silently skipped) and the worker must stop.
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var builder = new MiddlewarePipelineBuilder<KafkaRecordContext<string, string>>(container);
        builder.Use((_, _) => throw new InvalidOperationException("boom"));
        using var resolverFactory = new MicrosoftServiceResolverFactory(services);
        var kafkaApplication = new KafkaApplication<string, string>(builder.Build());

        var record = new ConsumeResult<string, string>
        {
            Message = new Message<string, string> { Key = "k", Value = "v", Headers = new Headers() },
            TopicPartitionOffset = new TopicPartitionOffset("orders", new Partition(0), new Offset(7)),
        };
        var mockConsumer = ConsumerYielding(record);
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(mockConsumer.Object);

        var produceAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockProducer = new Mock<IProducer<string, string>>();
        mockProducer.Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => produceAttempted.TrySetResult())
            .ThrowsAsync(new Exception("dead-letter broker down"));

        var deadLetter = new KafkaDeadLetterOptions<string, string>
        {
            DeadLetterTopic = "orders.DLT",
            MaxAttempts = 1,
            Producer = mockProducer.Object,
        };

        using var worker = new BenzeneKafkaWorker<string, string>(resolverFactory, kafkaApplication,
            Config(), Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object, deadLetter);

        await worker.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(produceAttempted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(produceAttempted.Task, completed);
        await worker.StopAsync(CancellationToken.None); // worker stopped itself; StopAsync completes (bounded)

        mockConsumer.Verify(x => x.StoreOffset(It.IsAny<ConsumeResult<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task DeadLetter_WithoutPreserveOrderPerPartition_ThrowsAtStartAsync()
    {
        var config = Config();
        config.PreserveOrderPerPartition = false; // manual-offset watermark would be unsafe out of order

        var deadLetter = new KafkaDeadLetterOptions<string, string>
        {
            DeadLetterTopic = "orders.DLT",
            MaxAttempts = 1,
            Producer = Mock.Of<IProducer<string, string>>(),
        };

        using var worker = new BenzeneKafkaWorker<string, string>(
            Mock.Of<Benzene.Abstractions.DI.IServiceResolverFactory>(),
            new KafkaApplication<string, string>(Mock.Of<Benzene.Abstractions.Middleware.IMiddlewarePipeline<KafkaRecordContext<string, string>>>()),
            config, Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(),
            Mock.Of<IKafkaConsumerFactory<string, string>>(), deadLetter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(CancellationToken.None));
    }

    private static byte[] GetHeaderBytes(Headers headers, string key)
    {
        Assert.True(headers.TryGetLastBytes(key, out var bytes), $"Expected header '{key}' on the dead-lettered message.");
        return bytes;
    }

    /// <summary>
    /// Covers #118: on a genuine partition-LOSS rebalance event the worker must register its own
    /// <c>SetPartitionsLostHandler</c> (rather than silently falling back to the revoked handler, which
    /// would run a pointless drain and then attempt a commit the broker's generation fencing would
    /// reject), and that handler must never commit and must return effectively instantly (no drain
    /// wait).
    /// </summary>
    /// <remarks>
    /// <c>ConsumerBuilder{TKey,TValue}.PartitionsRevokedHandler</c>/<c>PartitionsLostHandler</c> are
    /// NOT public (only the getter's accessibility looked public under a loose reflection check - the
    /// compiler correctly rejects reading them), so the registered <c>Func</c>/<c>Action</c> delegates
    /// can't be read back off a real builder without a live broker connection. Instead
    /// <see cref="BenzeneKafkaWorker{TKey,TValue}.OnPartitionsRevoked"/>/<c>OnPartitionsLost</c> are
    /// <c>internal</c> methods (visible to this test assembly via <c>InternalsVisibleTo</c>) holding
    /// the actual callback logic - this test invokes <c>OnPartitionsLost</c> directly against a mocked
    /// <see cref="IConsumer{TKey,TValue}"/>, the most direct equivalent of "invoke the registered
    /// handler delegate directly" available without a broker. <see cref="DrainOnRevokeOn_RegistersBothRevokedAndLostHandlersWithoutThrowing"/>
    /// separately covers that the worker actually wires both handlers onto the real builder.
    /// </remarks>
    [Fact]
    public async Task PartitionsLostHandler_NeverCommits_AndReturnsImmediately()
    {
        using var worker = await CreateWorkerForRebalanceCallbackTestsAsync(commitOnlyOnSuccess: true);

        var lostPartitions = new List<TopicPartitionOffset>
        {
            new("orders", new Partition(0), Offset.Unset),
            new("orders", new Partition(1), Offset.Unset),
        };

        var mockConsumer = new Mock<IConsumer<string, string>>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // CommitOnlyOnSuccess = true => _managesOffsetsManually = true, so the *revoked* handler would
        // call Commit() for the exact same input - the sharpest possible contrast against the lost
        // handler, which must never call it regardless of this flag.
        worker.OnPartitionsLost(mockConsumer.Object, lostPartitions);
        stopwatch.Stop();

        mockConsumer.Verify(c => c.Commit(), Times.Never);
        mockConsumer.Verify(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()), Times.Never);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Expected the lost handler to return near-instantly (no drain wait); took {stopwatch.Elapsed}.");
    }

    /// <summary>
    /// Regression guard alongside #118: the pre-existing revoked-handler behavior (drain then commit,
    /// when offsets are managed manually) must be unchanged by extracting it into
    /// <see cref="BenzeneKafkaWorker{TKey,TValue}.OnPartitionsRevoked"/> and adding the sibling lost
    /// handler.
    /// </summary>
    [Fact]
    public async Task PartitionsRevokedHandler_StillDrainsAndCommits_WhenManagingOffsetsManually()
    {
        using var worker = await CreateWorkerForRebalanceCallbackTestsAsync(commitOnlyOnSuccess: true);
        var dispatcher = new BoundedConcurrentDispatcher<ConsumeResult<string, string>>(
            1, (_, _) => Task.CompletedTask, Mock.Of<ILogger>());

        var revoked = new List<TopicPartitionOffset> { new("orders", new Partition(0), Offset.Unset) };
        var mockConsumer = new Mock<IConsumer<string, string>>();

        worker.OnPartitionsRevoked(mockConsumer.Object, revoked, dispatcher);

        mockConsumer.Verify(c => c.Commit(), Times.Once);
    }

    /// <summary>
    /// Covers the wiring half of #118: with <c>DrainOnRevoke</c> on, applying the worker's
    /// configure-builder callback to a real (never <c>Build()</c>-ed, so no broker connection is
    /// attempted) <see cref="ConsumerBuilder{TKey,TValue}"/> must call both
    /// <c>SetPartitionsRevokedHandler</c> AND <c>SetPartitionsLostHandler</c> without throwing -
    /// <c>ConsumerBuilder</c> throws if the same handler kind is registered twice, so this also proves
    /// neither is registered more than once.
    /// </summary>
    [Fact]
    public async Task DrainOnRevokeOn_RegistersBothRevokedAndLostHandlersWithoutThrowing()
    {
        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(x => x.Consume(It.IsAny<CancellationToken>())).Throws(new OperationCanceledException());

        Action<ConsumerBuilder<string, string>>? capturedConfigure = null;
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>?>()))
            .Callback<ConsumerConfig, Action<ConsumerBuilder<string, string>>?>((_, configure) => capturedConfigure = configure)
            .Returns(mockConsumer.Object);

        using var worker = new BenzeneKafkaWorker<string, string>(Mock.Of<Benzene.Abstractions.DI.IServiceResolverFactory>(),
            new KafkaApplication<string, string>(Mock.Of<Benzene.Abstractions.Middleware.IMiddlewarePipeline<KafkaRecordContext<string, string>>>()),
            Config(drainOnRevoke: true), Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(capturedConfigure);

        var builder = new ConsumerBuilder<string, string>(
            new ConsumerConfig { GroupId = "test-group", BootstrapServers = "localhost:9092" });

        var exception = Record.Exception(() => capturedConfigure!(builder));
        Assert.Null(exception);
    }

    /// <summary>
    /// Builds a worker and runs it through a full (mocked) <c>StartAsync</c>/<c>StopAsync</c> cycle so
    /// its internal <c>_managesOffsetsManually</c> field is set exactly as production code sets it -
    /// then hands the worker back so a test can invoke <c>OnPartitionsRevoked</c>/<c>OnPartitionsLost</c>
    /// directly against its OWN mocked <see cref="IConsumer{TKey,TValue}"/>, independent of the one the
    /// (immediately-cancelled) consume loop used internally.
    /// </summary>
    private static async Task<BenzeneKafkaWorker<string, string>> CreateWorkerForRebalanceCallbackTestsAsync(bool commitOnlyOnSuccess)
    {
        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(x => x.Consume(It.IsAny<CancellationToken>())).Throws(new OperationCanceledException());

        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>?>()))
            .Returns(mockConsumer.Object);
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(mockConsumer.Object);

        var worker = new BenzeneKafkaWorker<string, string>(Mock.Of<Benzene.Abstractions.DI.IServiceResolverFactory>(),
            new KafkaApplication<string, string>(Mock.Of<Benzene.Abstractions.Middleware.IMiddlewarePipeline<KafkaRecordContext<string, string>>>()),
            Config(commitOnlyOnSuccess: commitOnlyOnSuccess, drainOnRevoke: true),
            Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
        return worker;
    }

    /// <summary>
    /// Covers #119: <c>StartAsync</c> must not mutate the caller's shared <c>ConsumerConfig</c> instance
    /// when it needs <c>EnableAutoOffsetStore = false</c> for manual offset management - it must clone
    /// instead, so a <c>ConsumerConfig</c> the caller reuses elsewhere (a health check, a second worker)
    /// is unaffected. The worker's own internal behavior must still see the setting disabled, on its
    /// own clone.
    /// </summary>
    [Fact]
    public async Task StartAsync_ManagingOffsetsManually_DoesNotMutateCallersConsumerConfig()
    {
        var callerConfig = new ConsumerConfig { GroupId = "test-group", BootstrapServers = "localhost:9092" };
        Assert.Null(callerConfig.EnableAutoOffsetStore); // default, before StartAsync

        var config = new BenzeneKafkaConfig
        {
            ConsumerConfig = callerConfig,
            Topics = new[] { "orders" },
            CommitOnlyOnSuccess = true,
            CatchHandlerExceptions = false,
            PreserveOrderPerPartition = true,
            // Irrelevant to #119 - forced off so the worker takes the single-arg Create(config) path
            // this test mocks, rather than ShouldDrainOnRevoke's CommitOnlyOnSuccess-derived default.
            DrainOnRevoke = false,
        };

        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(x => x.Consume(It.IsAny<CancellationToken>())).Throws(new OperationCanceledException());

        ConsumerConfig? passedToFactory = null;
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>()))
            .Callback<ConsumerConfig>(c => passedToFactory = c)
            .Returns(mockConsumer.Object);

        using var worker = new BenzeneKafkaWorker<string, string>(Mock.Of<Benzene.Abstractions.DI.IServiceResolverFactory>(),
            new KafkaApplication<string, string>(Mock.Of<Benzene.Abstractions.Middleware.IMiddlewarePipeline<KafkaRecordContext<string, string>>>()),
            config, Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        // The caller's own object is untouched.
        Assert.Null(callerConfig.EnableAutoOffsetStore);
        Assert.Same(callerConfig, config.ConsumerConfig);

        // The worker still behaves correctly internally - the factory received a *different* object
        // with auto-store disabled on it.
        Assert.NotNull(passedToFactory);
        Assert.NotSame(callerConfig, passedToFactory);
        Assert.False(passedToFactory!.EnableAutoOffsetStore);
        Assert.Equal("test-group", passedToFactory.GroupId); // other settings carried over onto the clone
        Assert.Equal("localhost:9092", passedToFactory.BootstrapServers);
    }
}
