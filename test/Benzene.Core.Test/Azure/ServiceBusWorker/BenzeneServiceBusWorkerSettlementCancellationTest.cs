using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.ServiceBus;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Azure.ServiceBusWorker;

/// <summary>
/// Regression for #117: settling an already-successfully-handled Service Bus message is part of
/// graceful drain, so it must run under <see cref="CancellationToken.None"/>, never
/// <c>ProcessMessageEventArgs.CancellationToken</c>. Per the SDK's own docs that token "will be
/// cancelled when StopProcessingAsync is called" - and <c>StopProcessingAsync</c> awaits in-flight
/// message handlers rather than cancelling them and moving on - so a settle call gated on it can be
/// cancelled for a message whose handler already succeeded, silently leaving it unsettled for
/// redelivery/double-processing once the lock expires. Reproduced here with the SDK's own
/// <see cref="ProcessMessageEventArgs"/> (public constructor) and a mocked <see cref="ServiceBusReceiver"/>
/// (protected parameterless constructor, virtual settle methods) rather than a documentary claim only.
/// </summary>
public class BenzeneServiceBusWorkerSettlementCancellationTest
{
    private static (BenzeneServiceBusWorker Worker, Mock<ILogger<BenzeneServiceBusWorker>> Logger) CreateWorker(
        IMiddlewarePipeline<ServiceBusConsumerContext> pipeline, BenzeneServiceBusConfig? config = null)
    {
        var application = new ServiceBusConsumerApplication(pipeline);
        var mockLogger = new Mock<ILogger<BenzeneServiceBusWorker>>();

        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ILogger<BenzeneServiceBusWorker>>()).Returns(mockLogger.Object);
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);

        var worker = new BenzeneServiceBusWorker(mockResolverFactory.Object, application,
            config ?? new BenzeneServiceBusConfig { QueueName = "orders" }, Mock.Of<IServiceBusClientFactory>());

        return (worker, mockLogger);
    }

    private static Task InvokeOnProcessMessageAsync(BenzeneServiceBusWorker worker, ProcessMessageEventArgs args)
    {
        var method = typeof(BenzeneServiceBusWorker).GetMethod("OnProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(worker, new object[] { args })!;
    }

    [Fact]
    public async Task HandlerSucceeds_ArgsTokenAlreadyCancelled_MessageIsStillCompleted()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusConsumerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusConsumerContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusConsumerContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (worker, _) = CreateWorker(mockPipeline.Object);

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "done");

        CancellationToken? completeToken = null;
        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver
            .Setup(x => x.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns((ServiceBusReceivedMessage _, CancellationToken ct) =>
            {
                completeToken = ct;
                // Mirrors what a real SDK send-side operation does when handed an already-cancelled
                // token: this is what would have thrown pre-fix, when the (cancelled)
                // args.CancellationToken was passed straight through.
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var args = new ProcessMessageEventArgs(message, mockReceiver.Object, cts.Token);

        // Must not throw: an escaped exception here would surface as a processing failure (and an
        // abandon) for a message whose handler actually succeeded.
        await InvokeOnProcessMessageAsync(worker, args);

        mockReceiver.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        mockReceiver.Verify(x => x.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(completeToken);
        Assert.False(completeToken!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task HandlerFails_ArgsTokenAlreadyCancelled_MessageIsStillAbandoned()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusConsumerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusConsumerContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusConsumerContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.UnexpectedError())
            .Returns(Task.CompletedTask);

        var (worker, _) = CreateWorker(mockPipeline.Object);

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "failed");

        CancellationToken? abandonToken = null;
        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver
            .Setup(x => x.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns((ServiceBusReceivedMessage _, IDictionary<string, object> __, CancellationToken ct) =>
            {
                abandonToken = ct;
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var args = new ProcessMessageEventArgs(message, mockReceiver.Object, cts.Token);

        await InvokeOnProcessMessageAsync(worker, args);

        Assert.NotNull(abandonToken);
        Assert.False(abandonToken!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task HandlerSucceeds_SettlementThrows_LogsDistinctSettlementFailure_DoesNotAbandon_AndDoesNotThrow()
    {
        // Regression for #277: previously SettleAsync ran INSIDE the handler's own try/catch, so a
        // settlement failure after a *successful* handler run was caught by the handler's catch
        // block, logged with the handler-failure template, and the message was wrongly abandoned
        // even though it had already been fully and successfully processed. The fix isolates
        // settlement into its own try/catch: a distinct log message, zero abandon calls, and the
        // exception is swallowed (the lock is left to expire naturally and Service Bus redelivers).
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusConsumerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusConsumerContext>(), It.IsAny<IServiceResolver>()))
            .Callback<ServiceBusConsumerContext, IServiceResolver>((context, _) => context.MessageResult = BenzeneResult.Ok())
            .Returns(Task.CompletedTask);

        var (worker, logger) = CreateWorker(mockPipeline.Object);

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "settle-fails");

        var settlementException = new InvalidOperationException("MessageLockLostException: the lock supplied is invalid");
        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver
            .Setup(x => x.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(settlementException);

        var args = new ProcessMessageEventArgs(message, mockReceiver.Object, CancellationToken.None);

        // Must not throw: a post-success settlement failure must be logged and swallowed, not
        // escape the worker.
        await InvokeOnProcessMessageAsync(worker, args);

        // The already-successfully-processed message must NOT be abandoned.
        mockReceiver.Verify(x => x.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Never);

        // Must NOT be logged with the handler-failure template/exception.
        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Processing Service Bus message") && state.ToString()!.Contains("failed")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);

        // Must be logged with a distinct settlement-failure message naming the settlement action,
        // not the handler, as having failed.
        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Settling", StringComparison.OrdinalIgnoreCase) &&
                                               state.ToString()!.Contains("settle-fails")),
            It.Is<Exception>(ex => ex == settlementException),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task HandlerThrows_AbandonAlsoThrows_OriginalExceptionStillPropagates()
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<ServiceBusConsumerContext>>();
        var handlerException = new InvalidOperationException("handler blew up");
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<ServiceBusConsumerContext>(), It.IsAny<IServiceResolver>()))
            .ThrowsAsync(handlerException);

        var (worker, logger) = CreateWorker(mockPipeline.Object);

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "boom");

        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver
            .Setup(x => x.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("abandon also blew up"));

        var args = new ProcessMessageEventArgs(message, mockReceiver.Object, CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeOnProcessMessageAsync(worker, args));

        // The original handler exception - not the abandon failure - is what propagates.
        Assert.Same(handlerException, thrown);

        // Both failures are logged: the original processing failure, and the abandon failure.
        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => true),
            It.Is<Exception>(ex => ex == handlerException),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => true),
            It.Is<Exception>(ex => ex != null && ex.Message == "abandon also blew up"),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}
