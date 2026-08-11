using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Core.Middleware;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.Kafka.Core;
using Benzene.Kafka.Streaming;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Kafka.Streaming;

/// <summary>
/// Covers the <c>UseKafkaStream&lt;TKey,TValue&gt;</c> worker wiring end to end through
/// <see cref="InlineSelfHostedStartUp"/>: the batch reaches the configured stream pipeline as one
/// fan-in run, the Kafka transport and health check registrations come from
/// <c>Benzene.Kafka.Core</c> rather than being duplicated here, and the offsets settle.
/// </summary>
public class UseKafkaStreamTest
{
    private static ConsumeResult<string, string> Record(int partition, long offset, string value)
        => new()
        {
            Message = new Message<string, string> { Key = "k", Value = value },
            TopicPartitionOffset = new TopicPartitionOffset("orders", new Partition(partition), new Offset(offset)),
        };

    private static BenzeneKafkaConfig Config() => new()
    {
        ConsumerConfig = new ConsumerConfig { GroupId = "test-group", BootstrapServers = "localhost:9092" },
        Topics = new[] { "orders" },
        DrainTimeout = TimeSpan.FromSeconds(10),
    };

    private static KafkaStreamOptions Options(int maxBatchSize) => new()
    {
        MaxBatchSize = maxBatchSize,
        MaxBatchWait = TimeSpan.FromMinutes(1),
        PollTimeout = TimeSpan.FromMilliseconds(5),
    };

    private static Mock<IKafkaConsumerFactory<string, string>> FactoryServing(
        Mock<IConsumer<string, string>> consumer, params ConsumeResult<string, string>[] records)
    {
        var queue = new Queue<ConsumeResult<string, string>>(records);
        consumer.Setup(x => x.Consume(It.IsAny<TimeSpan>()))
            .Returns(() => queue.Count > 0 ? queue.Dequeue() : null);

        var factory = new Mock<IKafkaConsumerFactory<string, string>>();
        factory.Setup(x => x.Create(It.IsAny<ConsumerConfig>(), It.IsAny<Action<ConsumerBuilder<string, string>>>()))
            .Returns(consumer.Object);
        factory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(consumer.Object);
        return factory;
    }

    [Fact]
    public async Task Batch_IsDeliveredAsOneOrderedStream_InASingleRun()
    {
        var collected = new List<string>();
        var runs = 0;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new Mock<IConsumer<string, string>>();
        var factory = FactoryServing(consumer, Record(0, 1, "a"), Record(0, 2, "b"), Record(1, 9, "c"));

        var worker = new InlineSelfHostedStartUp()
            .ConfigureServices(services => services.AddLogging())
            .Configure(app => app.UseKafkaStream<string, string>(Config(),
                stream => stream.UseStream<ConsumeResult<string, string>>(async (records, _) =>
                {
                    Interlocked.Increment(ref runs);
                    await foreach (var record in records)
                    {
                        collected.Add(record.Message.Value);
                    }

                    done.TrySetResult(true);
                }),
                Options(maxBatchSize: 3), factory.Object, healthCheck: false))
            .Build();

        await worker.StartAsync(CancellationToken.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // Fan-in, not fan-out: three records, one pipeline run, in consume order across partitions.
        Assert.Equal(1, runs);
        Assert.Equal(new[] { "a", "b", "c" }, collected);
    }

    [Fact]
    public async Task Batch_IsCheckpointedThroughToTheConsumer()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new List<TopicPartitionOffset>();
        var consumer = new Mock<IConsumer<string, string>>();
        consumer.Setup(x => x.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()))
            .Callback<IEnumerable<TopicPartitionOffset>>(offsets => committed.AddRange(offsets));

        var factory = FactoryServing(consumer, Record(0, 4, "a"), Record(1, 9, "b"));

        var worker = new InlineSelfHostedStartUp()
            .ConfigureServices(services => services.AddLogging())
            .Configure(app => app.UseKafkaStream<string, string>(Config(),
                stream => stream.UseStream<ConsumeResult<string, string>>(async (records, _) =>
                {
                    await foreach (var record in records)
                    {
                        Assert.NotNull(record);
                    }

                    done.TrySetResult(true);
                }),
                Options(maxBatchSize: 2), factory.Object, healthCheck: false))
            .Build();

        await worker.StartAsync(CancellationToken.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // AutoCheckpointOnSuccess is on by default, so a handler that never checkpoints still advances.
        Assert.Equal(new[] { (0, 5L), (1, 10L) },
            committed.Select(x => (x.Partition.Value, x.Offset.Value)).OrderBy(x => x.Item1).ToArray());
        consumer.Verify(x => x.StoreOffset(It.IsAny<TopicPartitionOffset>()), Times.Exactly(2));
    }

    [Fact]
    public void UseKafkaStream_DeclaresTheKafkaTransport_ReusingBenzeneKafkaCoresAddKafka()
    {
        var services = new ServiceCollection();
        var builder = new BenzeneWorkerBuilder(new Benzene.Microsoft.Dependencies.MicrosoftBenzeneServiceContainer(services));

        builder.UseKafkaStream<string, string>(Config(),
            stream => stream.UseStream<ConsumeResult<string, string>>((_, _) => Task.CompletedTask),
            healthCheck: false);

        var factory = new Benzene.Microsoft.Dependencies.MicrosoftServiceResolverFactory(services);
        using var scope = factory.CreateScope();

        // AddKafka<TKey,TValue>() is called rather than re-declared here, so the transport name is
        // the one Benzene.Kafka.Core already publishes.
        Assert.Contains(scope.GetService<IEnumerable<ITransportInfo>>(), x => x.Name == TransportNames.Kafka);
    }

    [Fact]
    public void UseKafkaStream_WithTheHealthCheckOn_RegistersTheSameKafkaDependencyCheckAsUseKafka()
    {
        var services = new ServiceCollection();
        _ = new HealthCheckBuilder(new WorkerRegister(services));

        var builder = new BenzeneWorkerBuilder(new Benzene.Microsoft.Dependencies.MicrosoftBenzeneServiceContainer(services));
        builder.UseKafkaStream<string, string>(Config(),
            stream => stream.UseStream<ConsumeResult<string, string>>((_, _) => Task.CompletedTask));

        var factory = new Benzene.Microsoft.Dependencies.MicrosoftServiceResolverFactory(services);
        using var scope = factory.CreateScope();
        var finder = scope.GetService<IHealthCheckFinder>();

        Assert.Single(finder.FindDependencyHealthChecks(), x => x.Type == "Kafka");
    }

    [Fact]
    public void UseKafkaStream_WithTheHealthCheckOff_RegistersNoKafkaCheck()
    {
        var services = new ServiceCollection();
        _ = new HealthCheckBuilder(new WorkerRegister(services));

        var builder = new BenzeneWorkerBuilder(new Benzene.Microsoft.Dependencies.MicrosoftBenzeneServiceContainer(services));
        builder.UseKafkaStream<string, string>(Config(),
            stream => stream.UseStream<ConsumeResult<string, string>>((_, _) => Task.CompletedTask),
            healthCheck: false);

        var factory = new Benzene.Microsoft.Dependencies.MicrosoftServiceResolverFactory(services);
        using var scope = factory.CreateScope();
        var finder = scope.GetService<IHealthCheckFinder>();

        Assert.DoesNotContain(finder.FindDependencyHealthChecks(), x => x.Type == "Kafka");
    }

    private sealed class WorkerRegister : Benzene.Abstractions.DI.IRegisterDependency
    {
        private readonly IServiceCollection _services;
        public WorkerRegister(IServiceCollection services) => _services = services;

        public void Register(Action<Benzene.Abstractions.DI.IBenzeneServiceContainer> action)
            => action(new Benzene.Microsoft.Dependencies.MicrosoftBenzeneServiceContainer(_services));
    }
}
