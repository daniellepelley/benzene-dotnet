using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Benzene.Abstractions.Results;
using Benzene.Clients.Aws.EventBridge;
using Benzene.Clients.Aws.Lambda;
using Benzene.Clients.Aws.Sns;
using Benzene.Clients.Aws.Sqs;
using Benzene.Clients.Aws.StepFunctions;
using Benzene.Core;
using Benzene.Resilience;
using Benzene.Results;
using Benzene.Test.Examples;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Aws;

/// <summary>
/// #261: every outbound AWS SDK client middleware/client called its SDK method with no
/// <see cref="CancellationToken"/>, despite every one of those SDK methods actually supporting one -
/// so <c>UseTimeout(...)</c> (or any other consumer of the ambient
/// <see cref="Benzene.Abstractions.DI.ICancellationTokenAccessor"/>) around an outbound AWS send was a
/// silent no-op. Each test here wraps the client in <see cref="TimeoutMiddleware{TContext}"/> at a
/// short deadline over a mocked SDK call that runs for <see cref="MockDelay"/> (much longer than the
/// deadline) unless it observes a cancelled token. Before the fix the deadline never actually aborted
/// the call (it ran for the full <see cref="MockDelay"/> regardless); after the fix, the ambient token
/// reaches the SDK call and the deadline is genuinely enforced, so the call finishes in a small
/// fraction of <see cref="MockDelay"/>. The gap between the 50ms deadline and the generous 2s assertion
/// ceiling (40x) is deliberate slack for scheduler jitter on a loaded box, matching
/// <c>MeshDispatchTest.UseTimeout_AroundTheDispatchHandler_ActuallyBoundsTheRealDispatchCall</c>'s
/// pattern - the assertion is about the fix's mechanism, not about scheduler precision.
/// </summary>
public class OutboundClientCancellationTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MockDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AssertionCeiling = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Sqs_UseTimeoutAroundTheClientMiddleware_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var mockSqs = new Mock<IAmazonSQS>();
        mockSqs.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Returns<SendMessageRequest, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(MockDelay, ct);
                return new SendMessageResponse();
            });

        var middleware = new SqsClientMiddleware(mockSqs.Object, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<SqsSendMessageContext>(accessor, Timeout);
        var context = new SqsSendMessageContext(new SendMessageRequest());

        var stopwatch = Stopwatch.StartNew();
        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => timeoutMiddleware.HandleAsync(context, () => middleware.HandleAsync(context, () => Task.CompletedTask)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < AssertionCeiling,
            $"Expected the send to be cancelled well short of the mocked SDK call's {MockDelay} delay, but it took {stopwatch.Elapsed}.");
        Assert.NotNull(thrown);
        mockSqs.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }

    [Fact]
    public async Task Sns_UseTimeoutAroundTheClientMiddleware_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var mockSns = new Mock<IAmazonSimpleNotificationService>();
        mockSns.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PublishRequest, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(MockDelay, ct);
                return new PublishResponse();
            });

        var middleware = new SnsClientMiddleware(mockSns.Object, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<SnsSendMessageContext>(accessor, Timeout);
        var context = new SnsSendMessageContext(new PublishRequest());

        var stopwatch = Stopwatch.StartNew();
        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => timeoutMiddleware.HandleAsync(context, () => middleware.HandleAsync(context, () => Task.CompletedTask)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < AssertionCeiling,
            $"Expected the publish to be cancelled well short of the mocked SDK call's {MockDelay} delay, but it took {stopwatch.Elapsed}.");
        Assert.NotNull(thrown);
        mockSns.Verify(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }

    [Fact]
    public async Task EventBridge_UseTimeoutAroundTheClientMiddleware_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var mockEventBridge = new Mock<IAmazonEventBridge>();
        mockEventBridge.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutEventsRequest, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(MockDelay, ct);
                return new PutEventsResponse();
            });

        var middleware = new EventBridgeClientMiddleware(mockEventBridge.Object, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<EventBridgeSendMessageContext>(accessor, Timeout);
        var context = new EventBridgeSendMessageContext(new PutEventsRequest());

        var stopwatch = Stopwatch.StartNew();
        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => timeoutMiddleware.HandleAsync(context, () => middleware.HandleAsync(context, () => Task.CompletedTask)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < AssertionCeiling,
            $"Expected the put-events call to be cancelled well short of the mocked SDK call's {MockDelay} delay, but it took {stopwatch.Elapsed}.");
        Assert.NotNull(thrown);
        mockEventBridge.Verify(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }

    [Fact]
    public async Task Lambda_UseTimeoutAroundTheClientMiddleware_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var mockLambda = new Mock<IAmazonLambda>();
        mockLambda.Setup(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), It.IsAny<CancellationToken>()))
            .Returns<InvokeRequest, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(MockDelay, ct);
                return new InvokeResponse();
            });

        var middleware = new AwsLambdaClientMiddleware(mockLambda.Object, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<LambdaSendMessageContext>(accessor, Timeout);
        var context = new LambdaSendMessageContext(new InvokeRequest());

        var stopwatch = Stopwatch.StartNew();
        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => timeoutMiddleware.HandleAsync(context, () => middleware.HandleAsync(context, () => Task.CompletedTask)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < AssertionCeiling,
            $"Expected the invoke to be cancelled well short of the mocked SDK call's {MockDelay} delay, but it took {stopwatch.Elapsed}.");
        Assert.NotNull(thrown);
        mockLambda.Verify(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }

    // StepFunctionsClient wraps its SDK call in its own catch-all (a genuine cancellation is reported
    // as a BenzeneResultStatus.ServiceUnavailable result, not rethrown) - so the observable fix here is
    // that the call actually COMPLETES near the configured deadline (aborting the stalled SDK call)
    // rather than running for the full mocked delay, not that a TimeoutException propagates.
    [Fact]
    public async Task StepFunctions_UseTimeoutAroundTheClient_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var mockStepFunctions = new Mock<IAmazonStepFunctions>();
        mockStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Returns<StartExecutionRequest, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(MockDelay, ct);
                return new StartExecutionResponse();
            });

        var client = new StepFunctionsClient("arn:aws:states:us-east-1:123456789012:stateMachine:test", mockStepFunctions.Object,
            NullLogger<StepFunctionsClient>.Instance, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<object>(accessor, Timeout);

        Task<IBenzeneResult<ExampleResponsePayload>> callTask = null!;
        var stopwatch = Stopwatch.StartNew();
        await timeoutMiddleware.HandleAsync(new object(), () =>
        {
            callTask = client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(new ExampleRequestPayload());
            return callTask;
        });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < AssertionCeiling,
            $"Expected the start-execution call to be cancelled well short of the mocked SDK call's {MockDelay} delay, but it took {stopwatch.Elapsed}.");

        var result = await callTask;
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        mockStepFunctions.Verify(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }
}
