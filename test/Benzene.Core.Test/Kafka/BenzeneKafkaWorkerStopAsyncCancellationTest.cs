using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Kafka.Core;
using Benzene.Kafka.Core.KafkaMessage;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Kafka;

/// <summary>
/// #238: <see cref="BenzeneKafkaWorker{TKey,TValue}.StopAsync"/> used to ignore its own
/// <c>cancellationToken</c> parameter entirely - if the consume loop's drain/close hung, the host's
/// stop-timeout had no way to abort the wait (contrast with <c>RabbitMqWorker.StopAsync</c>, which
/// already threaded its token into its own shutdown calls). It now awaits the loop's background task via
/// <c>Task.WaitAsync(cancellationToken)</c>, so a fired stop-timeout token unblocks the caller even while
/// the loop's own <c>IConsumer.Close()</c> is still stuck.
/// </summary>
public class BenzeneKafkaWorkerStopAsyncCancellationTest
{
    private static BenzeneKafkaWorker<string, string> CreateWorker(IKafkaConsumerFactory<string, string> consumerFactory)
    {
        var config = new BenzeneKafkaConfig
        {
            ConsumerConfig = new ConsumerConfig { GroupId = "test-group", BootstrapServers = "localhost:9092" },
            Topics = new[] { "some-topic" }
        };
        var pipeline = Mock.Of<IMiddlewarePipeline<KafkaRecordContext<string, string>>>();
        var kafkaApplication = new KafkaApplication<string, string>(pipeline);
        var resolverFactory = Mock.Of<IServiceResolverFactory>();
        var logger = Mock.Of<ILogger<BenzeneKafkaWorker<string, string>>>();

        return new BenzeneKafkaWorker<string, string>(resolverFactory, kafkaApplication, config, logger, consumerFactory);
    }

    [Fact]
    public async Task StopAsync_WhenItsOwnTokenIsAlreadyCancelled_AbortsTheWaitInsteadOfHangingOnAStuckClose()
    {
        // The consume loop exits immediately (Consume throws OCE, the standard shutdown signal), then
        // its finally block calls IConsumer.Close() - which this mock makes hang indefinitely, standing
        // in for a broker-side close that never completes.
        var closeStarted = new ManualResetEventSlim();
        var releaseClose = new ManualResetEventSlim();
        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(x => x.Consume(It.IsAny<CancellationToken>()))
            .Throws(new OperationCanceledException());
        mockConsumer.Setup(x => x.Close())
            .Callback(() =>
            {
                closeStarted.Set();
                releaseClose.Wait(TimeSpan.FromSeconds(30));
            });
        var mockFactory = new Mock<IKafkaConsumerFactory<string, string>>();
        mockFactory.Setup(x => x.Create(It.IsAny<ConsumerConfig>())).Returns(mockConsumer.Object);

        using var worker = CreateWorker(mockFactory.Object);
        try
        {
            await worker.StartAsync(CancellationToken.None);

            // Don't race StopAsync against the loop until it has actually reached the hung Close() call.
            Assert.True(closeStarted.Wait(TimeSpan.FromSeconds(5)), "the consume loop never reached IConsumer.Close().");

            using var stopCts = new CancellationTokenSource();
            stopCts.Cancel();

            var stopTask = worker.StopAsync(stopCts.Token);
            var winner = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));

            // This is the bug #238 fixes: StopAsync used to ignore its own token and just `await
            // _runTask`, so it would hang here for as long as Close() does (up to the 30s cap above),
            // not return promptly when the caller's own stop-timeout token fired.
            Assert.Same(stopTask, winner);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopTask);
        }
        finally
        {
            // Let the background task actually finish so it doesn't outlive the test.
            releaseClose.Set();
        }
    }
}
