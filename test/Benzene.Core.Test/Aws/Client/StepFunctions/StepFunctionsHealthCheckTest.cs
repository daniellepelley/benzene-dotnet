using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Benzene.Clients.Aws.StepFunctions;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Moq;
using Xunit;

namespace Benzene.Test.Aws.Client.StepFunctions;

public class StepFunctionsHealthCheckTest
{
    [Fact]
    public async Task Reachability_OkResponse_ReturnsHealthy_NonDestructively()
    {
        var mock = new Mock<IAmazonStepFunctions>();
        mock.Setup(x => x.DescribeStateMachineAsync(It.IsAny<DescribeStateMachineRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeStateMachineResponse { HttpStatusCode = HttpStatusCode.OK });

        var healthCheck = new StepFunctionsHealthCheck("some-state-machine-arn", mock.Object);

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("StepFunctions", healthCheck.Type);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("StateMachine", dependency.Kind);
        Assert.Equal("some-state-machine-arn", dependency.Name);
        // The default probe is read-only — it must NOT start an execution.
        mock.Verify(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reachability_NonOkResponse_ReturnsUnhealthy()
    {
        var mock = new Mock<IAmazonStepFunctions>();
        mock.Setup(x => x.DescribeStateMachineAsync(It.IsAny<DescribeStateMachineRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeStateMachineResponse { HttpStatusCode = HttpStatusCode.InternalServerError });

        var result = await new StepFunctionsHealthCheck("some-state-machine-arn", mock.Object).ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Active_StartsAnExecution_AndReportsUnderTheActiveType()
    {
        var mock = new Mock<IAmazonStepFunctions>();
        mock.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartExecutionResponse { HttpStatusCode = HttpStatusCode.OK });

        var healthCheck = new StepFunctionsHealthCheck("some-state-machine-arn", mock.Object, HealthCheckMode.Active);

        var result = await healthCheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Ok, result.Status);
        Assert.Equal("StepFunctions.Active", healthCheck.Type);
        mock.Verify(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // #44: StepFunctionsHealthCheck no longer runs its own internal Task.WhenAny/Task.Delay timeout
    // guard - it relies purely on the processor's uniform timeout wrap, which genuinely cancels the
    // underlying call. This proves that end-to-end: a DescribeStateMachineAsync call that never completes
    // on its own only ends when its own CancellationToken is cancelled - if StepFunctionsHealthCheck
    // failed to forward the token into the SDK call, this would hang instead of being bounded by the
    // processor's short timeout.
    [Fact]
    public async Task ProcessorTimeout_BoundsAHungStepFunctionsCall()
    {
        var callCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mock = new Mock<IAmazonStepFunctions>();
        mock.Setup(x => x.DescribeStateMachineAsync(It.IsAny<DescribeStateMachineRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (DescribeStateMachineRequest _, CancellationToken ct) =>
            {
                // Never completes on its own - only cancellation (the processor's timeout, forwarded by
                // StepFunctionsHealthCheck) can end this await.
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    callCancelled.TrySetResult();
                    throw;
                }
                return new DescribeStateMachineResponse { HttpStatusCode = HttpStatusCode.OK };
            });

        var healthCheck = new StepFunctionsHealthCheck("some-state-machine-arn", mock.Object);
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
