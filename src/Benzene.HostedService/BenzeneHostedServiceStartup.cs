using Benzene.Abstractions.Hosting;
using Benzene.SelfHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Benzene.HostedService;

/// <summary>
/// Adapts an <see cref="IBenzeneWorker"/> to <see cref="IHostedService"/>.
/// </summary>
/// <remarks>
/// <see cref="IBenzeneWorker.StartAsync"/>'s contract is "the returned task completes when the worker
/// stops" - some implementations (<c>BenzeneKafkaWorker</c>) already background their run loop and
/// return promptly, but others (<c>SqsConsumer</c>) run their loop directly on that task, which
/// doesn't return until cancelled. <see cref="IHostedService.StartAsync"/> has a narrower contract:
/// the .NET generic host awaits each registered hosted service's <c>StartAsync</c> IN TURN before
/// starting the next one (<see cref="IHost"/> does not start hosted services concurrently by
/// default), so a hosted service whose <c>StartAsync</c> never returns starves every hosted service
/// registered after it - including ASP.NET Core's own Kestrel listener, if a Benzene worker shares a
/// process with <c>Benzene.AspNet.Core</c> (see <c>examples/K8sTransports</c>). This adapter closes
/// that gap the same way <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> does: run the
/// worker on its own linked cancellation, and return immediately unless it already finished.
/// </remarks>
public class BenzeneHostedServiceAdapter : IHostedService, IDisposable
{
    private readonly IBenzeneWorker _benzeneWorker;
    private readonly ILogger<BenzeneHostedServiceAdapter>? _logger;
    private readonly IHostApplicationLifetime? _hostApplicationLifetime;
    private Task? _executingTask;
    private CancellationTokenSource? _stoppingCts;

    /// <param name="benzeneWorker">The worker this adapter runs and observes.</param>
    /// <param name="logger">
    /// Optional. When supplied, an unhandled fault in the worker's own task is logged at
    /// <see cref="LogLevel.Critical"/> as soon as it happens - not just when the host later gets
    /// around to calling <see cref="StopAsync"/> - so a dead worker is never silent. Not every
    /// construction path (e.g. <see cref="BenzeneWorkerExtensions.BuildHostedService"/>) has a
    /// resolver to supply one; the adapter degrades to no logging rather than requiring it.
    /// </param>
    /// <param name="hostApplicationLifetime">
    /// Optional. When supplied, an unhandled worker fault also stops the whole host - matching
    /// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>'s modern default
    /// (<c>BackgroundServiceExceptionBehavior.StopHost</c>) - rather than leaving the process "up"
    /// with a dead worker and every other hosted service none the wiser.
    /// </param>
    public BenzeneHostedServiceAdapter(
        IBenzeneWorker benzeneWorker,
        ILogger<BenzeneHostedServiceAdapter>? logger = null,
        IHostApplicationLifetime? hostApplicationLifetime = null)
    {
        _benzeneWorker = benzeneWorker;
        _logger = logger;
        _hostApplicationLifetime = hostApplicationLifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executingTask = _benzeneWorker.StartAsync(_stoppingCts.Token);

        // Observe a fault on the worker's own task the moment it happens, rather than only when
        // StopAsync eventually gets called (which may be never, if nothing else ever asks the host
        // to stop) - otherwise a dead/crashed worker leaves the process "up" with zero signal. This
        // mirrors BenzeneKafkaWorker's own outer `catch (Exception) { LogCritical }` safety net, but
        // lives once in the shared adapter instead of being duplicated per worker.
        ObserveFault(_executingTask);

        // A worker whose StartAsync already backgrounds itself (e.g. BenzeneKafkaWorker) typically
        // completes this near-instantly anyway; either way, only propagate a task that's ALREADY
        // done (e.g. a synchronous failure) - otherwise return promptly so the host can start the
        // next hosted service (Kestrel included) without waiting on this worker's full lifetime.
        return _executingTask.IsCompleted ? _executingTask : Task.CompletedTask;
    }

    // Fire-and-forget on purpose: this is the observer, not a caller waiting on the worker's result.
    // Awaiting the same Task instance here and again in StartAsync/StopAsync is safe - a .NET Task
    // supports any number of independent awaiters.
    private async void ObserveFault(Task executingTask)
    {
        try
        {
            await executingTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex,
                "Benzene worker {WorkerType} faulted; the worker has stopped running.",
                _benzeneWorker.GetType().Name);

            // Match BackgroundService's modern default (BackgroundServiceExceptionBehavior.StopHost):
            // an unhandled worker fault stops the whole host, rather than leaving the process up
            // with a silently dead worker.
            _hostApplicationLifetime?.StopApplication();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executingTask == null)
        {
            return;
        }

        try
        {
            _stoppingCts!.Cancel();
        }
        finally
        {
            // Wait for the worker's own task to unwind, but no longer than the host's stop timeout
            // (the cancellationToken passed in here) - a worker that ignores cancellation shouldn't
            // hang shutdown forever.
            var stopTimeoutTcs = new TaskCompletionSource<object>();
            using var registration = cancellationToken.Register(
                state => ((TaskCompletionSource<object>)state!).TrySetResult(null!), stopTimeoutTcs);
            await Task.WhenAny(_executingTask, stopTimeoutTcs.Task).ConfigureAwait(false);
        }

        // Note: a worker fault is already logged (and the host optionally stopped) by ObserveFault
        // above, the moment it happens - not duplicated here, so a worker that had already crashed
        // long before shutdown doesn't log twice.

        // The worker's own StopAsync is where real drain/close logic lives (e.g. BenzeneKafkaWorker
        // waits for its run task and closes the consumer); cancelling above is enough on its own for
        // a worker like SqsConsumer whose StopAsync is a no-op.
        await _benzeneWorker.StopAsync(cancellationToken);
    }

    public void Dispose()
    {
        _stoppingCts?.Cancel();
        _stoppingCts?.Dispose();
    }
}

public static class BenzeneWorkerExtensions
{
    public static BenzeneHostedServiceAdapter BuildHostedService(this IBenzeneWorkerBuilder source)
    {
        return new BenzeneHostedServiceAdapter(source.Build());
    }
}
