using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Xunit;

namespace Benzene.Test.HealthChecks;

/// <summary>
/// Coverage for the per-check overrides the processor honours (§3.4 / §3.5): a non-critical check's
/// failure degrades rather than flips the probe, and a check's own <see cref="IHealthCheck.Timeout"/>
/// replaces the processor-wide timeout.
/// </summary>
public class HealthCheckProcessorTest
{
    // A check with a fixed outcome and a configurable IsNonCritical override.
    private sealed class StubCheck : IHealthCheck
    {
        private readonly bool _ok;
        public StubCheck(string type, bool ok, bool isNonCritical) { Type = type; _ok = ok; IsNonCritical = isNonCritical; }
        public string Type { get; }
        public bool IsNonCritical { get; }
        public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult(HealthCheckResult.CreateInstance(_ok, Type));
    }

    // A check that fails with a persistent (deterministic, won't-self-heal) result, e.g. an authorization denial.
    private sealed class PersistentFailCheck : IHealthCheck
    {
        public PersistentFailCheck(string type) { Type = type; }
        public string Type { get; }
        public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
            => Task.FromResult(HealthCheckResult.CreatePersistentFailure(Type, new System.Collections.Generic.Dictionary<string, object>(), Array.Empty<HealthCheckDependency>()));
    }

    // A check that takes _delay to complete, with a configurable Timeout override.
    private sealed class SlowCheck : IHealthCheck
    {
        private readonly TimeSpan _delay;
        public SlowCheck(string type, TimeSpan delay, TimeSpan? timeout) { Type = type; _delay = delay; Timeout = timeout; }
        public string Type { get; }
        public TimeSpan? Timeout { get; }
        public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            // Forwards the token, like a conforming IHealthCheck must (WP-7 #2): this is what lets
            // the processor's (or a per-check override's) timeout genuinely cancel the wait rather
            // than merely abandon it.
            await Task.Delay(_delay, cancellationToken);
            return HealthCheckResult.CreateInstance(true, Type);
        }
    }

    // A check whose Timeout getter throws - simulates a buggy IHealthCheck implementation that breaks
    // one of the member reads the processor performs OUTSIDE the check's own ExecuteAsync try/catch.
    private sealed class ThrowingTimeoutCheck : IHealthCheck
    {
        public ThrowingTimeoutCheck(string type) { Type = type; }
        public string Type { get; }
        public TimeSpan? Timeout => throw new InvalidOperationException("Timeout getter is broken");
        public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult(HealthCheckResult.CreateInstance(true, Type));
    }

    // A check whose IsNonCritical getter throws. Returns a FAILED result from ExecuteAsync - the
    // status-downgrade expression only reads IsNonCritical when the result is Failed (`&&`
    // short-circuits otherwise), so a throwing IsNonCritical getter is only actually reachable off a
    // failing check.
    private sealed class ThrowingIsNonCriticalCheck : IHealthCheck
    {
        public ThrowingIsNonCriticalCheck(string type) { Type = type; }
        public string Type { get; }
        public bool IsNonCritical => throw new InvalidOperationException("IsNonCritical getter is broken");
        public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult(HealthCheckResult.CreateInstance(false, Type));
    }

    // A check whose Type getter throws.
    private sealed class ThrowingTypeCheck : IHealthCheck
    {
        public string Type => throw new InvalidOperationException("Type getter is broken");
        public Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult(HealthCheckResult.CreateInstance(true, "n/a"));
    }

    private static async Task<HealthCheckResponse> RunAsync(HealthCheckProcessor processor, params IHealthCheck[] checks)
    {
        var result = await processor.PerformHealthChecksAsync(checks);
        return (HealthCheckResponse)result.PayloadAsObject;
    }

    [Fact]
    public async Task AThrowingTimeoutGetter_DoesNotCrashTheWholeBatch_AndOtherChecksStillReportTheirRealResult()
    {
        // #113: a throwing Timeout/IsNonCritical/Type getter used to propagate out of Task.WhenAll and
        // lose every other, perfectly healthy, check's result with it.
        var healthy1 = new StubCheck("healthy1", ok: true, isNonCritical: false);
        var healthy2 = new StubCheck("healthy2", ok: true, isNonCritical: false);
        var buggy = new ThrowingTimeoutCheck("buggy");

        var response = await RunAsync(new HealthCheckProcessor(), healthy1, healthy2, buggy);

        Assert.False(response.IsHealthy);
        Assert.Equal(HealthCheckStatus.Ok, response.HealthChecks["healthy1"].Status);
        Assert.Equal(HealthCheckStatus.Ok, response.HealthChecks["healthy2"].Status);
        Assert.Equal(HealthCheckStatus.Failed, response.HealthChecks["buggy"].Status);
        Assert.Equal("InvalidOperationException", response.HealthChecks["buggy"].Data["Exception"]);
    }

    [Fact]
    public async Task AThrowingIsNonCriticalGetter_DoesNotCrashTheWholeBatch()
    {
        var healthy = new StubCheck("healthy", ok: true, isNonCritical: false);
        var buggy = new ThrowingIsNonCriticalCheck("buggy");

        var response = await RunAsync(new HealthCheckProcessor(), healthy, buggy);

        Assert.Equal(HealthCheckStatus.Ok, response.HealthChecks["healthy"].Status);
        Assert.Equal(HealthCheckStatus.Failed, response.HealthChecks["buggy"].Status);
    }

    [Fact]
    public async Task AThrowingTypeGetter_DoesNotCrashTheWholeBatch_AndReportsUnderTheRuntimeTypeName()
    {
        var healthy = new StubCheck("healthy", ok: true, isNonCritical: false);
        var buggy = new ThrowingTypeCheck();

        var response = await RunAsync(new HealthCheckProcessor(), healthy, buggy);

        Assert.Equal(HealthCheckStatus.Ok, response.HealthChecks["healthy"].Status);
        // Type itself is unavailable - fall back to the runtime type name so the failure is still
        // attributable to a specific check.
        Assert.Equal(HealthCheckStatus.Failed, response.HealthChecks[nameof(ThrowingTypeCheck)].Status);
    }

    [Fact]
    public async Task NonCriticalFailure_DoesNotFlipHealthy_AndIsReportedAsWarning()
    {
        var response = await RunAsync(new HealthCheckProcessor(), new StubCheck("dep", ok: false, isNonCritical: true));

        // A non-critical dependency being down degrades the instance but does not take it out of service.
        Assert.True(response.IsHealthy);
        Assert.Equal(HealthCheckStatus.Warning, response.HealthChecks["dep"].Status);
    }

    [Fact]
    public async Task CriticalFailure_FlipsUnhealthy()
    {
        // isNonCritical: false is also the default - a failing critical check flips the probe unhealthy.
        var response = await RunAsync(new HealthCheckProcessor(), new StubCheck("dep", ok: false, isNonCritical: false));

        Assert.False(response.IsHealthy);
        Assert.Equal(HealthCheckStatus.Failed, response.HealthChecks["dep"].Status);
    }

    [Fact]
    public async Task DependencyCategoryCheck_ThatFails_DegradesToWarning_KeepingTheEndpointHealthy()
    {
        // The inner check reports Failed and even declares itself critical, but the dependency category
        // forces non-critical: a down dependency degrades the deep healthcheck report (Warning) rather
        // than flipping the endpoint to 503. This is what keeps a healthcheck endpoint green when an
        // auto-wired downstream (e.g. an egress queue) is unreachable — the common integration-test case.
        var wrapped = new DependencyHealthCheck(new StubCheck("dep", ok: false, isNonCritical: false));

        var response = await RunAsync(new HealthCheckProcessor(), wrapped);

        Assert.True(response.IsHealthy);
        Assert.Equal(HealthCheckStatus.Warning, response.HealthChecks["dep"].Status);
    }

    [Fact]
    public async Task DependencyCategoryCheck_WithAPersistentFailure_FlipsUnhealthy_EscapingTheDowngrade()
    {
        // A persistent failure (e.g. an authorization denial) is deterministic and won't self-heal, so it
        // escapes the non-critical downgrade even under the dependency category: it stays Failed and flips
        // the deep healthcheck report unhealthy, rather than sitting yellow forever hiding a real IAM break.
        var wrapped = new DependencyHealthCheck(new PersistentFailCheck("dep"));

        var response = await RunAsync(new HealthCheckProcessor(), wrapped);

        Assert.False(response.IsHealthy);
        Assert.Equal(HealthCheckStatus.Failed, response.HealthChecks["dep"].Status);
    }

    [Fact]
    public async Task PerCheckTimeout_ShorterThanProcessor_TimesTheCheckOut()
    {
        // Processor budget is generous (30s); the check's own 10ms Timeout is what should bite.
        // Since SlowCheck forwards the token it is given (as every conforming IHealthCheck must -
        // WP-7 #2), the timeout genuinely cancels its Task.Delay; that cancellation is reported by
        // ExceptionHandlingHealthCheck (the decorator closest to the raw check) as "Cancelled" - a
        // more accurate label than "Timed Out", since the call really was cancelled.
        var processor = new HealthCheckProcessor(TimeSpan.FromSeconds(30));
        var response = await RunAsync(processor, new SlowCheck("slow", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10)));

        Assert.False(response.IsHealthy);
        Assert.Equal("Cancelled", response.HealthChecks["slow"].Data["Error"]);
    }

    [Fact]
    public async Task PerCheckTimeout_LongerThanProcessor_LetsTheSlowCheckPass()
    {
        // Processor budget is tight (10ms); the check's own 5s Timeout override widens it so a 200ms
        // check still passes - proving the override replaces the processor-wide timeout in both directions.
        var processor = new HealthCheckProcessor(TimeSpan.FromMilliseconds(10));
        var response = await RunAsync(processor, new SlowCheck("slow", TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5)));

        Assert.True(response.IsHealthy);
    }
}
