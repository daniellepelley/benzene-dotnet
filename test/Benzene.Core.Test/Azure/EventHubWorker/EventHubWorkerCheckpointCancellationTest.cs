using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.EventHub;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Azure.EventHubWorker;

/// <summary>
/// Regression for #116: checkpointing a successfully-handled event is part of graceful drain, so it
/// must run under <see cref="CancellationToken.None"/> and must never let an
/// <see cref="OperationCanceledException"/> (or any other exception) escape
/// <c>OnProcessEventAsync</c> unhandled - per the SDK's docs, <c>args.CancellationToken</c> is
/// cancelled by <c>StopProcessingAsync</c> while an in-flight handler is still being awaited, and an
/// unhandled exception out of the event handler faults the partition-processing task (and can crash
/// the process on some hosts).
/// </summary>
public class EventHubWorkerCheckpointCancellationTest
{
    private static (BenzeneEventHubWorker Worker, Mock<ILogger<BenzeneEventHubWorker>> Logger) CreateWorker(BenzeneEventHubConfig config)
    {
        var mockPipeline = new Mock<IMiddlewarePipeline<EventHubConsumerContext>>();
        mockPipeline.Setup(x => x.HandleAsync(It.IsAny<EventHubConsumerContext>(), It.IsAny<IServiceResolver>()))
            .Callback<EventHubConsumerContext, IServiceResolver>((context, _) =>
            {
                context.MessageResult = BenzeneResult.Ok();
            })
            .Returns(Task.CompletedTask);

        var mockLogger = new Mock<ILogger<BenzeneEventHubWorker>>();

        var mockResolver = new Mock<IServiceResolver>();
        mockResolver.Setup(x => x.GetService<ISetCurrentTransport>()).Returns(Mock.Of<ISetCurrentTransport>());
        mockResolver.Setup(x => x.GetService<ILogger<BenzeneEventHubWorker>>()).Returns(mockLogger.Object);
        var mockResolverFactory = new Mock<IServiceResolverFactory>();
        mockResolverFactory.Setup(x => x.CreateScope()).Returns(mockResolver.Object);

        var worker = new BenzeneEventHubWorker(mockResolverFactory.Object,
            new EventHubConsumerApplication(mockPipeline.Object), config, Mock.Of<IEventProcessorClientFactory>());

        return (worker, mockLogger);
    }

    private static Task InvokeOnProcessEventAsync(BenzeneEventHubWorker worker, ProcessEventArgs args)
    {
        var method = typeof(BenzeneEventHubWorker).GetMethod("OnProcessEventAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(worker, new object[] { args })!;
    }

    [Fact]
    public async Task HandlerSucceeds_ArgsTokenAlreadyCancelled_ChecksInsAnyway_NoExceptionEscapes()
    {
        // Simulates the documented SDK state after StopProcessingAsync is called while this event's
        // handler is still in flight: args.CancellationToken has already fired by the time the
        // handler completes successfully and the checkpoint runs. Before the fix, the checkpoint was
        // gated on that same (cancelled) token, so it threw OperationCanceledException, which used to
        // propagate unhandled out of OnProcessEventAsync. After the fix, the checkpoint uses
        // CancellationToken.None, so it goes through even though args.CancellationToken is cancelled.
        var (worker, logger) = CreateWorker(new BenzeneEventHubConfig());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var checkpointed = false;
        CancellationToken? observedCheckpointToken = null;
        var args = new ProcessEventArgs(
            EventHubsModelFactory.PartitionContext("0"),
            new EventData(new BinaryData("some-message")),
            ct =>
            {
                observedCheckpointToken = ct;
                ct.ThrowIfCancellationRequested();
                checkpointed = true;
                return Task.CompletedTask;
            },
            cts.Token);

        // Must not throw: an escaped exception here would fault the partition-processing task.
        await InvokeOnProcessEventAsync(worker, args);

        Assert.True(checkpointed);
        Assert.NotNull(observedCheckpointToken);
        Assert.False(observedCheckpointToken!.Value.IsCancellationRequested);

        logger.Verify(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => true),
            It.Is<Exception>(ex => ex is OperationCanceledException),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Fact]
    public async Task HandlerSucceeds_CheckpointStoreThrowsOce_LoggedAsInformation_NotError_WorkerNotStopped()
    {
        // Even with the fix passing CancellationToken.None, defend against a checkpoint call that
        // still throws OperationCanceledException for some other reason (e.g. a cancellation-aware
        // checkpoint store escalating a shutdown it detected some other way): this is a shutdown-time
        // skip, not a failure, so it logs at Information (not Error) and does not stop the worker
        // (CatchHandlerExceptions = false would otherwise stop it on a genuine failure).
        var (worker, logger) = CreateWorker(new BenzeneEventHubConfig { CatchHandlerExceptions = false });

        var args = new ProcessEventArgs(
            EventHubsModelFactory.PartitionContext("0"),
            new EventData(new BinaryData("some-message")),
            _ => throw new OperationCanceledException("checkpoint store shutting down"),
            CancellationToken.None);

        await InvokeOnProcessEventAsync(worker, args);

        logger.Verify(x => x.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("skipped due to shutdown")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => true),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);

        var stopInitiated = typeof(BenzeneEventHubWorker)
            .GetField("_stopInitiated", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(worker);
        Assert.Equal(0, stopInitiated);
    }

    [Fact]
    public async Task HandlerSucceeds_CheckpointStoreThrowsOrdinaryException_LoggedAsError_RoutedThroughCatchHandlerExceptionsPolicy()
    {
        // A genuine (non-cancellation) checkpoint-store failure - throttling, a network blip - must
        // never escape OnProcessEventAsync unhandled (bypassing CatchHandlerExceptions entirely); it
        // goes through the same stop-or-continue policy as every other failure in this file.
        var (worker, logger) = CreateWorker(new BenzeneEventHubConfig { CatchHandlerExceptions = false });

        var args = new ProcessEventArgs(
            EventHubsModelFactory.PartitionContext("0"),
            new EventData(new BinaryData("some-message")),
            _ => throw new InvalidOperationException("checkpoint store unavailable"),
            CancellationToken.None);

        await InvokeOnProcessEventAsync(worker, args);

        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => true),
            It.Is<Exception>(ex => ex is InvalidOperationException),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        var stopInitiated = typeof(BenzeneEventHubWorker)
            .GetField("_stopInitiated", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(worker);
        Assert.Equal(1, stopInitiated);
    }
}
