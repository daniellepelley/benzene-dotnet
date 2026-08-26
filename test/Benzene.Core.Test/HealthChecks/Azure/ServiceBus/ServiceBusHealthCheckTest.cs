using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Azure.ServiceBus;
using Benzene.HealthChecks.Core;
using Moq;
using Xunit;

namespace Benzene.Test.HealthChecks.Azure.ServiceBus;

public class ServiceBusHealthCheckTest
{
    [Fact]
    public async Task ExecuteAsync_QueuePeekSucceeds_ReturnsHealthy_WithTheQueueDependency()
    {
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(x => x.PeekMessageAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServiceBusReceivedMessage?)null); // empty queue - still a successful round-trip

        var client = new Mock<ServiceBusClient>();
        client.Setup(x => x.CreateReceiver("orders")).Returns(receiver.Object);

        var result = await new ServiceBusHealthCheck(client.Object, "orders").ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("ServiceBus", result.Type);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Queue", dependency.Kind);
        Assert.Equal("orders", dependency.Name);
    }

    [Fact]
    public async Task ExecuteAsync_SubscriptionPeekSucceeds_ReturnsHealthy_WithTheSubscriptionDependency()
    {
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(x => x.PeekMessageAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServiceBusReceivedMessage?)null);

        var client = new Mock<ServiceBusClient>();
        client.Setup(x => x.CreateReceiver("events", "audit")).Returns(receiver.Object);

        var result = await new ServiceBusHealthCheck(client.Object, "events", "audit").ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Subscription", dependency.Kind);
        Assert.Equal("events/audit", dependency.Name);
    }

    [Fact]
    public async Task ExecuteAsync_PeekThrows_ReturnsUnhealthy_ReportingTheExceptionTypeNotMessage()
    {
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(x => x.PeekMessageAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("entity not found: super-secret-connection-detail",
                ServiceBusFailureReason.MessagingEntityNotFound));

        var client = new Mock<ServiceBusClient>();
        client.Setup(x => x.CreateReceiver("orders")).Returns(receiver.Object);

        var result = await new ServiceBusHealthCheck(client.Object, "orders").ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal("ServiceBusException", result.Data["Error"]);
        // The exception message (which could carry sensitive detail) must never reach Data.
        Assert.DoesNotContain(result.Data.Values,
            v => v is string s && s.Contains("super-secret-connection-detail"));
        Assert.Equal("orders", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public async Task ExecuteAsync_Unauthorized_IsPersistentFailure()
    {
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(x => x.PeekMessageAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("no Listen claim"));

        var client = new Mock<ServiceBusClient>();
        client.Setup(x => x.CreateReceiver("orders")).Returns(receiver.Object);

        var result = await new ServiceBusHealthCheck(client.Object, "orders").ExecuteAsync(CancellationToken.None);

        // Lacking the Listen claim is a permission problem: Warning, not a failure (§3.9).
        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.True(result.IsPersistent);
        Assert.Equal("Unauthorized", result.Data["ErrorCode"]);
        Assert.Equal(403, result.Data["StatusCode"]);
    }

    [Fact]
    public async Task ExecuteAsync_DisposesTheReceiver_EvenWhenPeekThrows()
    {
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(x => x.PeekMessageAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("boom", ServiceBusFailureReason.ServiceCommunicationProblem));

        var client = new Mock<ServiceBusClient>();
        client.Setup(x => x.CreateReceiver("orders")).Returns(receiver.Object);

        await new ServiceBusHealthCheck(client.Object, "orders").ExecuteAsync(CancellationToken.None);

        receiver.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // WP-K (#50): before this fix, ServiceBusHealthCheck's own catch (Exception ex) caught the
    // OperationCanceledException the processor's timeout produces along with every genuine SDK failure,
    // and fed it through HealthCheckError.Classify the same way - misclassifying a timeout/shutdown as an
    // ordinary transient dependency failure ({"Error": "TaskCanceledException"}), indistinguishable from
    // a real dead entity. It must instead propagate so ExceptionHandlingHealthCheck (which every check
    // runs under via HealthCheckProcessor) reports the distinct "Cancelled" outcome, the same way
    // TcpHealthCheck's own catch/rethrow already does. Fixed once in HealthCheckError.Classify rather than
    // in this file (or any of the ~10 other affected checks) individually.
    [Fact]
    public async Task ProcessorTimeout_OnAHungPeek_ClassifiesAsCancelled_NotAnOrdinaryDependencyFailure()
    {
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(x => x.PeekMessageAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(async (long? _, CancellationToken ct) =>
            {
                // Never completes on its own - only the processor's (much shorter) timeout ends this await.
                await Task.Delay(Timeout.Infinite, ct);
                return (ServiceBusReceivedMessage?)null;
            });

        var client = new Mock<ServiceBusClient>();
        client.Setup(x => x.CreateReceiver("orders")).Returns(receiver.Object);

        var healthCheck = new ServiceBusHealthCheck(client.Object, "orders");
        var processor = new HealthCheckProcessor(TimeSpan.FromMilliseconds(50));

        var result = await processor.PerformHealthChecksAsync(new IHealthCheck[] { healthCheck });

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.NotNull(response);
        var check = response!.HealthChecks["ServiceBus"];
        Assert.Equal(HealthCheckStatus.Failed, check.Status);
        // Not "TaskCanceledException" (the pre-fix misclassification) - the distinct "Cancelled" outcome.
        Assert.Equal("Cancelled", check.Data["Error"]);
    }
}
