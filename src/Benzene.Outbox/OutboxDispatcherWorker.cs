using Benzene.Abstractions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benzene.Outbox;

/// <summary>
/// A self-hosted poll-loop relay for <see cref="IOutboxDispatcher"/>: calls
/// <see cref="IOutboxDispatcher.RunOnceAsync"/> on a fixed interval
/// (<see cref="OutboxOptions.PollInterval"/>). Suitable for <c>Benzene.HostedService</c> (the .NET
/// generic host) or <c>Benzene.SelfHost</c>; this package stays host-agnostic and does not depend on
/// either - wire this worker into whichever host you use (see <c>Benzene.Outbox/CLAUDE.md</c> for both
/// examples).
/// </summary>
/// <remarks>
/// For AWS Lambda, where no background thread exists, this worker is NOT the answer - see the
/// DynamoDB-Streams-plus-scheduled-sweep relay pattern documented in <c>Benzene.Outbox/CLAUDE.md</c>
/// and built out in <c>Benzene.Outbox.DynamoDb</c> (Phase 2).
/// </remarks>
public class OutboxDispatcherWorker : IBenzeneWorker
{
    private readonly IOutboxDispatcher _dispatcher;
    private readonly OutboxOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task? _runTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxDispatcherWorker"/> class.
    /// </summary>
    /// <param name="dispatcher">The dispatch engine this worker polls.</param>
    /// <param name="options">Supplies <see cref="OutboxOptions.PollInterval"/>.</param>
    /// <param name="logger">The logger used to record a failed run. Defaults to <see cref="NullLogger.Instance"/>.</param>
    public OutboxDispatcherWorker(IOutboxDispatcher dispatcher, OutboxOptions options, ILogger? logger = null)
    {
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Starts the poll loop on a background task and returns immediately - it does not wait for the
    /// loop to run to completion. Use <see cref="StopAsync"/> to signal shutdown and wait for an
    /// in-flight <see cref="IOutboxDispatcher.RunOnceAsync"/> call to finish.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingCts.Token);
        var runToken = linkedCts.Token;

        _runTask = Task.Run(async () =>
        {
            try
            {
                while (!runToken.IsCancellationRequested)
                {
                    try
                    {
                        await _dispatcher.RunOnceAsync(runToken);
                    }
                    catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // A single failed run (a store outage, say) should not kill the worker - log
                        // and try again after the poll interval, same as any other transient failure
                        // this loop can recover from on its own.
                        _logger.LogError(ex, "Outbox dispatcher run failed; retrying after the poll interval.");
                    }

                    try
                    {
                        await Task.Delay(_options.PollInterval, runToken);
                    }
                    catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            finally
            {
                linkedCts.Dispose();
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals the poll loop to stop and waits for an in-flight run to finish (graceful drain,
    /// mirroring how the codebase's other workers stop).
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts.Cancel();

        if (_runTask != null)
        {
            await _runTask;
        }
    }
}
