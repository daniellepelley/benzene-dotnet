using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.SelfHost;
using Xunit;

namespace Benzene.Test.Hosting;

public class CompositeBenzeneWorkerTest
{
    private class FakeWorker : IBenzeneWorker
    {
        private readonly bool _throwOnStart;
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public FakeWorker(bool throwOnStart = false)
        {
            _throwOnStart = throwOnStart;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_throwOnStart)
            {
                throw new InvalidOperationException("boom");
            }

            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StartAsync_WhenAWorkerFails_RollsBackTheStartedWorkers()
    {
        var good = new FakeWorker();
        var bad = new FakeWorker(throwOnStart: true);
        var composite = new CompositeBenzeneWorker(new IBenzeneWorker[] { good, bad });

        await Assert.ThrowsAsync<InvalidOperationException>(() => composite.StartAsync(CancellationToken.None));

        Assert.True(good.Started);
        Assert.True(good.Stopped); // rolled back so a partial start doesn't leak a running worker
    }

    [Fact]
    public async Task StartAsync_WhenAllSucceed_DoesNotStopAnyWorker()
    {
        var first = new FakeWorker();
        var second = new FakeWorker();
        var composite = new CompositeBenzeneWorker(new IBenzeneWorker[] { first, second });

        await composite.StartAsync(CancellationToken.None);

        Assert.True(first.Started);
        Assert.True(second.Started);
        Assert.False(first.Stopped);
        Assert.False(second.Stopped);
    }

    // Mirrors SqsConsumer.StartAsync's actual shape: runs its full lifetime inline on the
    // returned task, which only completes once cancelled - it never faults or succeeds on its
    // own. This is the shape that let #291 hide a sibling's startup fault forever behind
    // Task.WhenAll, which only ever completes once EVERY constituent task has completed.
    private class LongRunningWorker : IBenzeneWorker
    {
        public bool Stopped { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();
            using var registration = cancellationToken.Register(() => tcs.TrySetResult());
            await tcs.Task;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private class ImmediatelyFailingWorker : IBenzeneWorker
    {
        private readonly Exception _exception;

        public ImmediatelyFailingWorker(Exception? exception = null)
        {
            _exception = exception ?? new InvalidOperationException("bad connection string");
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.FromException(_exception);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // A worker that starts cleanly (its StartAsync task stays pending, exactly like
    // LongRunningWorker) but later faults mid-lifetime rather than at startup.
    private class LateFaultingWorker : IBenzeneWorker
    {
        private readonly TaskCompletionSource _tcs = new();
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => _tcs.Task;

        public void Fault(Exception exception) => _tcs.TrySetException(exception);

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StartAsync_LongRunningSiblingFailsToStart_FaultsPromptlyAndStopsTheLongRunningSibling()
    {
        var longRunning = new LongRunningWorker();
        var failing = new ImmediatelyFailingWorker();
        var composite = new CompositeBenzeneWorker(new IBenzeneWorker[] { longRunning, failing });

        var startTask = composite.StartAsync(CancellationToken.None);

        var completedWithinTimeout =
            await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(5))) == startTask;

        Assert.True(completedWithinTimeout,
            "expected StartAsync to fault promptly instead of hanging behind the never-completing " +
            "long-running sibling (#291)");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => startTask);
        Assert.Equal("bad connection string", thrown.Message);

        // The rollback predicate must stop a sibling that is still running - not only one that
        // already completed successfully - otherwise this exact long-running shape is skipped.
        Assert.True(longRunning.Stopped);
    }

    [Fact]
    public async Task StartAsync_SiblingFaultsAfterStartingSuccessfully_FaultsPromptlyAndRollsBackTheSibling()
    {
        var lateFaulting = new LateFaultingWorker();
        var goodButLongRunning = new LongRunningWorker();
        var composite = new CompositeBenzeneWorker(new IBenzeneWorker[] { goodButLongRunning, lateFaulting });

        var startTask = composite.StartAsync(CancellationToken.None);

        // Give both workers a moment to be "running" before the mid-lifetime fault - this is not
        // a startup-time fault, it happens after the composite has already started everyone.
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        lateFaulting.Fault(new InvalidOperationException("connection dropped"));

        var completedWithinTimeout =
            await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(5))) == startTask;

        Assert.True(completedWithinTimeout,
            "expected a mid-lifetime fault on one sibling to be raced and surfaced promptly too");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => startTask);
        Assert.Equal("connection dropped", thrown.Message);
        Assert.True(goodButLongRunning.Stopped);
    }
}
