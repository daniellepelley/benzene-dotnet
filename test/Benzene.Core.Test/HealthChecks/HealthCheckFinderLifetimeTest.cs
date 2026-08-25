using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.HealthChecks;

/// <summary>
/// <see cref="IHealthCheckFinder"/> must not outlive the scoped checks it holds.
/// </summary>
/// <remarks>
/// <para>
/// It used to be registered as a singleton over <c>IEnumerable&lt;IHealthCheck&gt;</c> and
/// <c>IEnumerable&lt;IDependencyHealthCheck&gt;</c>, both of which are scoped. That is a captive
/// dependency: the first scope's checks are pinned for the life of the process, and a check holding a
/// per-request handle keeps serving from a scope that closed long ago. It fails silently — nothing
/// throws, the answers are just stale.
/// </para>
/// <para>
/// It is also what kept <c>ServiceProviderOptions.ValidateScopes</c> switched off, so this is the test
/// that lets that check stay on.
/// </para>
/// </remarks>
public class HealthCheckFinderLifetimeTest
{
    private sealed class TestRegister : IRegisterDependency
    {
        private readonly IServiceCollection _services;
        public TestRegister(IServiceCollection services) => _services = services;
        public void Register(Action<IBenzeneServiceContainer> action) => action(new MicrosoftBenzeneServiceContainer(_services));
    }

    private sealed class ScopedCheck : IHealthCheck
    {
        public string Name => "scoped";
        public string Type => "scoped";
        public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IHealthCheckResult>(HealthCheckResult.CreateInstance(true, "scoped"));
    }

    [Fact]
    public void AContainerWithAScopedHealthCheckSurvivesFullValidation()
    {
        // With the old singleton registration this threw at build:
        //   "Cannot consume scoped service 'IEnumerable<IHealthCheck>' from singleton 'IHealthCheckFinder'"
        // — but only once a scoped check was actually registered, which is why examples/Aws passed
        // validation while the bug was live.
        var services = new ServiceCollection();
        var builder = new HealthCheckBuilder(new TestRegister(services));
        builder.AddHealthCheck<ScopedCheck>();

        using var factory = new MicrosoftServiceResolverFactory(services, validateOnBuild: true);

        Assert.NotNull(factory);
    }

    [Fact]
    public void TheFinderIsScoped()
    {
        var services = new ServiceCollection();
        _ = new HealthCheckBuilder(new TestRegister(services));

        var descriptor = Assert.Single(services, x => x.ServiceType == typeof(IHealthCheckFinder));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
}
