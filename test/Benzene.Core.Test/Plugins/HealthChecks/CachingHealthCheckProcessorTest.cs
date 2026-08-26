using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.Results;
using Xunit;

namespace Benzene.Test.Plugins.HealthChecks;

public class CachingHealthCheckProcessorTest
{
    private class CountingProcessor : IHealthCheckProcessor
    {
        public int Calls { get; private set; }
        public Task<IBenzeneResult> PerformHealthChecksAsync(IHealthCheck[] healthChecks)
        {
            Calls++;
            return Task.FromResult((IBenzeneResult)BenzeneResult.Ok(new HealthCheckResponse(true,
                new System.Collections.Generic.Dictionary<string, HealthCheckResult>())));
        }
    }

    private static IHealthCheck Check(string type)
    {
        return new InlineHealthCheck(type, () => Task.FromResult(HealthCheckResult.CreateInstance(true, type)));
    }

    [Fact]
    public async Task WithinTtl_ServesFromCache_WithoutReRunningTheChecks()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var inner = new CountingProcessor();
        var processor = new CachingHealthCheckProcessor(inner, TimeSpan.FromSeconds(30), () => now);
        var checks = new[] { Check("a") };

        await processor.PerformHealthChecksAsync(checks);
        now = now.AddSeconds(10);
        await processor.PerformHealthChecksAsync(checks);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task AfterTtlExpires_ReRunsTheChecks()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var inner = new CountingProcessor();
        var processor = new CachingHealthCheckProcessor(inner, TimeSpan.FromSeconds(30), () => now);
        var checks = new[] { Check("a") };

        await processor.PerformHealthChecksAsync(checks);
        now = now.AddSeconds(31);
        await processor.PerformHealthChecksAsync(checks);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task DifferentCheckSets_CacheIndependently()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var inner = new CountingProcessor();
        var processor = new CachingHealthCheckProcessor(inner, TimeSpan.FromSeconds(30), () => now);

        // Liveness (check "live") and readiness (check "ready") must not share one cache entry.
        await processor.PerformHealthChecksAsync(new[] { Check("live") });
        await processor.PerformHealthChecksAsync(new[] { Check("ready") });

        Assert.Equal(2, inner.Calls);
    }

    // An inner processor that counts its executions and blocks each one on a shared gate, so a test can
    // hold every concurrent caller inside the inner run at once before releasing them - simulating slow
    // I/O (a real inner health check hitting a database/queue/etc.) under a cold-cache stampede.
    private class GatedCountingProcessor : IHealthCheckProcessor
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => _calls;

        public void Release() => _gate.TrySetResult();

        public async Task<IBenzeneResult> PerformHealthChecksAsync(IHealthCheck[] healthChecks)
        {
            Interlocked.Increment(ref _calls);
            await _gate.Task;
            return BenzeneResult.Ok(new HealthCheckResponse(true, new System.Collections.Generic.Dictionary<string, HealthCheckResult>()));
        }
    }

    [Fact]
    public async Task ColdCache_ConcurrentCallers_SingleFlightTheInnerRun()
    {
        // The reviewer's exact repro (#111): 50 concurrent callers against a cold cache with an inner
        // check whose execution is artificially delayed must run the inner processor EXACTLY once, not
        // once per caller.
        var inner = new GatedCountingProcessor();
        var processor = new CachingHealthCheckProcessor(inner, TimeSpan.FromSeconds(30));
        var checks = new[] { Check("a") };

        var callers = Enumerable.Range(0, 50)
            .Select(_ => processor.PerformHealthChecksAsync(checks))
            .ToArray();

        // Give every caller a chance to reach (and observe) the in-flight run before releasing it -
        // otherwise a caller that hasn't started yet could race past a released gate and never test the
        // single-flight guard at all.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        inner.Release();
        await Task.WhenAll(callers);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task AfterAFaultedRun_TheNextCallRetries_InsteadOfReplayingTheFailure()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var callCount = 0;
        var faultingThenHealthy = new FuncProcessor(() =>
        {
            callCount++;
            return callCount == 1
                ? throw new InvalidOperationException("boom")
                : Task.FromResult((IBenzeneResult)BenzeneResult.Ok(new HealthCheckResponse(true,
                    new System.Collections.Generic.Dictionary<string, HealthCheckResult>())));
        });
        var processor = new CachingHealthCheckProcessor(faultingThenHealthy, TimeSpan.FromSeconds(30), () => now);
        var checks = new[] { Check("a") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.PerformHealthChecksAsync(checks));

        // A faulted run must not poison the cache/in-flight guard forever - the next call retries fresh
        // and succeeds.
        var result = await processor.PerformHealthChecksAsync(checks);

        Assert.NotNull(result);
        Assert.Equal(2, callCount);
    }

    private class FuncProcessor : IHealthCheckProcessor
    {
        private readonly Func<Task<IBenzeneResult>> _func;
        public FuncProcessor(Func<Task<IBenzeneResult>> func) => _func = func;
        public Task<IBenzeneResult> PerformHealthChecksAsync(IHealthCheck[] healthChecks) => _func();
    }
}
