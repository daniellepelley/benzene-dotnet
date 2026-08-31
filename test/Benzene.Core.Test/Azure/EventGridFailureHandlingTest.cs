using System;
using Benzene.Results;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.EventGrid;
using Benzene.Core.Exceptions;
using Benzene.Core.MessageHandlers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Azure;

public class EventGridFailureHandlingTest
{
    private static EventGridTriggerEvent[] CreateEvent(string id = "evt-1")
        => [new EventGridTriggerEvent { Id = id, EventType = "OrderPlaced" }];

    private static Mock<IServiceResolverFactory> CreateResolverFactory()
    {
        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        mockResolver.Setup(x => x.GetService<ILogger<EventGridApplication>>()).Returns(Mock.Of<ILogger<EventGridApplication>>());
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);
        return mockResolverFactory;
    }

    [Fact]
    public void EventGridOptions_Defaults_CascadeExceptions_AndEscalateFailureResults()
    {
        var options = new EventGridOptions();
        Assert.False(options.CatchExceptions);
        Assert.True(options.RaiseOnFailureStatus);
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerThrows_ExceptionCascades()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<EventGridContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<EventGridContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var application = new EventGridBatchApplication(mockPipeline.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(CreateEvent(), CreateResolverFactory().Object));
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_HandlerReturnsFailureResult_ThrowsEventGridMessageProcessingException()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<EventGridContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<EventGridContext>(), It.IsAny<IServiceResolver>()))
            .Callback<EventGridContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var application = new EventGridBatchApplication(mockPipeline.Object, new EventGridOptions { RaiseOnFailureStatus = true });

        var exception = await Assert.ThrowsAsync<EventGridMessageProcessingException>(
            () => application.HandleAsync(CreateEvent("evt-2"), CreateResolverFactory().Object));
        Assert.Equal("evt-2", exception.EventId);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_NoResultRecorded_ThrowsEventGridMessageProcessingException()
    {
        // Nothing set a MessageResult - typically an unrouted event (no handler matched the event
        // type). Per work/settlement-consistency-fix-plan.md row 5, a null outcome is escalated the
        // same as an explicit failure result, not accepted as success - Event Grid's own delivery
        // retry + optional dead-letter destination is the backstop that makes retaining it safe.
        // Enforced via AzureFunctionBatchApplicationBase.EscalateUnestablishedOutcome (default true,
        // not overridden by this transport).
        var mockPipeline = new Mock<IMiddlewarePipeline<EventGridContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<EventGridContext>(), It.IsAny<IServiceResolver>()))
            .Returns(Task.CompletedTask);

        var application = new EventGridBatchApplication(mockPipeline.Object, new EventGridOptions { RaiseOnFailureStatus = true });

        var exception = await Assert.ThrowsAsync<EventGridMessageProcessingException>(
            () => application.HandleAsync(CreateEvent("evt-3"), CreateResolverFactory().Object));
        Assert.Equal("evt-3", exception.EventId);
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerReturnsFailureResult_ThrowsEventGridMessageProcessingException()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<EventGridContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<EventGridContext>(), It.IsAny<IServiceResolver>()))
            .Callback<EventGridContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var application = new EventGridBatchApplication(mockPipeline.Object);

        // Safe-by-default: a returned failure result is escalated so Event Grid retries it.
        await Assert.ThrowsAsync<EventGridMessageProcessingException>(
            () => application.HandleAsync(CreateEvent(), CreateResolverFactory().Object));
    }

    /// <summary>
    /// Regression coverage for #257: under <c>CatchExceptions = true</c>,
    /// <see cref="Benzene.Azure.Function.Core.AzureFunctionBatchApplicationBase{TContext, TState}.ProcessItemAsync"/>
    /// (this class, representative of every consumer - ServiceBus/EventHub/Kafka/QueueStorage/EventGrid
    /// all share the base) must let an infrastructure/DI-wiring failure
    /// (<see cref="BenzeneFailure.IsInfrastructure"/>) escape containment and fail the whole invocation,
    /// mirroring <c>SingleContextEscalatingApplicationBase.ProcessAsync</c>'s #228 fix. Before the fix,
    /// this completed without throwing (logged only) - the mis-wired-service defect #228 fixed for AWS
    /// SNS/S3/EventBridge.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_InfrastructureFailure_EscapesContainmentAndRethrows()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<EventGridContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<EventGridContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new BenzeneResolutionException("Unable to resolve ISomeService"));

        var application = new EventGridBatchApplication(mockPipeline.Object, new EventGridOptions { CatchExceptions = true });

        await Assert.ThrowsAsync<BenzeneResolutionException>(
            () => application.HandleAsync(CreateEvent(), CreateResolverFactory().Object));
    }

    /// <summary>
    /// Regression coverage for #258: the same catch that Finding 1 fixes must also let a genuine
    /// ambient-cancellation <see cref="OperationCanceledException"/> escape containment - matching
    /// #230's "still-queued" item, which already aborts the whole invocation regardless of
    /// <c>CatchExceptions</c>. Before the fix, an already-running item's OCE (tied to the very
    /// <c>cancellationToken</c> passed into this call) was logged and swallowed like an ordinary
    /// business exception - two items hit by the same host cancellation got opposite treatment purely
    /// by scheduling luck.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_AmbientCancellation_EscapesContainmentAndRethrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockPipeline = new Mock<IMiddlewarePipeline<EventGridContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<EventGridContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var application = new EventGridBatchApplication(mockPipeline.Object, new EventGridOptions { CatchExceptions = true });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => application.HandleAsync(CreateEvent(), CreateResolverFactory().Object, cts.Token));
    }

    /// <summary>
    /// The negative case alongside #258's fix: an <see cref="OperationCanceledException"/> whose own
    /// token is unrelated to this call's ambient <c>cancellationToken</c> (which is NOT itself
    /// cancelled) is an application-produced cancellation, not host shutdown - it stays contained under
    /// <c>CatchExceptions</c>, exactly like any other business exception. This is the token-VERIFIED
    /// distinction (matching <c>MessageHandler.cs</c>'s existing pattern), not a bare type-based
    /// exclusion of every <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_UnrelatedCancellation_StaysContained()
    {
        using var unrelatedCts = new CancellationTokenSource();
        unrelatedCts.Cancel();

        var mockPipeline = new Mock<IMiddlewarePipeline<EventGridContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<EventGridContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new OperationCanceledException(unrelatedCts.Token));

        var application = new EventGridBatchApplication(mockPipeline.Object, new EventGridOptions { CatchExceptions = true });

        // The ambient token for this call (CancellationToken.None) is not cancelled, so this OCE is not
        // ambient host cancellation - it must stay contained, not escalate.
        await application.HandleAsync(CreateEvent(), CreateResolverFactory().Object, CancellationToken.None);
    }
}
