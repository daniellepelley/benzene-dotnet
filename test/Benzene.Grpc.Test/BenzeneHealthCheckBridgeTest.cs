using Benzene.Grpc.AspNet;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using BenzeneHealthCheckResult = Benzene.HealthChecks.Core.HealthCheckResult;
using BenzeneHealthCheckStatus = Benzene.HealthChecks.Core.HealthCheckStatus;
using IBenzeneHealthCheck = Benzene.HealthChecks.Core.IHealthCheck;
using IBenzeneHealthCheckResult = Benzene.HealthChecks.Core.IHealthCheckResult;

namespace Benzene.Grpc.Test;

public class BenzeneHealthCheckBridgeTest
{
    [Fact]
    public async Task CheckHealthAsync_WhenNoChecksAreRegistered_ReturnsHealthy()
    {
        var bridge = new BenzeneHealthCheckBridge(Array.Empty<IBenzeneHealthCheck>());

        var result = await bridge.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenAllChecksPass_ReturnsHealthy()
    {
        var bridge = new BenzeneHealthCheckBridge(new[] { new FakeHealthCheck("a", BenzeneHealthCheckStatus.Ok) });

        var result = await bridge.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenAnyCheckFails_ReturnsUnhealthy()
    {
        var bridge = new BenzeneHealthCheckBridge(new[]
        {
            new FakeHealthCheck("a", BenzeneHealthCheckStatus.Ok),
            new FakeHealthCheck("b", BenzeneHealthCheckStatus.Failed),
        });

        var result = await bridge.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenNoneFailButOneWarns_ReturnsDegraded()
    {
        var bridge = new BenzeneHealthCheckBridge(new[]
        {
            new FakeHealthCheck("a", BenzeneHealthCheckStatus.Ok),
            new FakeHealthCheck("b", BenzeneHealthCheckStatus.Warning),
        });

        var result = await bridge.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    // Round-10 #110: a configured LivenessCheckTypes/ReadinessCheckTypes entry that matches no
    // registered check is a wiring error - it must fail loud at construction ("wiring time"), not
    // fall through CheckHealthAsync's "zero checks matched" branch and report an unconditional
    // Healthy at every probe.
    [Fact]
    public void Constructor_IncludeTypesEntryMatchesNoRegisteredCheck_ThrowsImmediately()
    {
        var checks = new[] { new FakeHealthCheck("Liveness", BenzeneHealthCheckStatus.Ok) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new BenzeneHealthCheckBridge(checks, new HashSet<string> { "Live" })); // typo'd type

        Assert.Contains("Live", ex.Message);
    }

    [Fact]
    public void Constructor_IncludeTypesAllMatchRegisteredChecks_DoesNotThrow()
    {
        var checks = new[]
        {
            new FakeHealthCheck("Liveness", BenzeneHealthCheckStatus.Ok),
            new FakeHealthCheck("Memory", BenzeneHealthCheckStatus.Ok),
        };

        var bridge = new BenzeneHealthCheckBridge(checks, new HashSet<string> { "Liveness" });

        Assert.NotNull(bridge);
    }

    [Fact]
    public void Constructor_NoIncludeTypes_NeverThrowsEvenWithNoRegisteredChecks()
    {
        var bridge = new BenzeneHealthCheckBridge(Array.Empty<IBenzeneHealthCheck>());

        Assert.NotNull(bridge);
    }

    // Round-10 #110: two checks that happen to share the same Type must both appear distinctly in
    // the reported data dictionary, suffixed, rather than the second silently clobbering the first.
    [Fact]
    public async Task CheckHealthAsync_TwoChecksShareTheSameType_BothAppearDistinctlyInData()
    {
        var bridge = new BenzeneHealthCheckBridge(new[]
        {
            new FakeHealthCheck("Database", BenzeneHealthCheckStatus.Ok),
            new FakeHealthCheck("Database", BenzeneHealthCheckStatus.Failed),
        });

        var result = await bridge.CheckHealthAsync(new HealthCheckContext());

        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data!.Count);
        Assert.True(result.Data.ContainsKey("Database"));
        Assert.True(result.Data.ContainsKey("Database-2"));
        Assert.Equal(BenzeneHealthCheckStatus.Ok, result.Data["Database"]);
        Assert.Equal(BenzeneHealthCheckStatus.Failed, result.Data["Database-2"]);
    }

    private class FakeHealthCheck : IBenzeneHealthCheck
    {
        private readonly string _status;

        public FakeHealthCheck(string type, string status)
        {
            Type = type;
            _status = status;
        }

        public string Type { get; }

        public Task<IBenzeneHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IBenzeneHealthCheckResult>(new BenzeneHealthCheckResult(_status, Type, new Dictionary<string, object>()));
        }
    }
}
