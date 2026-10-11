namespace Benzene.HealthChecks.Core;

/// <summary>The aggregated outcome of running every registered <see cref="IHealthCheck"/>.</summary>
/// <typeparam name="THealthCheckResult">The concrete result type each individual check reported.</typeparam>
/// <remarks>
/// The verdict is <see cref="IsHealthy"/>, not the result's status: a health query whose checks ran is
/// <c>ok</c> whatever they found (wire-contracts.md §5). Through
/// <see cref="Benzene.Abstractions.Results.IHealthVerdict"/> an HTTP binding maps an unhealthy verdict to 503.
/// </remarks>
public interface IHealthCheckResponse<THealthCheckResult> : Benzene.Abstractions.Results.IHealthVerdict
    where THealthCheckResult : IHealthCheckResult
{
    /// <summary>
    /// <c>true</c> unless at least one check reported <see cref="HealthCheckStatus.Failed"/> - a
    /// <see cref="HealthCheckStatus.Warning"/> result does not flip this to <c>false</c> (see
    /// <c>Benzene.HealthChecks.HealthCheckProcessor.PerformHealthChecksAsync</c> for the exact rule).
    /// </summary>
    new bool IsHealthy { get; }

    /// <summary>Every check's result, keyed by its <see cref="IHealthCheck.Type"/>.</summary>
    IDictionary<string, THealthCheckResult> HealthChecks { get; }
}
