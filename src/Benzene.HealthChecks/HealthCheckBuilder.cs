using Benzene.Abstractions.DI;
using Benzene.Core;
using Benzene.HealthChecks.Core;

namespace Benzene.HealthChecks;

/// <summary>
/// Default <see cref="IHealthCheckBuilder"/> implementation. Health checks registered via the
/// <c>THealthCheck</c> overload are registered as scoped services against <see cref="IHealthCheckFinder"/>
/// (constructing an <see cref="HealthCheckFinder"/> and wiring it as a scoped service on first use); health
/// checks registered via the factory-function overload are held in-memory and lazily constructed at
/// resolution time.
/// </summary>
public class HealthCheckBuilder : IHealthCheckBuilder
{
    private readonly List<Func<IServiceResolver, IHealthCheck>> _healthCheckBuilders = new();
    private readonly IRegisterDependency _register;

    /// <summary>Initializes a new instance of the <see cref="HealthCheckBuilder"/> class, registering the scoped <see cref="IHealthCheckFinder"/> used to discover container-resolved checks.</summary>
    /// <param name="register">The dependency registry checks/services are registered against.</param>
    public HealthCheckBuilder(IRegisterDependency register)
    {
        _register = register;
        // Scoped, not singleton. HealthCheckFinder takes IEnumerable<IHealthCheck> and
        // IEnumerable<IDependencyHealthCheck>, both registered scoped — as a singleton it captured the
        // first scope's checks for the life of the process. That is a captive dependency, and the
        // reason ServiceProviderOptions.ValidateScopes could not be turned on. Its only consumer is
        // GetHealthChecks(resolver), which already has a scope in hand.
        _register.Register(x => x.AddScoped<IHealthCheckFinder, HealthCheckFinder>());
        // Registered with TryAdd so a consumer can register their own IHealthCheckProcessor first
        // (e.g. with a non-default timeout) and have it win.
        _register.Register(x => x.TryAddSingleton<IHealthCheckProcessor>(_ => new HealthCheckProcessor()));
        // Scoped cancellation-token accessor so a check can observe the ambient token. TryAdd, and
        // mapped so the same scoped instance is resolvable as both the concrete (settable, for a
        // seeder) and the read-only interface (for checks).
        _register.Register(x => x
            .TryAddScoped<CancellationTokenAccessor>()
            .TryAddScoped<ICancellationTokenAccessor>(r => r.GetService<CancellationTokenAccessor>()));
    }

    /// <inheritdoc />
    public IHealthCheckBuilder AddHealthCheck<THealthCheck>() where THealthCheck : class, IHealthCheck
    {
        _register.Register(x => x.AddScoped<IHealthCheck, THealthCheck>());
        return this;
    }

    /// <inheritdoc />
    public IHealthCheckBuilder AddHealthCheck(Func<IServiceResolver, IHealthCheck> func)
    {
        _healthCheckBuilders.Add(func);
        return this;
    }

    /// <summary>
    /// Combines the checks registered via <see cref="AddHealthCheck{THealthCheck}"/> (resolved through
    /// the registered <see cref="IHealthCheckFinder"/>) with the checks registered via
    /// <see cref="AddHealthCheck(Func{IServiceResolver,IHealthCheck})"/> (each deferred so the factory
    /// is not invoked until the aggregated array is executed).
    /// </summary>
    /// <param name="resolver">The service resolver used to resolve container-registered checks and to invoke the factory functions.</param>
    /// <returns>Every registered health check, factory-based checks first, followed by container-resolved checks.</returns>
    public IHealthCheck[] GetHealthChecks(IServiceResolver resolver)
    {
        return GetHealthChecks(resolver, includeDependencyChecks: true);
    }

    /// <summary>
    /// Resolves the registered checks for a specific probe scope. The builder-local factory checks and the
    /// plain container-registered checks are always included; the dependency-category checks
    /// (<see cref="IDependencyHealthCheck"/>) are included only when <paramref name="includeDependencyChecks"/>
    /// is <c>true</c> - so a liveness or readiness probe never harvests an auto-wired dependency check (§3.2).
    /// </summary>
    /// <param name="resolver">The service resolver used to resolve container-registered checks and to invoke the factory functions.</param>
    /// <param name="includeDependencyChecks">Whether to include the dependency-category checks.</param>
    /// <returns>The health checks for the requested scope: factory-based checks first, then plain container checks, then (optionally) dependency-category checks.</returns>
    public IHealthCheck[] GetHealthChecks(IServiceResolver resolver, bool includeDependencyChecks)
    {
        var healthCheckFinder = resolver.GetService<IHealthCheckFinder>();
        var healthChecks = healthCheckFinder.FindHealthChecks();
        // Not InlineHealthCheck here: its Func<Task<IHealthCheckResult>> delegate predates per-call
        // cancellation and takes no token, which would silently drop cancellation for every
        // factory-registered check (a very common registration path, e.g.
        // AddHealthCheck(resolver => new SqsHealthCheck(...))). FactoryHealthCheck defers construction
        // the same way InlineHealthCheck did, but forwards the real token into the resolved check's
        // own ExecuteAsync.
        var factoryHealthChecks = _healthCheckBuilders
            .Select(x => (IHealthCheck)new FactoryHealthCheck(x, resolver)).ToArray();

        var combined = factoryHealthChecks.Concat(healthChecks);
        if (includeDependencyChecks)
        {
            combined = combined.Concat(healthCheckFinder.FindDependencyHealthChecks());
        }

        return combined.ToArray();
    }

    // Lazily resolves and runs a factory-registered IHealthCheck at execution time (not registration
    // time), forwarding the real CancellationToken into it - the counterpart of what InlineHealthCheck
    // does for a bare result-producing delegate, but for a delegate that produces a full IHealthCheck.
    // Type is intentionally empty (matching the prior InlineHealthCheck-based wrapping): the wrapped
    // check's own Type is not known until it is constructed at execution time.
    private sealed class FactoryHealthCheck : IHealthCheck
    {
        private readonly Func<IServiceResolver, IHealthCheck> _factory;
        private readonly IServiceResolver _resolver;

        public FactoryHealthCheck(Func<IServiceResolver, IHealthCheck> factory, IServiceResolver resolver)
        {
            _factory = factory;
            _resolver = resolver;
        }

        public string Type => string.Empty;

        public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
            => _factory(_resolver).ExecuteAsync(cancellationToken);
    }
}
