using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.Core.Middleware;
using Benzene.Kafka.Core;
using Benzene.Kafka.Core.KafkaMessage;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Kafka;

/// <summary>
/// Covers how the self-hosted Kafka worker settles a handler that reports an unsuccessful
/// <see cref="IBenzeneResult"/> <b>without throwing</b> (settlement-default-alignment A1). Under each of
/// the three settlement configurations - default auto-store, <c>CommitOnlyOnSuccess</c>, and
/// dead-lettering - a returned failure must take the same path as a thrown one rather than being
/// silently committed.
/// </summary>
public class KafkaWorkerFailureResultSettlementTest
{
    private static BenzeneKafkaConfig Config(bool commitOnlyOnSuccess = false, bool raiseOnFailureStatus = true) => new()
    {
        ConsumerConfig = new ConsumerConfig { GroupId = "test-group", BootstrapServers = "localhost:9092" },
        Topics = new[] { "orders" },
        CommitOnlyOnSuccess = commitOnlyOnSuccess,
        CatchHandlerExceptions = !commitOnlyOnSuccess,
        RaiseOnFailureStatus = raiseOnFailureStatus,
    };

    private static ConsumeResult<string, string> Record() => new()
    {
        Message = new Message<string, string>
        {
            Key = "k",
            Value = "v",
            Headers = new Headers { { "orig-h", Encoding.UTF8.GetBytes("1") } },
        },
        TopicPartitionOffset = new TopicPartitionOffset("orders", new Partition(2), new Offset(42)),
    };

    private static Mock<IConsumer<string, string>> ConsumerYielding(ConsumeResult<string, string> record)
    {
        var mockConsumer = new Mock<IConsumer<string, string>>();
        var served = 0;
        mockConsumer.Setup(x => x.Consume(It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref served) == 1 ? record : throw new OperationCanceledException());
        return mockConsumer;
    }

    /// <summary>Builds a pipeline whose single middleware records a fixed result, signalling once it has run.</summary>
    private static (KafkaApplication<string, string> Application, MicrosoftServiceResolverFactory Factory, TaskCompletionSource Handled, Func<int> Attempts)
        ApplicationReturning(IBenzeneResult result)
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var builder = new MiddlewarePipelineBuilder<KafkaRecordContext<string, string>>(container);
        builder.Use((context, _) =>
        {
            Interlocked.Increment(ref attempts);
            context.MessageResult = result;
            handled.TrySetResult();
            return Task.CompletedTask;
        });

        return (new KafkaApplication<string, string>(builder.Build()),
            new MicrosoftServiceResolverFactory(services), handled, () => Volatile.Read(ref attempts));
    }

    private static async Task WaitFor(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, completed);
    }

    [Fact]
    public void RaiseOnFailureStatus_DefaultsToTrue()
    {
        // The safe default every other Benzene transport uses - a returned failure is not silently committed.
        Assert.True(new BenzeneKafkaConfig
        {
            ConsumerConfig = new ConsumerConfig(),
            Topics = new[] { "orders" },
        }.RaiseOnFailureStatus);
    }

    [Fact]
    public async Task CommitOnlyOnSuccess_FailureResult_DoesNotStoreTheOffset()
    {
        var (application, factory, handled, _) = ApplicationReturning(BenzeneResult.Set(BenzeneResultStatus.UnexpectedError));
        using var resolverFactory = factory;

        var mockConsumer = ConsumerYielding(Record());
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>?>()))
            .Returns(mockConsumer.Object);

        using var worker = new BenzeneKafkaWorker<string, string>(resolverFactory, application,
            Config(commitOnlyOnSuccess: true), Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(handled.Task);
        await worker.StopAsync(CancellationToken.None);

        // The record's outcome was never established as success, so its offset must stay unstored and
        // the record be redelivered - exactly what a thrown exception already got here.
        mockConsumer.Verify(x => x.StoreOffset(It.IsAny<ConsumeResult<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task CommitOnlyOnSuccess_SuccessResult_StoresTheOffset()
    {
        var (application, factory, handled, _) = ApplicationReturning(BenzeneResult.Set(BenzeneResultStatus.Ok));
        using var resolverFactory = factory;

        var mockConsumer = ConsumerYielding(Record());
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>?>()))
            .Returns(mockConsumer.Object);

        using var worker = new BenzeneKafkaWorker<string, string>(resolverFactory, application,
            Config(commitOnlyOnSuccess: true), Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(handled.Task);
        await worker.StopAsync(CancellationToken.None);

        mockConsumer.Verify(x => x.StoreOffset(It.IsAny<ConsumeResult<string, string>>()), Times.Once);
    }

    [Fact]
    public async Task CommitOnlyOnSuccess_FailureResult_WithRaiseOff_StoresTheOffsetAsBefore()
    {
        var (application, factory, handled, _) = ApplicationReturning(BenzeneResult.Set(BenzeneResultStatus.UnexpectedError));
        using var resolverFactory = factory;

        var mockConsumer = ConsumerYielding(Record());
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>?>()))
            .Returns(mockConsumer.Object);

        using var worker = new BenzeneKafkaWorker<string, string>(resolverFactory, application,
            Config(commitOnlyOnSuccess: true, raiseOnFailureStatus: false),
            Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(handled.Task);
        await worker.StopAsync(CancellationToken.None);

        // Opting out is still possible - a failure result is then indistinguishable from a success.
        mockConsumer.Verify(x => x.StoreOffset(It.IsAny<ConsumeResult<string, string>>()), Times.Once);
    }

    [Fact]
    public async Task AutoStore_FailureResult_LogsAWarning()
    {
        // Under the default auto-store config the offset was stored before the handler ran, so nothing
        // can hold the record back; the least the worker can do is make the loss visible.
        var (application, factory, handled, _) = ApplicationReturning(BenzeneResult.Set(BenzeneResultStatus.UnexpectedError));
        using var resolverFactory = factory;

        var mockConsumer = ConsumerYielding(Record());
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(mockConsumer.Object);

        var mockLogger = new Mock<ILogger<BenzeneKafkaWorker<string, string>>>();
        using var worker = new BenzeneKafkaWorker<string, string>(resolverFactory, application,
            Config(), mockLogger.Object, mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(handled.Task);
        await worker.StopAsync(CancellationToken.None);

        mockConsumer.Verify(x => x.StoreOffset(It.IsAny<ConsumeResult<string, string>>()), Times.Never);
        mockLogger.Verify(x => x.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString().Contains("unsuccessful result") && state.ToString().Contains("orders")),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task AutoStore_SuccessResult_LogsNoWarning()
    {
        var (application, factory, handled, _) = ApplicationReturning(BenzeneResult.Set(BenzeneResultStatus.Ok));
        using var resolverFactory = factory;

        var mockConsumer = ConsumerYielding(Record());
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(mockConsumer.Object);

        var mockLogger = new Mock<ILogger<BenzeneKafkaWorker<string, string>>>();
        using var worker = new BenzeneKafkaWorker<string, string>(resolverFactory, application,
            Config(), mockLogger.Object, mockFactory.Object);

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(handled.Task);
        await worker.StopAsync(CancellationToken.None);

        mockLogger.Verify(x => x.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task DeadLetter_FailureResult_RetriesThenDeadLettersTheRecord()
    {
        var (application, factory, handled, attempts) = ApplicationReturning(BenzeneResult.Set(BenzeneResultStatus.UnexpectedError));
        using var resolverFactory = factory;
        _ = handled;

        var mockConsumer = ConsumerYielding(Record());
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

        using var worker = new BenzeneKafkaWorker<string, string>(resolverFactory, application,
            Config(), Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>(), mockFactory.Object, deadLetter);

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(produced.Task);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, attempts()); // retried up to MaxAttempts, exactly as a throwing handler is
        Assert.Equal("orders.DLT", producedTopic);
        Assert.NotNull(producedMessage);
        Assert.Equal("v", producedMessage.Value);
        Assert.True(producedMessage.Headers.TryGetLastBytes(KafkaDeadLetterOptions<string, string>.ReasonHeader, out var reason));
        Assert.Equal(nameof(KafkaMessageProcessingException), Encoding.UTF8.GetString(reason));
        // Dead-lettered safely, so the poison record's offset is advanced past.
        mockConsumer.Verify(x => x.StoreOffset(It.IsAny<ConsumeResult<string, string>>()), Times.Once);
    }
}
