using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Benzene.Clients.Aws.Sqs;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Moq;
using Xunit;

namespace Benzene.Test.Aws.Client.Sqs;

public class SqsHealthCheckTest
{
    [Fact]
    public async Task Reachability_OkResponse_ReturnsHealthy_NonDestructively()
    {
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse { HttpStatusCode = HttpStatusCode.OK });

        var healthCheck = new SqsHealthCheck("some-queue-url", mockSqsClient.Object);

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("Sqs", healthCheck.Type);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Queue", dependency.Kind);
        Assert.Equal("some-queue-url", dependency.Name);
        // The default probe is read-only — it must NOT send a message.
        mockSqsClient.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reachability_NonOkResponse_ReturnsUnhealthy()
    {
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse { HttpStatusCode = HttpStatusCode.InternalServerError });

        var result = await new SqsHealthCheck("some-queue-url", mockSqsClient.Object).ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Reachability_Faults_ReturnsUnhealthy_AndKeepsTheQueueDependency()
    {
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSQSException("connection refused"));

        var result = await new SqsHealthCheck("some-queue-url", mockSqsClient.Object).ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        // The structured result (incl. the Queue dependency and the failure TYPE, never the message) survives the fault.
        Assert.Equal("AmazonSQSException", result.Data["Error"]);
        Assert.Equal("some-queue-url", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public async Task Reachability_PermissionDenied_IsPersistentFailure_AndSurfacesTheDiscriminators()
    {
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSQSException("access denied for arn:...")
            {
                ErrorCode = "AccessDenied",
                StatusCode = HttpStatusCode.Forbidden
            });

        var result = await new SqsHealthCheck("some-queue-url", mockSqsClient.Object).ExecuteAsync(CancellationToken.None);

        // "I can't probe this" (403) is a Warning, not a Failed - a least-privilege caller stays green (§3.9).
        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.True(result.IsPersistent);
        Assert.Equal("AccessDenied", result.Data["ErrorCode"]);
        Assert.Equal(403, result.Data["StatusCode"]);
        Assert.Equal("AmazonSQSException", result.Data["Error"]);
    }

    [Fact]
    public async Task Active_SendsAPing_AndReportsUnderTheActiveType()
    {
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK });

        var healthCheck = new SqsHealthCheck("some-queue-url", mockSqsClient.Object, HealthCheckMode.Active);

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("Sqs.Active", healthCheck.Type);
        mockSqsClient.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // WP-7 #26: SqsHealthCheck no longer runs its own internal Task.WhenAny/Task.Delay timeout guard -
    // it relies purely on the processor's uniform timeout wrap, which (after WP-7 #2) genuinely cancels
    // the underlying call. This proves that end-to-end: a GetQueueAttributesAsync call that never
    // completes on its own only ends when its own CancellationToken is cancelled - if SqsHealthCheck
    // failed to forward the token into the SDK call, this would hang for the real 10s SDK-call duration
    // (simulated here as Timeout.Infinite) instead of being bounded by the processor's short timeout.
    [Fact]
    public async Task ProcessorTimeout_BoundsAHungSqsCall()
    {
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (GetQueueAttributesRequest _, CancellationToken ct) =>
            {
                // Never completes on its own - only cancellation (the processor's timeout, forwarded by
                // SqsHealthCheck) can end this await.
                await Task.Delay(Timeout.Infinite, ct);
                return new GetQueueAttributesResponse { HttpStatusCode = HttpStatusCode.OK };
            });

        var healthCheck = new SqsHealthCheck("some-queue-url", mockSqsClient.Object);
        var processor = new HealthCheckProcessor(TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        var result = await processor.PerformHealthChecksAsync(new IHealthCheck[] { healthCheck });
        stopwatch.Stop();

        // Bounded by the processor's 50ms timeout, not the call's real (never-completing) duration.
        // A generous outer bound: this only guards against a genuine "never returns" regression, not
        // ordinary scheduling latency under host contention - the cancellation itself is immediate.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Expected the processor's timeout to bound the hung SQS call; took {stopwatch.Elapsed}.");

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.NotNull(response);
        Assert.False(response!.IsHealthy);
    }

    // WP-K (#50): before this fix, SqsHealthCheck's own catch (Exception ex) caught the
    // OperationCanceledException the processor's timeout produces (via the token forwarding proven
    // above) along with every genuine SDK failure, and fed it through HealthCheckError.Classify the same
    // way - misclassifying a timeout/shutdown as an ordinary transient dependency failure
    // ({"Error": "TaskCanceledException"}), indistinguishable from a real dead queue. It must instead
    // propagate so ExceptionHandlingHealthCheck (which every check runs under via HealthCheckProcessor)
    // reports the distinct "Cancelled" outcome, the same way TcpHealthCheck's own catch/rethrow already
    // does. Fixed once in HealthCheckError.Classify rather than in this file (or any of the ~10 other
    // affected checks) individually.
    [Fact]
    public async Task ProcessorTimeout_OnAHungSqsCall_ClassifiesAsCancelled_NotAnOrdinaryDependencyFailure()
    {
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (GetQueueAttributesRequest _, CancellationToken ct) =>
            {
                // Never completes on its own - only the processor's (much shorter) timeout ends this await.
                await Task.Delay(Timeout.Infinite, ct);
                return new GetQueueAttributesResponse { HttpStatusCode = HttpStatusCode.OK };
            });

        var healthCheck = new SqsHealthCheck("some-queue-url", mockSqsClient.Object);
        var processor = new HealthCheckProcessor(TimeSpan.FromMilliseconds(50));

        var result = await processor.PerformHealthChecksAsync(new IHealthCheck[] { healthCheck });

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.NotNull(response);
        var check = response!.HealthChecks["Sqs"];
        Assert.Equal(HealthCheckStatus.Failed, check.Status);
        // Not "TaskCanceledException" (the pre-fix misclassification) - the distinct "Cancelled" outcome.
        Assert.Equal("Cancelled", check.Data["Error"]);
    }
}
