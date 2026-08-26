using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.HostedService;
using Benzene.Test.Logging.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Benzene.Test.Hosting;

// #88: BenzeneHostedServiceAdapter never observed whether the wrapped worker's task faulted, so a
// dead/crashed worker left the process "up" with zero signal - unlike BackgroundService's
// BackgroundServiceExceptionBehavior (default: stop the host). The fix: the adapter observes an
// unhandled fault on the worker's own task as soon as it happens, logs it at Critical, and (when an
// IHostApplicationLifetime is available) stops the whole host - matching BackgroundService's modern
// default. Both are opt-in via optional constructor parameters, since not every construction path
// (e.g. BuildHostedService(this IBenzeneWorkerBuilder)) has a resolver to supply them.
public class BenzeneHostedServiceAdapterFaultTest
{
    // Faults ASYNCHRONOUSLY - after StartAsync has already returned an incomplete task - so this
    // models a worker that starts fine and crashes while running, the scenario #88 is about. (A
    // worker that faults SYNCHRONOUSLY at start is a different, already-handled case: the adapter's
    // existing "only propagate a task that's ALREADY done" check surfaces that immediately from
    // StartAsync itself.)
    private class AsyncFaultingWorker : IBenzeneWorker
    {
        private readonly Exception _exception;

        public AsyncFaultingWorker(Exception exception)
        {
            _exception = exception;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(10, CancellationToken.None);
            throw _exception;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        public bool StopApplicationCalled { get; private set; }
        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() => StopApplicationCalled = true;
    }

    [Fact]
    public async Task StartAsync_WorkerFaults_LogsCriticalAndStopsTheHost()
    {
        var collector = new FakeLogCollector();
        var logger = new FakeLogger<BenzeneHostedServiceAdapter>(collector);
        var lifetime = new FakeHostApplicationLifetime();
        var boom = new InvalidOperationException("boom");

        var adapter = new BenzeneHostedServiceAdapter(new AsyncFaultingWorker(boom), logger, lifetime);

        // The worker hasn't faulted yet when StartAsync returns (it's still awaiting its delay), so
        // this must return promptly rather than propagating the not-yet-observed fault.
        await adapter.StartAsync(CancellationToken.None);

        // The fault is observed asynchronously (fire-and-forget continuation) once the worker's task
        // actually transitions to Faulted - give it a moment to run.
        await WaitUntil(() => collector.Entries.Length > 0);

        var entry = Assert.Single(collector.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Same(boom, entry.Exception);
        Assert.True(lifetime.StopApplicationCalled);
    }

    [Fact]
    public async Task StartAsync_WorkerFaults_WithoutLoggerOrLifetime_DoesNotThrow()
    {
        // Neither optional dependency is available (e.g. BuildHostedService's construction path) -
        // the adapter must degrade to doing nothing observable, not throw an unobserved exception.
        var adapter = new BenzeneHostedServiceAdapter(new AsyncFaultingWorker(new InvalidOperationException("boom")));

        await adapter.StartAsync(CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WorkerSucceeds_NeverLogsOrStopsTheHost()
    {
        var collector = new FakeLogCollector();
        var logger = new FakeLogger<BenzeneHostedServiceAdapter>(collector);
        var lifetime = new FakeHostApplicationLifetime();

        var worker = new LongRunningWorker();
        var adapter = new BenzeneHostedServiceAdapter(worker, logger, lifetime);

        await adapter.StartAsync(CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);

        Assert.Empty(collector.Entries);
        Assert.False(lifetime.StopApplicationCalled);
    }

    private class LongRunningWorker : IBenzeneWorker
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
