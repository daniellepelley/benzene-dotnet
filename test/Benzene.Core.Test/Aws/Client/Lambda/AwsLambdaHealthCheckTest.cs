using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Benzene.Clients.Aws.Lambda;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Aws.Client.Lambda;

public class AwsLambdaHealthCheckTest
{
    private static MemoryStream ToPayloadStream(string json)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task Reachability_OkResponse_ReturnsHealthy_NonDestructively()
    {
        var mockLambdaClient = new Mock<IAmazonLambda>();
        mockLambdaClient
            .Setup(x => x.GetFunctionConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetFunctionConfigurationResponse { HttpStatusCode = HttpStatusCode.OK });

        var healthCheck = new AwsLambdaHealthCheck("some-lambda", mockLambdaClient.Object, NullLogger<AwsLambdaHealthCheck>.Instance);

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal("Lambda", healthCheck.Type);
        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Lambda", dependency.Kind);
        Assert.Equal("some-lambda", dependency.Name);
        // The default probe is read-only — it must NOT invoke the function.
        mockLambdaClient.Verify(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Reachability_NonOkResponse_ReturnsUnhealthy()
    {
        var mockLambdaClient = new Mock<IAmazonLambda>();
        mockLambdaClient
            .Setup(x => x.GetFunctionConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetFunctionConfigurationResponse { HttpStatusCode = HttpStatusCode.NotFound });

        var result = await new AwsLambdaHealthCheck("some-lambda", mockLambdaClient.Object, NullLogger<AwsLambdaHealthCheck>.Instance).ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Active_InvokesTheFunction_AndReportsUnderTheActiveType()
    {
        var mockLambdaClient = new Mock<IAmazonLambda>();
        mockLambdaClient
            .Setup(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), default))
            .ReturnsAsync(new InvokeResponse { StatusCode = 202, Payload = ToPayloadStream("{\"status\":\"Ok\"}") });

        var healthCheck = new AwsLambdaHealthCheck("some-lambda", mockLambdaClient.Object, NullLogger<AwsLambdaHealthCheck>.Instance, HealthCheckMode.Active);

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal("Lambda.Active", healthCheck.Type);
        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        mockLambdaClient.Verify(x => x.InvokeAsync(
            It.Is<InvokeRequest>(r => r.InvocationType == InvocationType.Event), default), Times.Once);
    }

    // #44: AwsLambdaHealthCheck no longer runs its own internal Task.WhenAny/Task.Delay timeout guard -
    // the reachability path relies purely on the processor's uniform timeout wrap, which genuinely
    // cancels the underlying call. This proves that end-to-end: a GetFunctionConfigurationAsync call that
    // never completes on its own only ends when its own CancellationToken is cancelled - if
    // AwsLambdaHealthCheck failed to forward the token into the SDK call, this would hang instead of
    // being bounded by the processor's short timeout.
    [Fact]
    public async Task ProcessorTimeout_BoundsAHungLambdaCall()
    {
        var callCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockLambdaClient = new Mock<IAmazonLambda>();
        mockLambdaClient
            .Setup(x => x.GetFunctionConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                // Never completes on its own - only cancellation (the processor's timeout, forwarded by
                // AwsLambdaHealthCheck) can end this await.
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    callCancelled.TrySetResult();
                    throw;
                }
                return new GetFunctionConfigurationResponse { HttpStatusCode = HttpStatusCode.OK };
            });

        var healthCheck = new AwsLambdaHealthCheck("some-lambda", mockLambdaClient.Object, NullLogger<AwsLambdaHealthCheck>.Instance);
        var processor = new HealthCheckProcessor(TimeSpan.FromMilliseconds(50));

        // The check returns, and the SDK call ended because its token was cancelled - the processor's 50ms
        // timeout, forwarded into the call. Both waits are only "never returns" guards, far above the
        // timeout: a stopwatch ceiling here failed whenever the parallel suite starved the thread pool
        // (38s against 50ms on CI), which says nothing about the mechanism. If the token were not
        // forwarded, the infinite delay would never be cancelled and the second wait would time out.
        var result = await processor.PerformHealthChecksAsync(new IHealthCheck[] { healthCheck }).WaitAsync(TimeSpan.FromMinutes(2));
        await callCancelled.Task.WaitAsync(TimeSpan.FromMinutes(2));

        var response = result.PayloadAsObject as HealthCheckResponse;
        Assert.NotNull(response);
        Assert.False(response!.IsHealthy);
    }
}
