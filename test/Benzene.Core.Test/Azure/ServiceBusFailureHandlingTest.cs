using System;
using Benzene.Results;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.ServiceBus;
using Benzene.Core.MessageHandlers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Azure;

public class ServiceBusFailureHandlingTest
{
    private static ServiceBusReceivedMessage[] CreateEvent(string messageId = "msg-1")
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: messageId);
        return [message];
    }

    private static (Mock<IServiceResolver> Resolver, Mock<IServiceResolverFactory> ResolverFactory) CreateResolver()
        => CreateResolverWithLogger().Resolvers;

    private static ((Mock<IServiceResolver> Resolver, Mock<IServiceResolverFactory> ResolverFactory) Resolvers, Mock<ILogger<ServiceBusApplication>> Logger) CreateResolverWithLogger()
    {
        var mockLogger = new Mock<ILogger<ServiceBusApplication>>();
        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        mockResolver.Setup(x => x.GetService<ILogger<ServiceBusApplication>>()).Returns(mockLogger.Object);
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);
        return ((mockResolver, mockResolverFactory), mockLogger);
    }

    [Fact]
    public void ServiceBusOptions_Defaults_CascadeExceptions_AndEscalateFailureResults()
    {
        var options = new ServiceBusOptions();
        Assert.False(options.CatchExceptions);
        Assert.True(options.RaiseOnFailureStatus);
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_HandlerThrows_ExceptionCascades()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var (_, resolverFactory) = CreateResolver();
        var application = new ServiceBusBatchApplication(mockPipeline.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(CreateEvent(), resolverFactory.Object));
    }

    [Fact]
    public async Task HandleAsync_CatchExceptionsTrue_HandlerThrows_ExceptionIsSwallowedAndLogged()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var (_, resolverFactory) = CreateResolver();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { CatchExceptions = true });

        // Reaching the end without throwing proves the exception was caught, not cascaded.
        await application.HandleAsync(CreateEvent(), resolverFactory.Object);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_HandlerReturnsFailureResult_ThrowsServiceBusMessageProcessingException()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { RaiseOnFailureStatus = true });

        var exception = await Assert.ThrowsAsync<ServiceBusMessageProcessingException>(
            () => application.HandleAsync(CreateEvent("msg-2"), resolverFactory.Object));
        Assert.Equal("msg-2", exception.MessageId);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_NoResultRecorded_ThrowsServiceBusMessageProcessingException()
    {
        // Nothing set a MessageResult - typically an unrouted message (no handler matched the topic).
        // Per work/settlement-consistency-fix-plan.md row 8, a null outcome is escalated the same as an
        // explicit failure result under the default AckMode = AutoComplete, not accepted (completed) as
        // success - the host then abandons the message on the thrown exception, respecting the
        // entity's max-delivery-count before auto-dead-lettering. Enforced via
        // AzureFunctionBatchApplicationBase.EscalateUnestablishedOutcome (default true, not overridden
        // by this transport's AutoComplete path - row 13's Explicit path is separately covered below).
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { RaiseOnFailureStatus = true });

        var exception = await Assert.ThrowsAsync<ServiceBusMessageProcessingException>(
            () => application.HandleAsync(CreateEvent("msg-3"), resolverFactory.Object));
        Assert.Equal("msg-3", exception.MessageId);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusTrue_HandlerSucceeds_DoesNotThrow()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { RaiseOnFailureStatus = true });

        await application.HandleAsync(CreateEvent(), resolverFactory.Object);
    }

    [Fact]
    public async Task HandleAsync_RaiseOnFailureStatusAndCatchExceptionsBothTrue_FailureResultIsEscalatedThenSwallowed()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { RaiseOnFailureStatus = true, CatchExceptions = true });

        // Reaching the end without throwing proves the escalated failure was caught too.
        await application.HandleAsync(CreateEvent(), resolverFactory.Object);
    }

    [Fact]
    public void ServiceBusOptions_AckMode_DefaultsToAutoComplete()
    {
        Assert.Equal(ServiceBusAckMode.AutoComplete, new ServiceBusOptions().AckMode);
    }

    [Fact]
    public async Task HandleAsync_ExplicitAckMode_HandlerSucceeds_CompletesMessage()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var mockActions = new Mock<ServiceBusMessageActions>();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit });
        var message = CreateEvent()[0];

        await ((IMiddlewareApplication<ServiceBusTriggerBatch>)application)
            .HandleAsync(new ServiceBusTriggerBatch(mockActions.Object, [message]), resolverFactory.Object);

        mockActions.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        mockActions.Verify(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ExplicitAckMode_HandlerReturnsFailureResult_AbandonsMessage()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var mockActions = new Mock<ServiceBusMessageActions>();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit });
        var message = CreateEvent()[0];

        await ((IMiddlewareApplication<ServiceBusTriggerBatch>)application)
            .HandleAsync(new ServiceBusTriggerBatch(mockActions.Object, [message]), resolverFactory.Object);

        mockActions.Verify(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()), Times.Once);
        mockActions.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ExplicitAckMode_NoResultRecorded_AbandonsMessage()
    {
        // Row 13 (Explicit ack path) - already correct before this plan, unchanged: abandon on failure
        // OR a null result, completing only on genuine success (OnPipelineSucceededAsync's own doc
        // comment). Included here alongside row 8's AutoComplete coverage above for completeness.
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var mockActions = new Mock<ServiceBusMessageActions>();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit });
        var message = CreateEvent()[0];

        await ((IMiddlewareApplication<ServiceBusTriggerBatch>)application)
            .HandleAsync(new ServiceBusTriggerBatch(mockActions.Object, [message]), resolverFactory.Object);

        mockActions.Verify(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()), Times.Once);
        mockActions.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ExplicitAckMode_HandlerThrows_AbandonsMessageThenCascades()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var (_, resolverFactory) = CreateResolver();
        var mockActions = new Mock<ServiceBusMessageActions>();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit });
        var message = CreateEvent()[0];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IMiddlewareApplication<ServiceBusTriggerBatch>)application)
                .HandleAsync(new ServiceBusTriggerBatch(mockActions.Object, [message]), resolverFactory.Object));

        mockActions.Verify(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ExplicitAckModeAndCatchExceptionsTrue_HandlerThrows_AbandonsMessageAndSwallows()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var (_, resolverFactory) = CreateResolver();
        var mockActions = new Mock<ServiceBusMessageActions>();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit, CatchExceptions = true });
        var message = CreateEvent()[0];

        // Reaching the end without throwing proves the exception was caught, not cascaded.
        await ((IMiddlewareApplication<ServiceBusTriggerBatch>)application)
            .HandleAsync(new ServiceBusTriggerBatch(mockActions.Object, [message]), resolverFactory.Object);

        mockActions.Verify(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Regression for #10: state.Acked used to be set to true BEFORE the settle call
    // (CompleteMessageAsync/AbandonMessageAsync), not after. So when the settle call itself threw, the
    // base class's fallback-abandon (OnExceptionCaughtAsync / ShouldCleanUpBeforeRethrow, both gated on
    // !state.Acked) saw Acked already true and skipped - exactly when the message most needed to be
    // abandoned. Reverting the OnPipelineSucceededAsync fix (moving `state.Acked = true;` back above
    // the settle call) turns this red: AbandonMessageAsync.Verify(Times.Once) fails because the
    // fallback never fires.
    [Fact]
    public async Task HandleAsync_ExplicitAckMode_CompleteMessageThrows_FallbackAbandonStillFires()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var mockActions = new Mock<ServiceBusMessageActions>();
        var message = CreateEvent()[0];
        mockActions
            .Setup(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Service Bus is unavailable"));

        // CatchExceptions off (the default) so the settle failure cascades - the fallback-abandon must
        // still fire via ShouldCleanUpBeforeRethrow/CleanUpBeforeRethrowAsync before it does.
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IMiddlewareApplication<ServiceBusTriggerBatch>)application)
                .HandleAsync(new ServiceBusTriggerBatch(mockActions.Object, [message]), resolverFactory.Object));

        mockActions.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        mockActions.Verify(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Round 14-15 #232: the fallback-abandon in CleanUpBeforeRethrowAsync used to have no try/catch
    // of its own - if it threw too (e.g. the lock already expired by the time the fallback runs,
    // very plausible since it only runs because something already went wrong), that new exception
    // replaced CompleteMessageAsync's original failure, masking the real cause. Double-fault case:
    // both the primary settle (CompleteMessageAsync) AND the fallback abandon throw.
    [Fact]
    public async Task HandleAsync_ExplicitAckMode_CompleteMessageThrowsAndFallbackAbandonAlsoThrows_OriginalExceptionStillPropagates()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (resolvers, logger) = CreateResolverWithLogger();
        var mockActions = new Mock<ServiceBusMessageActions>();
        var message = CreateEvent()[0];
        var completeException = new InvalidOperationException("Service Bus is unavailable");
        mockActions
            .Setup(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(completeException);
        mockActions
            .Setup(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("abandon also failed - lock already expired"));

        // CatchExceptions off (the default) so the settle failure cascades through
        // ShouldCleanUpBeforeRethrow / CleanUpBeforeRethrowAsync, whose own fallback abandon also
        // fails here.
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IMiddlewareApplication<ServiceBusTriggerBatch>)application)
                .HandleAsync(new ServiceBusTriggerBatch(mockActions.Object, [message]), resolvers.ResolverFactory.Object));

        // The original CompleteMessageAsync failure - not the fallback abandon's own exception - is
        // what propagates; the fallback abandon failure never masks it.
        Assert.Same(completeException, thrown);

        mockActions.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        mockActions.Verify(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()), Times.Once);

        // The fallback abandon's own failure is logged distinctly rather than silently swallowed.
        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => true),
            It.Is<Exception>(ex => ex != null && ex.Message == "abandon also failed - lock already expired"),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    // Same double-fault shape as above, but with CatchExceptions = true so the failure is routed
    // through OnExceptionCaughtAsync (the base class's other guarded call site) instead of
    // CleanUpBeforeRethrowAsync - both hooks call the same fallback-abandon and both needed the
    // guard.
    [Fact]
    public async Task HandleAsync_ExplicitAckModeAndCatchExceptionsTrue_CompleteMessageThrowsAndFallbackAbandonAlsoThrows_ExceptionIsSwallowedNotMasked()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (resolvers, logger) = CreateResolverWithLogger();
        var mockActions = new Mock<ServiceBusMessageActions>();
        var message = CreateEvent()[0];
        var completeException = new InvalidOperationException("Service Bus is unavailable");
        mockActions
            .Setup(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(completeException);
        mockActions
            .Setup(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("abandon also failed - lock already expired"));

        var application = new ServiceBusBatchApplication(mockPipeline.Object,
            new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit, CatchExceptions = true });

        // Reaching the end without throwing proves neither the original settle failure nor the
        // fallback abandon failure cascades under CatchExceptions - and, per the guard, the abandon
        // failure never masks the original settle failure in what gets logged.
        await ((IMiddlewareApplication<ServiceBusTriggerBatch>)application)
            .HandleAsync(new ServiceBusTriggerBatch(mockActions.Object, [message]), resolvers.ResolverFactory.Object);

        mockActions.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        mockActions.Verify(x => x.AbandonMessageAsync(message, null, It.IsAny<CancellationToken>()), Times.Once);

        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => true),
            It.Is<Exception>(ex => ex == completeException),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => true),
            It.Is<Exception>(ex => ex != null && ex.Message == "abandon also failed - lock already expired"),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ReceivedMessageArrayOverload_NeverTouchesMessageActions()
    {
        // The plain ServiceBusReceivedMessage[] overload (no ServiceBusMessageActions available)
        // must behave exactly as AutoComplete mode always has, even when AckMode is Explicit.
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (_, resolverFactory) = CreateResolver();
        var application = new ServiceBusBatchApplication(mockPipeline.Object, new ServiceBusOptions { AckMode = ServiceBusAckMode.Explicit });

        // No ServiceBusMessageActions involved at all - proves this overload doesn't require one.
        await application.HandleAsync(CreateEvent(), resolverFactory.Object);
    }
}
