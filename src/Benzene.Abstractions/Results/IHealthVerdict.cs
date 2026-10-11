namespace Benzene.Abstractions.Results;

/// <summary>
/// A payload that carries a health verdict: the health response of the reserved health topics
/// (wire-contracts.md §5). Implemented by <c>Benzene.HealthChecks.Core.HealthCheckResponse</c>.
/// </summary>
/// <remarks>
/// <para>
/// A health query that ran its checks is a <i>successful</i> query whatever it found: its result's status
/// is <c>ok</c>, and <see cref="IsHealthy"/> carries the verdict. Transports that have a status convention
/// of their own map the verdict onto it - the HTTP binding answers a route mapped to a health topic with
/// <b>503</b> when <see cref="IsHealthy"/> is <c>false</c> (transport-bindings.md §1), the one place it
/// maps a payload field rather than the result's status, so load balancers and orchestrators that read
/// only the status line see the verdict.
/// </para>
/// <para>
/// It lives in the root abstractions so an HTTP binding can read the verdict without referencing the
/// health-check packages.
/// </para>
/// </remarks>
public interface IHealthVerdict
{
    /// <summary>
    /// <c>true</c> unless a check that decides health failed - a warning does not flip it.
    /// </summary>
    bool IsHealthy { get; }
}
