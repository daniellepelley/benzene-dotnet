using Microsoft.Extensions.Diagnostics.HealthChecks;
using BenzeneHealthCheckStatus = Benzene.HealthChecks.Core.HealthCheckStatus;
using IBenzeneHealthCheck = Benzene.HealthChecks.Core.IHealthCheck;

namespace Benzene.Grpc.AspNet;

/// <summary>
/// Bridges Benzene's own <see cref="IBenzeneHealthCheck"/>s onto ASP.NET Core's health check system, so
/// <c>Grpc.AspNetCore.HealthChecks</c> can surface them over grpc.health.v1's <c>Check</c>/<c>Watch</c>.
/// Registered via <c>services.AddGrpcHealthChecks().AddCheck&lt;BenzeneHealthCheckBridge&gt;("benzene")</c>
/// when <see cref="BenzeneGrpcOptions.EnableHealthChecks"/> is set. Every Benzene check registered in the
/// ASP.NET Core container is executed; the aggregate is unhealthy if any failed, degraded if any warned,
/// healthy otherwise.
/// </summary>
public class BenzeneHealthCheckBridge : IHealthCheck
{
    private readonly IReadOnlyCollection<IBenzeneHealthCheck> _healthChecks;
    private readonly ISet<string>? _includeTypes;

    /// <summary>Bridges every registered Benzene health check.</summary>
    public BenzeneHealthCheckBridge(IEnumerable<IBenzeneHealthCheck> healthChecks)
        : this(healthChecks, null)
    {
    }

    /// <summary>
    /// Bridges only the Benzene health checks whose <see cref="IBenzeneHealthCheck.Type"/> is in
    /// <paramref name="includeTypes"/> (case-sensitive). Null bridges all - used to map a named
    /// grpc.health.v1 service (e.g. "liveness"/"readiness") to a subset of checks.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Round-10 #110: a non-null <paramref name="includeTypes"/> containing a type that matches NO
    /// registered check (a typo'd <c>LivenessCheckTypes</c>/<c>ReadinessCheckTypes</c> entry, most
    /// often) used to fall through <see cref="CheckHealthAsync"/>'s "zero checks matched" branch and
    /// report an unconditional <c>Healthy</c> at every probe - a wiring mistake silently reads as a
    /// passing liveness/readiness service instead of failing loud. Fail fast here instead, at
    /// construction (wiring time - see <c>ServiceCollectionExtensions.AddBenzeneGrpc</c>'s
    /// <c>HealthCheckRegistration</c> factories), matching the same "never silently under-enforced"
    /// principle <c>Benzene.Mesh.Host.MeshAuthGate.Validate</c> applies to its own config.
    /// </exception>
    public BenzeneHealthCheckBridge(IEnumerable<IBenzeneHealthCheck> healthChecks, ISet<string>? includeTypes)
    {
        _healthChecks = healthChecks as IReadOnlyCollection<IBenzeneHealthCheck> ?? healthChecks.ToArray();
        _includeTypes = includeTypes;

        if (includeTypes != null)
        {
            var registeredTypes = new HashSet<string>(_healthChecks.Select(x => x.Type));
            var unmatched = includeTypes.Where(t => !registeredTypes.Contains(t)).ToArray();
            if (unmatched.Length > 0)
            {
                throw new InvalidOperationException(
                    $"No registered Benzene health check has Type '{string.Join("', '", unmatched)}' - " +
                    "check LivenessCheckTypes/ReadinessCheckTypes for a typo. Registered types: " +
                    (registeredTypes.Count > 0 ? string.Join(", ", registeredTypes) : "(none)") + ".");
            }
        }
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var checks = _healthChecks
            .Where(x => _includeTypes == null || _includeTypes.Contains(x.Type))
            .ToArray();
        if (checks.Length == 0)
        {
            return HealthCheckResult.Healthy("No Benzene health checks are registered.");
        }

        var results = await Task.WhenAll(checks.Select(x => x.ExecuteAsync(cancellationToken)));

        // Keyed by Type, suffixed on collision (mirrors Benzene.HealthChecks.HealthCheckNamer's
        // convention - kept as an independent copy here rather than a project reference, per this
        // package's deliberate non-dependency on the full Benzene.HealthChecks pipeline package, see
        // this package's CLAUDE.md) so two checks that happen to share the same Type both appear in
        // the reported data instead of the second silently clobbering the first (round-10 #110).
        var namer = new DuplicateTypeSuffixer();
        var data = new Dictionary<string, object>();
        foreach (var result in results)
        {
            data[namer.GetName(result.Type)] = result.Status;
        }

        if (results.Any(x => x.Status == BenzeneHealthCheckStatus.Failed))
        {
            return HealthCheckResult.Unhealthy("One or more Benzene health checks failed.", data: data);
        }

        if (results.Any(x => x.Status == BenzeneHealthCheckStatus.Warning))
        {
            return HealthCheckResult.Degraded("One or more Benzene health checks reported a warning.", data: data);
        }

        return HealthCheckResult.Healthy("All Benzene health checks passed.", data: data);
    }

    /// <summary>
    /// Assigns unique keys for the <see cref="CheckHealthAsync"/> data dictionary so that multiple
    /// checks with the same (or an empty) <c>Type</c> don't collide - reuses
    /// <c>Benzene.HealthChecks.HealthCheckNamer</c>'s suffixing convention as an independent copy
    /// (this package deliberately doesn't take a project reference on the full
    /// <c>Benzene.HealthChecks</c> pipeline package - see this package's CLAUDE.md): the first
    /// occurrence of a name is returned unchanged, every subsequent collision is suffixed <c>-2</c>,
    /// <c>-3</c>, ... with the suffixed candidate itself reserved too so a later check whose Type
    /// literally equals a generated name can't collide again. Not thread-safe - a new instance is
    /// created per probe.
    /// </summary>
    private sealed class DuplicateTypeSuffixer
    {
        private readonly Dictionary<string, int> _existingNames = new();

        public string GetName(string name)
        {
            var candidateBase = string.IsNullOrEmpty(name) ? "HealthCheck" : name;

            if (_existingNames.TryAdd(candidateBase, 1))
            {
                return candidateBase;
            }

            string candidate;
            do
            {
                _existingNames[candidateBase]++;
                candidate = $"{candidateBase}-{_existingNames[candidateBase]}";
            } while (!_existingNames.TryAdd(candidate, 1));

            return candidate;
        }
    }
}
