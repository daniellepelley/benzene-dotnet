using System;
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
/// fraction of <see cref="MockDelay"/>. The assertion is on how the mocked SDK call ended - aborted by
/// the token, or run to completion - not on wall-clock time: a stopwatch ceiling failed whenever the
/// rest of the suite, running in parallel, starved the thread pool (a 50ms deadline measured at 15s
/// there, and at 0.4s run alone), which says nothing about the mechanism under test.
/// </summary>
public class OutboundClientCancellationTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MockDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The mocked SDK call: waits <see cref="MockDelay"/> unless its token is cancelled, and records
    /// which of the two ended it. <see cref="Ended"/> completes either way, so a test can wait for the
    /// call to finish unwinding before asking how it ended.
    /// </summary>
    private sealed class SdkCall
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool AbortedByToken { get; private set; }

        public Task Ended => _ended.Task;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(MockDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                AbortedByToken = true;
                throw;
            }
            finally
            {
                _ended.TrySetResult();
            }
        }

        /// <summary>Waits for the call to finish (generously: it is bounded by <see cref="MockDelay"/> either way) and asserts the token ended it.</summary>
        public async Task AssertAbortedByTheDeadlineAsync(string what)
        {
            await Ended.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(AbortedByToken,
                $"Expected the {what} to be aborted by the deadline's token, but the mocked SDK call ran its full {MockDelay}.");
        }
    }

    [Fact]
    public async Task Sqs_UseTimeoutAroundTheClientMiddleware_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var sdkCall = new SdkCall();
        var mockSqs = new Mock<IAmazonSQS>();
        mockSqs.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Returns<SendMessageRequest, CancellationToken>(async (_, ct) =>
            {
                await sdkCall.RunAsync(ct);
                return new SendMessageResponse();
            });

        var middleware = new SqsClientMiddleware(mockSqs.Object, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<SqsSendMessageContext>(accessor, Timeout);
        var context = new SqsSendMessageContext(new SendMessageRequest());

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => timeoutMiddleware.HandleAsync(context, () => middleware.HandleAsync(context, () => Task.CompletedTask)));

        await sdkCall.AssertAbortedByTheDeadlineAsync("send");
        Assert.NotNull(thrown);
        mockSqs.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }

    [Fact]
    public async Task Sns_UseTimeoutAroundTheClientMiddleware_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var sdkCall = new SdkCall();
        var mockSns = new Mock<IAmazonSimpleNotificationService>();
        mockSns.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PublishRequest, CancellationToken>(async (_, ct) =>
            {
                await sdkCall.RunAsync(ct);
                return new PublishResponse();
            });

        var middleware = new SnsClientMiddleware(mockSns.Object, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<SnsSendMessageContext>(accessor, Timeout);
        var context = new SnsSendMessageContext(new PublishRequest());

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => timeoutMiddleware.HandleAsync(context, () => middleware.HandleAsync(context, () => Task.CompletedTask)));

        await sdkCall.AssertAbortedByTheDeadlineAsync("publish");
        Assert.NotNull(thrown);
        mockSns.Verify(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }

    [Fact]
    public async Task EventBridge_UseTimeoutAroundTheClientMiddleware_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var sdkCall = new SdkCall();
        var mockEventBridge = new Mock<IAmazonEventBridge>();
        mockEventBridge.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutEventsRequest, CancellationToken>(async (_, ct) =>
            {
                await sdkCall.RunAsync(ct);
                return new PutEventsResponse();
            });

        var middleware = new EventBridgeClientMiddleware(mockEventBridge.Object, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<EventBridgeSendMessageContext>(accessor, Timeout);
        var context = new EventBridgeSendMessageContext(new PutEventsRequest());

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => timeoutMiddleware.HandleAsync(context, () => middleware.HandleAsync(context, () => Task.CompletedTask)));

        await sdkCall.AssertAbortedByTheDeadlineAsync("put-events call");
        Assert.NotNull(thrown);
        mockEventBridge.Verify(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }

    [Fact]
    public async Task Lambda_UseTimeoutAroundTheClientMiddleware_ActuallyBoundsTheSdkCall()
    {
        var accessor = new CancellationTokenAccessor();
        var sdkCall = new SdkCall();
        var mockLambda = new Mock<IAmazonLambda>();
        mockLambda.Setup(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), It.IsAny<CancellationToken>()))
            .Returns<InvokeRequest, CancellationToken>(async (_, ct) =>
            {
                await sdkCall.RunAsync(ct);
                return new InvokeResponse();
            });

        var middleware = new AwsLambdaClientMiddleware(mockLambda.Object, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<LambdaSendMessageContext>(accessor, Timeout);
        var context = new LambdaSendMessageContext(new InvokeRequest());

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => timeoutMiddleware.HandleAsync(context, () => middleware.HandleAsync(context, () => Task.CompletedTask)));

        await sdkCall.AssertAbortedByTheDeadlineAsync("invoke");
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
        var sdkCall = new SdkCall();
        var mockStepFunctions = new Mock<IAmazonStepFunctions>();
        mockStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Returns<StartExecutionRequest, CancellationToken>(async (_, ct) =>
            {
                await sdkCall.RunAsync(ct);
                return new StartExecutionResponse();
            });

        var client = new StepFunctionsClient("arn:aws:states:us-east-1:123456789012:stateMachine:test", mockStepFunctions.Object,
            NullLogger<StepFunctionsClient>.Instance, accessor);
        var timeoutMiddleware = new TimeoutMiddleware<object>(accessor, Timeout);

        Task<IBenzeneResult<ExampleResponsePayload>> callTask = null!;
        await timeoutMiddleware.HandleAsync(new object(), () =>
        {
            callTask = client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(new ExampleRequestPayload());
            return callTask;
        });

        await sdkCall.AssertAbortedByTheDeadlineAsync("start-execution call");

        var result = await callTask;
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        mockStepFunctions.Verify(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.Is<CancellationToken>(t => t.CanBeCanceled)));
    }
}
