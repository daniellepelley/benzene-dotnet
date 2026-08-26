using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.RabbitMq;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace Benzene.Test.RabbitMq;

public class RabbitMqHealthCheckTest
{
    private static Mock<IChannel> ChannelMock()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return channel;
    }

    private static IRabbitMqConnectionProvider ProviderWith(Mock<IChannel> channel)
    {
        var connection = new Mock<IConnection>();
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        var provider = new Mock<IRabbitMqConnectionProvider>();
        provider.Setup(x => x.GetConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        return provider.Object;
    }

    [Fact]
    public async Task ExecuteAsync_QueueExists_ReturnsHealthy_NonDestructively()
    {
        var channel = ChannelMock();
        channel.Setup(x => x.QueueDeclarePassiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDeclareOk("orders", 0, 0));

        var check = new RabbitMqHealthCheck(ProviderWith(channel), "orders");
        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("RabbitMq", check.Type);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Queue", dependency.Kind);
        Assert.Equal("orders", dependency.Name);
    }

    [Fact]
    public async Task ExecuteAsync_QueueMissing_ReturnsFailed_WithThe404ReplyCode()
    {
        var channel = ChannelMock();
        channel.Setup(x => x.QueueDeclarePassiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 404, "NOT_FOUND")));

        var result = await new RabbitMqHealthCheck(ProviderWith(channel), "orders").ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal(404, result.Data["StatusCode"]);
        Assert.Equal("404", result.Data["ErrorCode"]);
        Assert.Equal("orders", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public async Task ExecuteAsync_AccessRefused_IsPersistentFailure()
    {
        var channel = ChannelMock();
        channel.Setup(x => x.QueueDeclarePassiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 403, "ACCESS_REFUSED")));

        var result = await new RabbitMqHealthCheck(ProviderWith(channel), "orders").ExecuteAsync(CancellationToken.None);

        // AMQP 403 access-refused is a permission problem: Warning, not a failure (§3.9).
        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.True(result.IsPersistent);
        Assert.Equal(403, result.Data["StatusCode"]);
    }

    [Fact]
    public async Task ExecuteAsync_BrokerUnreachable_ReturnsFailed()
    {
        var provider = new Mock<IRabbitMqConnectionProvider>();
        provider.Setup(x => x.GetConnectionAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("connection refused"));

        var result = await new RabbitMqHealthCheck(provider.Object, "orders").ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal("orders", Assert.Single(result.Dependencies).Name);
    }

    // WP-K (#50): before this fix, RabbitMqHealthCheck's own catch (Exception ex) caught the
    // OperationCanceledException the processor's timeout produces along with every genuine broker
    // failure, and fed it through HealthCheckError.Classify the same way - misclassifying a
    // timeout/shutdown as an ordinary transient dependency failure ({"Error": "TaskCanceledException"}),
    // indistinguishable from a real dead broker. It must instead propagate so ExceptionHandlingHealthCheck
    // (which every check runs under via HealthCheckProcessor) reports the distinct "Cancelled" outcome,
    // the same way TcpHealthCheck's own catch/rethrow already does.
    [Fact]
    public async Task ProcessorTimeout_OnAHungDeclare_ClassifiesAsCancelled_NotAnOrdinaryDependencyFailure()
    {
        var channel = ChannelMock();
        channel.Setup(x => x.QueueDeclarePassiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                // Never completes on its own - only the processor's (much shorter) timeout ends this await.
                await Task.Delay(Timeout.Infinite, ct);
                return new QueueDeclareOk("orders", 0, 0);
            });

        var healthCheck = new RabbitMqHealthCheck(ProviderWith(channel), "orders");
        var processor = new HealthCheckProcessor(TimeSpan.FromMilliseconds(50));

        var result = await processor.PerformHealthChecksAsync(new IHealthCheck[] { healthCheck });

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.NotNull(response);
        var check = response!.HealthChecks["RabbitMq"];
        Assert.Equal(HealthCheckStatus.Failed, check.Status);
        // Not "TaskCanceledException" (the pre-fix misclassification) - the distinct "Cancelled" outcome.
        Assert.Equal("Cancelled", check.Data["Error"]);
    }

    // Unlike the check-level Classify fix above, RabbitMqHealthCheck layers its own connect+declare
    // budget (_timeout) on top of the caller's token (the same shape as GrpcHealthCheck) - a half-open
    // connection hanging past that budget, with no ambient/processor cancellation involved, is a genuine
    // dependency problem and must stay an ordinary transient Failed, not the "Cancelled" outcome reserved
    // for caller-driven cancellation. Guards against over-broadly treating every OperationCanceledException
    // from this check as "Cancelled".
    [Fact]
    public async Task OwnConnectBudgetElapsing_StillClassifiesAsOrdinaryFailed_NotCancelled()
    {
        var channel = ChannelMock();
        channel.Setup(x => x.QueueDeclarePassiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new QueueDeclareOk("orders", 0, 0);
            });

        // CancellationToken.None: no ambient/processor cancellation, only this check's own 50ms budget.
        var check = new RabbitMqHealthCheck(ProviderWith(channel), "orders", TimeSpan.FromMilliseconds(50));

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal("Timed Out", result.Data["Error"]);
    }
}
