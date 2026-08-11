using Benzene.Abstractions.Hosting;
using Benzene.SelfHost;
using Microsoft.Extensions.Hosting;

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
    private Task? _executingTask;
    private CancellationTokenSource? _stoppingCts;

    public BenzeneHostedServiceAdapter(IBenzeneWorker benzeneWorker)
    {
        _benzeneWorker = benzeneWorker;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executingTask = _benzeneWorker.StartAsync(_stoppingCts.Token);

        // A worker whose StartAsync already backgrounds itself (e.g. BenzeneKafkaWorker) typically
        // completes this near-instantly anyway; either way, only propagate a task that's ALREADY
        // done (e.g. a synchronous failure) - otherwise return promptly so the host can start the
        // next hosted service (Kestrel included) without waiting on this worker's full lifetime.
        return _executingTask.IsCompleted ? _executingTask : Task.CompletedTask;
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
