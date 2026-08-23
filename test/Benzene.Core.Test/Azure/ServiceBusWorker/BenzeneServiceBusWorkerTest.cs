using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.ServiceBus;
using Moq;
using Xunit;

namespace Benzene.Test.Azure.ServiceBusWorker;

public class BenzeneServiceBusWorkerTest
{
    private static BenzeneServiceBusWorker CreateWorker(BenzeneServiceBusConfig config, Mock<IServiceBusClientFactory> mockClientFactory)
    {
        var application = new ServiceBusConsumerApplication(Mock.Of<IMiddlewarePipeline<ServiceBusConsumerContext>>());
        return new BenzeneServiceBusWorker(Mock.Of<IServiceResolverFactory>(), application, config, mockClientFactory.Object);
    }

    [Fact]
    public void BenzeneServiceBusConfig_Defaults()
    {
        var config = new BenzeneServiceBusConfig();

        Assert.Equal(ServiceBusConsumerAckMode.Explicit, config.AckMode);
        Assert.Equal(5, config.MaxConcurrentCalls);
        Assert.Equal(0, config.PrefetchCount);
        Assert.False(config.SessionsEnabled);
        Assert.Equal(8, config.MaxConcurrentSessions);
        Assert.Equal(1, config.MaxConcurrentCallsPerSession);
        Assert.Null(config.MaxAutoLockRenewalDuration);
    }

    [Fact]
    public async Task StartAsync_NoEntityConfigured_ThrowsWithoutCreatingClient()
    {
        var mockClientFactory = new Mock<IServiceBusClientFactory>();
        var worker = CreateWorker(new BenzeneServiceBusConfig(), mockClientFactory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(CancellationToken.None));

        mockClientFactory.Verify(x => x.Create(), Times.Never);
    }

    [Fact]
    public async Task StartAsync_BothQueueAndSubscriptionConfigured_Throws()
    {
        var config = new BenzeneServiceBusConfig
        {
            QueueName = "some-queue",
            TopicName = "some-topic",
            SubscriptionName = "some-subscription"
        };
        var worker = CreateWorker(config, new Mock<IServiceBusClientFactory>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_TopicWithoutSubscription_Throws()
    {
        var config = new BenzeneServiceBusConfig { TopicName = "some-topic" };
        var worker = CreateWorker(config, new Mock<IServiceBusClientFactory>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(CancellationToken.None));
    }

    /// <summary>
    /// The client returned by <see cref="IServiceBusClientFactory.Create"/> is not exclusively the
    /// worker's to dispose: <c>UseServiceBus(..., healthCheck: true)</c> (the default) hands the very
    /// same factory to <c>AddServiceBusDependencyHealthCheck</c>, which builds and holds its own
    /// long-lived <c>ServiceBusHealthCheck</c> against whatever client <c>Create()</c> returns - the
    /// same shared instance when the caller uses the shipped <see cref="ServiceBusClientFactory"/>
    /// (which always returns one injected client, not a fresh one per call - see
    /// <c>Benzene.Integration.Test.ServiceBus.BenzeneServiceBusWorkerLiveTest</c>'s usage). If the
    /// worker disposed that client on stop, every later health-check probe would fail with
    /// <c>ObjectDisposedException</c> even
    /// though the bus itself is fine. So the worker only disposes what it exclusively owns - the
    /// processor(s) it created from the client - never the client itself; matching
    /// <see cref="Benzene.Azure.EventHub.BenzeneEventHubWorker"/>, which never disposes the client its
    /// own factory returns either.
    /// </summary>
    [Fact]
    public async Task StopAsync_DisposesTheProcessor_ButNeverTheClientItDidNotExclusivelyOwn()
    {
        var mockClient = new Mock<ServiceBusClient>();
        var mockProcessor = new Mock<ServiceBusProcessor>();
        mockClient
            .Setup(x => x.CreateProcessor(It.IsAny<string>(), It.IsAny<ServiceBusProcessorOptions>()))
            .Returns(mockProcessor.Object);
        mockProcessor.Setup(x => x.StartProcessingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProcessor.Setup(x => x.StopProcessingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var mockClientFactory = new Mock<IServiceBusClientFactory>();
        mockClientFactory.Setup(x => x.Create()).Returns(mockClient.Object);
        var worker = CreateWorker(new BenzeneServiceBusConfig { QueueName = "orders" }, mockClientFactory);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        // ServiceBusProcessor.DisposeAsync() is a sealed override Moq can't target directly - it
        // delegates to the (overridable) CloseAsync, which is the observable proxy for "the processor
        // was disposed" here. ServiceBusClient.DisposeAsync() has no such indirection, so it's
        // verified directly - this is the assertion that fails on the pre-fix code, which called it.
        mockProcessor.Verify(x => x.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockClient.Verify(x => x.DisposeAsync(), Times.Never);
    }
}
