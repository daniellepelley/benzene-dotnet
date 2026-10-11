using Benzene.Abstractions.Results;

namespace Benzene.HealthChecks.Core;

/// <summary>
/// Runs a set of <see cref="IHealthCheck"/>s and aggregates their outcomes into a single result.
/// Injectable so the timeout (and, in future, tags/subset execution) can be configured without a
/// breaking change to the execution engine.
/// </summary>
public interface IHealthCheckProcessor
{
    /// <summary>
    /// Runs every check (each under a timeout and exception isolation), captures each check's
    /// duration, and returns the aggregated <c>HealthCheckResponse</c> with status
    /// <see cref="BenzeneResultStatus.Ok"/> - whether or not the service is healthy (wire-contracts.md
    /// §5: the checks ran, so the health query succeeded). The verdict is the payload's <c>IsHealthy</c>;
    /// the HTTP binding maps <c>IsHealthy: false</c> to 503.
    /// </summary>
    /// <remarks>
    /// <see cref="PerformHealthChecksAsync"/> takes no ambient <see cref="System.Threading.CancellationToken"/> -
    /// the only cancellation signal that reaches an individual check is the timeout it's wrapped in
    /// (<c>TimeOutHealthCheck</c>: the check's own <see cref="IHealthCheck.Timeout"/> override, or the
    /// processor's default). A slow custom <see cref="IHealthCheck"/> will not observe a caller
    /// disconnecting or the host shutting down/draining early - it always runs to its own timeout
    /// regardless, even during a graceful drain.
    /// </remarks>
    Task<IBenzeneResult> PerformHealthChecksAsync(IHealthCheck[] healthChecks);
}
