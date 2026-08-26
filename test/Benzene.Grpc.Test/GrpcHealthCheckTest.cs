using System;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Grpc.Client;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.Microsoft.Dependencies;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Grpc.Test;

/// <summary>
/// Coverage for the gRPC transport-reachability check (dependency layer, not grpc.health.v1). The
/// unhealthy path is exercised against a real channel to a dead port (GrpcChannel is a concrete type
/// that can't be mocked); the healthy path needs a live server and is left to integration tests. Also
/// covers the <c>AddGrpcClient</c> auto-wiring onto the dependency category.
/// </summary>
public class GrpcHealthCheckTest
{
    private sealed class TestRegister : IRegisterDependency
    {
        private readonly IServiceCollection _services;
        public TestRegister(IServiceCollection services) => _services = services;
        public void Register(Action<IBenzeneServiceContainer> action) => action(new MicrosoftBenzeneServiceContainer(_services));
    }

    private static IHealthCheckFinder Finder(Action<IBenzeneServiceContainer> register)
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddSingleton(GrpcChannel.ForAddress("http://localhost:5000")); // the caller's channel
        _ = new HealthCheckBuilder(new TestRegister(services));
        register(container);

        var factory = new MicrosoftServiceResolverFactory(services);
        var scope = factory.CreateScope();
        return scope.GetService<IHealthCheckFinder>();
    }

    [Fact]
    public async Task ExecuteAsync_UnreachableTarget_TimesOut_ReturnsFailed()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost:1"); // nothing listens on port 1
        var check = new GrpcHealthCheck(channel, TimeSpan.FromMilliseconds(500));

        var result = await check.ExecuteAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatus.Failed, result.Status);
        Assert.Equal("Grpc", check.Type);
        Assert.Equal("Grpc", Assert.Single(result.Dependencies).Kind);
    }

    // WP-K (#50): GrpcHealthCheck layers its own connect budget (_timeout, above) on top of the caller's
    // token, so HealthCheckError.Classify's blanket OperationCanceledException re-throw (fixed once for
    // the ~10 other affected checks) cannot be used here unguarded - it would turn every unreachable-target
    // case above into a false "Cancelled" outcome. GrpcHealthCheck must instead distinguish: only the
    // caller's own token firing (ambient shutdown, or the processor's per-check timeout) propagates as
    // cancellation; this check's own budget elapsing stays an ordinary transient Failed (the test above).
    [Fact]
    public async Task ExecuteAsync_AmbientTokenAlreadyCancelled_PropagatesCancellation_InsteadOfReportingAnOrdinaryFailure()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost:1"); // nothing listens on port 1
        // A generous own-budget that would never itself elapse during this test - isolates the assertion
        // to the ambient token, not a race with GrpcHealthCheck's own CancelAfter.
        var check = new GrpcHealthCheck(channel, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.ExecuteAsync(cts.Token));
    }

    [Fact]
    public void AddGrpcClient_AutoRegistersADependencyCheck_OnTheDependencyCategoryOnly()
    {
        var finder = Finder(c => c.AddGrpcClient(_ => { }));

        Assert.Single(finder.FindDependencyHealthChecks(), x => x.Type == "Grpc");
        Assert.DoesNotContain(finder.FindHealthChecks(), x => x.Type == "Grpc");
    }

    [Fact]
    public void AddGrpcClient_HealthCheckFalse_RegistersNoDependencyCheck()
    {
        var finder = Finder(c => c.AddGrpcClient(_ => { }, healthCheck: false));

        Assert.DoesNotContain(finder.FindDependencyHealthChecks(), x => x.Type == "Grpc");
    }
}
