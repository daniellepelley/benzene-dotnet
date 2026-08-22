using Benzene.Abstractions.Middleware;
using Benzene.HealthChecks.Core;
using Benzene.Mesh.Contracts;
using Microsoft.Extensions.Logging;

namespace Benzene.Mesh.Reporting;

/// <summary>
/// After a real request/message completes, opportunistically publishes this service's current
/// spec/health via the injected <see cref="IMeshReportPublisher"/> - piggybacking on real traffic
/// rather than a dedicated scheduled/keep-warm reporter, so an on-demand host (e.g. AWS Lambda)
/// only ever pays for reporting it would have paid for anyway. Throttled by
/// <see cref="MeshSelfReportOptions.MinimumInterval"/> (tracked in <see cref="MeshSelfReportState"/>)
/// so a hot service doesn't publish on every single invocation.
/// </summary>
/// <remarks>
/// Publishing is fire-and-forget and fully best-effort to the CALLER: it never delays the response
/// this middleware wraps, and a publish failure (or a spec/health provider throwing) never propagates
/// to the primary request/message path. It is not silent to an operator, though - the swallowed
/// exception is logged (when a logger is available), the same "non-fatal to the caller, but never
/// invisible" treatment <c>Benzene.Mesh.Artifacts.MeshDispatchGuardMiddleware</c> and
/// <c>Benzene.Mesh.Auth.Oidc.OidcCallbackMiddleware</c> already give their own non-fatal failures - a
/// misconfigured reporter (wrong URL, expired credential, ...) should be diagnosable from the logs,
/// not discovered by noticing the dashboard never updates.
/// </remarks>
public class MeshSelfReportMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly IMeshReportPublisher _publisher;
    private readonly MeshSelfReportOptions _options;
    private readonly MeshSelfReportState _state;
    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance of the <see cref="MeshSelfReportMiddleware{TContext}"/> class.</summary>
    /// <param name="publisher">Publishes the report this middleware builds.</param>
    /// <param name="options">How to name this service, build its report, and throttle publishing.</param>
    /// <param name="state">Tracks the last publish time, shared across requests within this process.</param>
    /// <param name="logger">
    /// Logs a swallowed publish failure. Optional (like <c>Benzene.Mesh.Artifacts.MeshDispatchGuardMiddleware</c>'s
    /// own logger) so a host with no logging configured still works - it just reports nothing when a
    /// publish fails, exactly as before this was added.
    /// </param>
    public MeshSelfReportMiddleware(
        IMeshReportPublisher publisher, MeshSelfReportOptions options, MeshSelfReportState state, ILogger? logger = null)
    {
        _publisher = publisher;
        _options = options;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "MeshSelfReport";

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        await next();

        if (ShouldPublish())
        {
            _ = PublishBestEffortAsync();
        }
    }

    private bool ShouldPublish()
    {
        var lastPublishedAtUtc = _state.LastPublishedAtUtc;
        return lastPublishedAtUtc == null || DateTimeOffset.UtcNow - lastPublishedAtUtc.Value >= _options.MinimumInterval;
    }

    private async Task PublishBestEffortAsync()
    {
        // Set before attempting the publish, not after - avoids a burst of concurrent requests all
        // seeing "no recent publish" and racing to publish simultaneously. A failed attempt still
        // counts toward the throttle window, deliberately, to avoid a retry storm.
        _state.LastPublishedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            string? specJson = null;
            HealthCheckResponse? health = null;
            string? error = null;

            try
            {
                specJson = await _options.SpecProvider();
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
            }

            try
            {
                health = await _options.HealthProvider();
            }
            catch (Exception ex)
            {
                error ??= ex.GetType().Name;
            }

            var report = new MeshServiceReport(_options.ServiceName, DateTimeOffset.UtcNow, specJson, health, error);
            await _publisher.PublishAsync(report);
        }
        catch (Exception ex)
        {
            // Best-effort side-channel telemetry - a publish failure must never affect (or even be
            // visible to) the primary request/message path this middleware wraps. Logged server-side
            // regardless, so a misconfigured reporter fails loudly in the logs instead of totally
            // silently - see the class remarks.
            _logger?.LogWarning(ex, "Mesh self-report publish failed for service {Service}", _options.ServiceName);
        }
    }
}
