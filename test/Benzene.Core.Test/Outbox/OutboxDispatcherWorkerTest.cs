using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Outbox;
using Moq;
using Xunit;

namespace Benzene.Test.Outbox;

public class OutboxDispatcherWorkerTest
{
    [Fact]
    public async Task StartAsync_ThenStopAsync_StartsAndStopsCleanly()
    {
        var runSignal = new TaskCompletionSource();
        var dispatcher = new Mock<IOutboxDispatcher>();
        dispatcher
            .Setup(d => d.RunOnceAsync(It.IsAny<CancellationToken>()))
            .Callback(() => runSignal.TrySetResult())
            .ReturnsAsync(new OutboxDispatchResult(0, 0, 0, 0));

        var worker = new OutboxDispatcherWorker(dispatcher.Object, new OutboxOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(runSignal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(runSignal.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Worker_PollsRepeatedly_OnTheConfiguredInterval()
    {
        var callCount = 0;
        var secondCallSignal = new TaskCompletionSource();
        var dispatcher = new Mock<IOutboxDispatcher>();
        dispatcher
            .Setup(d => d.RunOnceAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (Interlocked.Increment(ref callCount) >= 2)
                {
                    secondCallSignal.TrySetResult();
                }
            })
            .ReturnsAsync(new OutboxDispatchResult(0, 0, 0, 0));

        var worker = new OutboxDispatcherWorker(dispatcher.Object, new OutboxOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(secondCallSignal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task Worker_ARunThatThrows_IsSwallowedAndTheLoopContinuesPolling()
    {
        var callCount = 0;
        var thirdCallSignal = new TaskCompletionSource();
        var dispatcher = new Mock<IOutboxDispatcher>();
        dispatcher
            .Setup(d => d.RunOnceAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (Interlocked.Increment(ref callCount) >= 3)
                {
                    thirdCallSignal.TrySetResult();
                }
            })
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var worker = new OutboxDispatcherWorker(dispatcher.Object, new OutboxOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(thirdCallSignal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await worker.StopAsync(CancellationToken.None);

        // The loop survived multiple failing runs rather than dying after the first.
        Assert.True(callCount >= 3);
    }

    [Fact]
    public async Task StopAsync_WithoutStartAsync_DoesNotThrow()
    {
        var dispatcher = new Mock<IOutboxDispatcher>();
        var worker = new OutboxDispatcherWorker(dispatcher.Object, new OutboxOptions());

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispose_AfterStopAsync_DoesNotThrow_AndIsIdempotent()
    {
        var dispatcher = new Mock<IOutboxDispatcher>();
        dispatcher
            .Setup(d => d.RunOnceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutboxDispatchResult(0, 0, 0, 0));
        var worker = new OutboxDispatcherWorker(dispatcher.Object, new OutboxOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        worker.Dispose();
        worker.Dispose(); // Disposing twice must not throw (ObjectDisposedException from the CTS).
    }

    [Fact]
    public void Dispose_WithoutEverStarting_DoesNotThrow()
    {
        var dispatcher = new Mock<IOutboxDispatcher>();
        var worker = new OutboxDispatcherWorker(dispatcher.Object, new OutboxOptions());

        worker.Dispose();
    }
}
