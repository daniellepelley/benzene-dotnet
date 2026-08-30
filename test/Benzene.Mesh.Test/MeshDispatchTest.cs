using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Clients.Aws.Lambda;
using Benzene.Core;
using Benzene.Core.Messages;
using Benzene.Mesh.Aws.Lambda;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Dispatch;
using Benzene.Resilience;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

public class MeshDispatchGateTest
{
    private sealed class StubEnvironment : IMeshDispatchEnvironment
    {
        public StubEnvironment(bool isProduction) => IsProduction = isProduction;
        public bool IsProduction { get; }
    }

    [Theory]
    [InlineData(false, false, true)]  // non-prod, no override -> allowed
    [InlineData(false, true, true)]   // non-prod, override -> allowed
    [InlineData(true, false, false)]  // prod, no override -> BLOCKED (the safe default)
    [InlineData(true, true, true)]    // prod, override -> allowed
    public void IsAllowed_RespectsEnvironmentAndOption(bool isProduction, bool allowInProduction, bool expected)
    {
        var gate = new MeshDispatchGate(
            new MeshDispatchOptions { AllowInProduction = allowInProduction },
            new StubEnvironment(isProduction));

        Assert.Equal(expected, gate.IsAllowed);
    }
}

public class MeshDispatchMessageHandlerTest
{
    private sealed class StubEnvironment : IMeshDispatchEnvironment
    {
        public StubEnvironment(bool isProduction) => IsProduction = isProduction;
        public bool IsProduction { get; }
    }

    private sealed class RecordingDispatcher : IMeshServiceDispatcher
    {
        private readonly MeshDispatchResult _result;
        public RecordingDispatcher(string key, MeshDispatchResult result) { Key = key; _result = result; }
        public string Key { get; }
        public MeshServiceRegistryEntry? Entry { get; private set; }
        public MeshDispatchEnvelope? Envelope { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }

        public Task<MeshDispatchResult> DispatchAsync(MeshServiceRegistryEntry entry, MeshDispatchEnvelope envelope, CancellationToken cancellationToken)
        {
            Entry = entry;
            Envelope = envelope;
            ReceivedToken = cancellationToken;
            return Task.FromResult(_result);
        }
    }

    // #185 - a dispatcher that runs long enough to observe whether the ambient token it is handed
    // ever actually fires. Before the fix the handler hardcodes CancellationToken.None, so this runs
    // the full simulated duration regardless of any outer deadline; after the fix, the linked token
    // TimeoutMiddleware installs cancels the Task.Delay almost immediately.
    private sealed class SlowDispatcher : IMeshServiceDispatcher
    {
        public SlowDispatcher(string key) => Key = key;
        public string Key { get; }
        public bool ObservedCancellation { get; private set; }

        public async Task<MeshDispatchResult> DispatchAsync(MeshServiceRegistryEntry entry, MeshDispatchEnvelope envelope, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }

            return new MeshDispatchResult("ok", "{}");
        }
    }

    // #186 - a dispatcher that always throws, simulating a real production target being unreachable
    // (DNS failure, connection refused, malformed URL).
    private sealed class ThrowingDispatcher : IMeshServiceDispatcher
    {
        public ThrowingDispatcher(string key) => Key = key;
        public string Key { get; }

        public Task<MeshDispatchResult> DispatchAsync(MeshServiceRegistryEntry entry, MeshDispatchEnvelope envelope, CancellationToken cancellationToken)
            => throw new HttpRequestException("target unreachable");
    }

    private static MeshDispatchMessageHandler Handler(bool isProduction, MeshServiceRegistry registry, params IMeshServiceDispatcher[] dispatchers)
    {
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(isProduction));
        return new MeshDispatchMessageHandler(gate, registry, dispatchers);
    }

    private static MeshServiceRegistry HttpRegistry() =>
        new(new[] { new MeshServiceRegistryEntry("orders", "https://orders.example/spec", "https://orders.example/health") });

    [Fact]
    public async Task BlockedInProduction_ReturnsForbidden_AndNeverDispatches()
    {
        var dispatcher = new RecordingDispatcher(MeshServiceSource.Http, new MeshDispatchResult("ok", "{}"));
        var handler = Handler(isProduction: true, HttpRegistry(), dispatcher);

        var result = await handler.HandleAsync(new MeshDispatchRequest { Service = "orders", Topic = "order:create", Body = "{}" });

        Assert.Equal("forbidden", result.Status);
        Assert.False(result.IsSuccessful);
        Assert.Null(dispatcher.Entry); // the real handler was never invoked
    }

    [Fact]
    public async Task UnknownService_ReturnsNotFound()
    {
        var handler = Handler(false, new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>()),
            new RecordingDispatcher(MeshServiceSource.Http, new MeshDispatchResult("ok", "{}")));

        var result = await handler.HandleAsync(new MeshDispatchRequest { Service = "ghost", Topic = "x" });

        Assert.Equal("not-found", result.Status);
    }

    [Fact]
    public async Task MissingTopic_ReturnsBadRequest()
    {
        var handler = Handler(false, HttpRegistry());

        var result = await handler.HandleAsync(new MeshDispatchRequest { Service = "orders" });

        Assert.Equal("bad-request", result.Status);
    }

    [Fact]
    public async Task NoDispatcherForSource_ReturnsNotImplemented()
    {
        var registry = new MeshServiceRegistry(new[]
        {
            new MeshServiceRegistryEntry("orders", "", "", MeshServiceSource.AwsLambdaInvoke,
                new Dictionary<string, string> { ["functionName"] = "fn" }),
        });
        // Only an HTTP dispatcher is registered - nothing handles AwsLambdaInvoke.
        var handler = Handler(false, registry, new RecordingDispatcher(MeshServiceSource.Http, new MeshDispatchResult("ok", "{}")));

        var result = await handler.HandleAsync(new MeshDispatchRequest { Service = "orders", Topic = "x" });

        Assert.Equal("not-implemented", result.Status);
    }

    // #255 - this exit path (registered service, rate limit passed, but no IMeshServiceDispatcher
    // matches the entry's Source) was the ONLY termination path in HandleAsync that never called
    // Audit(...) - unlike gate-blocked, bad-request, not-found, rate-limited, dispatch-failed (#186),
    // and the dispatched success path. It's also the most routine post-deploy misconfiguration (a
    // service registered with a Source whose matching AddMeshXxxDispatcher() was never wired into the
    // container), not a hostile input, so it must leave the same audit trail every other exit path
    // does. Sibling to NoDispatcherForSource_ReturnsNotImplemented above, which only checks the
    // returned status and never the audit log.
    [Fact]
    public async Task NoDispatcherRegisteredForSource_StillLeavesAnAuditRecord()
    {
        var mockLogger = new Mock<ILogger<MeshDispatchMessageHandler>>();
        var registry = new MeshServiceRegistry(new[]
        {
            new MeshServiceRegistryEntry("orders", "", "", MeshServiceSource.AwsLambdaInvoke,
                new Dictionary<string, string> { ["functionName"] = "fn" }),
        });
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(false));
        // Zero IMeshServiceDispatchers supplied - nothing handles AwsLambdaInvoke.
        var handler = new MeshDispatchMessageHandler(gate, registry, Array.Empty<IMeshServiceDispatcher>(),
            logger: mockLogger.Object);

        var result = await handler.HandleAsync(new MeshDispatchRequest { Service = "orders", Topic = "x" });

        Assert.Equal("not-implemented", result.Status);

        // Exactly one audit entry, under its own outcome label - not the silent zero-log vanishing act
        // the unfixed handler produced for this one misconfiguration class.
        mockLogger.Verify(x => x.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("no-dispatcher") && state.ToString()!.Contains("orders")),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task HappyPath_DispatchesViaMatchingTransport_AndReturnsTheServiceResponse()
    {
        var dispatcher = new RecordingDispatcher(MeshServiceSource.Http, new MeshDispatchResult("created", "{\"id\":1}"));
        var handler = Handler(false, HttpRegistry(), dispatcher);

        var result = await handler.HandleAsync(new MeshDispatchRequest
        {
            Service = "orders",
            Topic = "order:create",
            Headers = new Dictionary<string, string> { ["k"] = "v" },
            Body = "{\"a\":1}",
        });

        Assert.Equal("ok", result.Status);
        Assert.True(result.IsSuccessful);
        Assert.Equal("orders", dispatcher.Entry!.Name);
        Assert.Equal("order:create", dispatcher.Envelope!.Topic);
        Assert.Equal("{\"a\":1}", dispatcher.Envelope!.Body);
        // The service's response envelope is serialized into the payload.
        var payload = Assert.IsType<RawStringMessage>(result.Payload);
        Assert.Contains("created", payload.Content);
        Assert.Contains("id", payload.Content);
    }

    // #185 - resolves the dispatch's cancellation token via ICancellationTokenAccessor (the same
    // idiom HttpBenzeneMessageClient uses) instead of hardcoding CancellationToken.None.
    [Fact]
    public async Task ResolvesCancellationTokenFromTheAccessor_AndPassesItToTheDispatcher()
    {
        var accessor = new CancellationTokenAccessor();
        using var cts = new CancellationTokenSource();
        accessor.CancellationToken = cts.Token;

        var dispatcher = new RecordingDispatcher(MeshServiceSource.Http, new MeshDispatchResult("ok", "{}"));
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(false));
        var handler = new MeshDispatchMessageHandler(gate, HttpRegistry(), new IMeshServiceDispatcher[] { dispatcher },
            cancellation: accessor);

        await handler.HandleAsync(new MeshDispatchRequest { Service = "orders", Topic = "order:create" });

        // Not CancellationToken.None - the live token the accessor was holding at the point of use.
        Assert.Equal(cts.Token, dispatcher.ReceivedToken);
    }

    // #185 - the review's own probe: UseTimeout(...) wrapping the dispatch handler with a slow mock
    // dispatcher. Before the fix, the dispatch ignores the linked token TimeoutMiddleware installs and
    // runs to completion (the full simulated work) regardless of the configured deadline - "zero
    // protection". After the fix, the dispatch observes cancellation well short of that. The bound
    // below is deliberately generous (a small fraction of the dispatcher's simulated work, itself set
    // far above the 50ms deadline) so the assertion is about the fix's mechanism, not about scheduler
    // precision on a loaded CI box.
    [Fact]
    public async Task UseTimeout_AroundTheDispatchHandler_ActuallyBoundsTheRealDispatchCall()
    {
        var accessor = new CancellationTokenAccessor();
        var slow = new SlowDispatcher(MeshServiceSource.Http);
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(false));
        var handler = new MeshDispatchMessageHandler(gate, HttpRegistry(), new IMeshServiceDispatcher[] { slow },
            cancellation: accessor);

        var timeoutMiddleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromMilliseconds(50));

        IBenzeneResult<RawStringMessage>? result = null;
        var stopwatch = Stopwatch.StartNew();
        await timeoutMiddleware.HandleAsync(new object(), async () =>
        {
            result = await handler.HandleAsync(new MeshDispatchRequest { Service = "orders", Topic = "order:create" });
        });
        stopwatch.Stop();

        // Bounded well short of the dispatcher's simulated 20s of work - the pre-fix behaviour is to
        // run the full 20s regardless of the 50ms deadline, so even generous slack for scheduler jitter
        // leaves a wide, unambiguous gap between "fixed" and "broken".
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Expected the dispatch to be cancelled well short of the dispatcher's 20s simulated work, but it took {stopwatch.Elapsed}.");
        Assert.True(slow.ObservedCancellation);
        Assert.NotNull(result);
        Assert.False(result!.IsSuccessful);
    }

    // #186 - a thrown dispatch exception (target unreachable, DNS failure, malformed URL) must leave
    // an audit trail, exactly like every other exit path, and must not escape as a raw exception.
    [Fact]
    public async Task DispatcherThrows_AuditsTheFailure_AndReturnsServiceUnavailable_InsteadOfThrowing()
    {
        var mockLogger = new Mock<ILogger<MeshDispatchMessageHandler>>();
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(false));
        var handler = new MeshDispatchMessageHandler(gate, HttpRegistry(),
            new IMeshServiceDispatcher[] { new ThrowingDispatcher(MeshServiceSource.Http) },
            logger: mockLogger.Object);

        var result = await handler.HandleAsync(new MeshDispatchRequest { Service = "orders", Topic = "order:create" });

        Assert.False(result.IsSuccessful);
        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);

        // Exactly one audit entry, carrying the failure - not the silent zero-log raw throw the
        // unfixed handler produced.
        mockLogger.Verify(x => x.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("dispatch-failed") && state.ToString()!.Contains("orders")),
            It.Is<Exception>(e => e != null && e.Message.Contains("target unreachable")),
            (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()), Times.Once);
    }

    // #187 - the registry existence check must run BEFORE any per-target window is charged, so an
    // unregistered/arbitrary service name is rejected without ever pinning a dictionary entry in the
    // limiter. The review's probe: N distinct nonexistent service names, asserting zero growth.
    [Fact]
    public async Task UnregisteredServiceNames_AreRejected_WithoutEverChargingTheRateLimiterWindow()
    {
        var limiter = new MeshDispatchRateLimiter();
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(false));
        var handler = new MeshDispatchMessageHandler(gate, new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>()),
            new IMeshServiceDispatcher[] { new RecordingDispatcher(MeshServiceSource.Http, new MeshDispatchResult("ok", "{}")) },
            limiter: limiter);

        for (var i = 0; i < 500; i++)
        {
            var result = await handler.HandleAsync(new MeshDispatchRequest { Service = $"ghost-{i}", Topic = "x" });
            Assert.Equal("not-found", result.Status);
        }

        Assert.Equal(0, WindowCount(limiter));
    }

    private static int WindowCount(MeshDispatchRateLimiter limiter)
    {
        var field = typeof(MeshDispatchRateLimiter).GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)field.GetValue(limiter)!;
        return dict.Count;
    }
}

public class MeshDispatchRateLimiterTest
{
    // #254 - a comparer that lets the test inject a real, deterministic concurrent mutation at the
    // exact point Prune()'s TryRemove call looks up the bucket for a key. ConcurrentDictionary
    // computes the key's hashcode (via this comparer, since one was supplied) BEFORE taking any
    // internal lock, so firing here reproduces the real TOCTOU window - Prune() has already decided,
    // from the stale Window it enumerated, to remove the entry, but the removal has not yet executed -
    // without relying on real thread-scheduling luck (a genuine race on a single-key dictionary
    // operation with a two-line critical section isn't reliably reproducible without an injected
    // delay).
    private sealed class RaceInjectingComparer : IEqualityComparer<string>
    {
        private readonly IEqualityComparer<string> _inner = StringComparer.OrdinalIgnoreCase;
        private Action? _onFirstLookup;

        public void ArmOnce(Action onFirstLookup) => _onFirstLookup = onFirstLookup;

        public bool Equals(string? x, string? y)
        {
            Interlocked.Exchange(ref _onFirstLookup, null)?.Invoke();
            return _inner.Equals(x, y);
        }

        public int GetHashCode(string obj)
        {
            Interlocked.Exchange(ref _onFirstLookup, null)?.Invoke();
            return _inner.GetHashCode(obj);
        }
    }

    // #254 - Prune() enumerates _windows and, for each entry it decides (from its enumeration
    // snapshot) is stale, removes it. Before the fix this used the unconditional two-argument
    // TryRemove(key), which deletes whatever is CURRENTLY stored for that key - even a fresh,
    // still-current-minute window a concurrent TryAcquire installed for the SAME key between Prune()'s
    // enumeration reading the stale value and its removal call actually executing (Prune runs before
    // every guarded TryAcquire, so this is hot-path concurrency, not a rare timer edge case). This
    // test reproduces that exact interleaving deterministically - via RaceInjectingComparer above,
    // installed on a replacement _windows dictionary through the same private-field reflection the
    // existing WindowCount helper uses - and drives the REAL, compiled Prune() method through it, not
    // a hand-reimplementation of its logic.
    [Fact]
    public void Prune_RaceAtTheMinuteBoundary_NeverDeletesAConcurrentlyInstalledFreshWindow()
    {
        const string key = "target:orders";
        const int limit = 2;
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(1);
        var now = t0;
        var limiter = new MeshDispatchRateLimiter(() => now);

        // Replace _windows with a dictionary of the SAME concrete type the limiter uses (its Window
        // type is private, so it's located and constructed via reflection) - seeded with a stale
        // window for `key` from the OLD minute, exactly what Prune()'s enumerator would read.
        var limiterType = typeof(MeshDispatchRateLimiter);
        var windowsField = limiterType.GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var windowType = limiterType.GetNestedType("Window", BindingFlags.NonPublic)!;
        var dictType = typeof(ConcurrentDictionary<,>).MakeGenericType(typeof(string), windowType);

        var comparer = new RaceInjectingComparer();
        var raceDict = (IDictionary)Activator.CreateInstance(dictType, comparer)!;
        raceDict[key] = Activator.CreateInstance(windowType, t0, 1)!;
        windowsField.SetValue(limiter, raceDict);

        // Arm the race: the FIRST time Prune()'s TryRemove call touches the comparer to look up `key`
        // (i.e. after it has already decided, from the stale Window(t0, 1) it enumerated, to remove
        // this entry, but strictly before the removal executes), two real TryAcquire calls land for
        // the same key at t1 - the concurrently-installed fresh window the review reconstructed.
        comparer.ArmOnce(() =>
        {
            Assert.True(limiter.TryAcquire(key, limit, out _)); // fresh Window(t1, 1)
            Assert.True(limiter.TryAcquire(key, limit, out _)); // Window(t1, 2) - at the limit
        });

        now = t1;
        limiter.Prune();

        // The window the two concurrent requests built up must survive Prune()'s stale-snapshot
        // decision - a third request this minute must be refused, never wrongly re-admitted as if the
        // window had just reset to Count=1.
        Assert.True(raceDict.Contains(key),
            "Prune() deleted the concurrently-installed fresh window instead of refusing the stale removal.");
        Assert.False(limiter.TryAcquire(key, limit, out _),
            "the lost increment let a third request through this minute against a limit of 2.");
    }
}

public class AwsLambdaMeshServiceDispatcherTest
{
    [Fact]
    public async Task Dispatch_InvokesFunction_WithTopicAndBody_AndMapsResponse()
    {
        var client = new Mock<IAwsLambdaClient>();
        client.Setup(x => x.SendMessageAsync<BenzeneMessageClientRequest, BenzeneMessageClientResponse>(
                It.IsAny<BenzeneMessageClientRequest>(), "orders-fn", InvocationType.RequestResponse))
            .ReturnsAsync(new BenzeneMessageClientResponse("created", "{\"ok\":true}", new Dictionary<string, string>()));

        var dispatcher = new AwsLambdaMeshServiceDispatcher(client.Object);
        var entry = new MeshServiceRegistryEntry("orders", "", "", MeshServiceSource.AwsLambdaInvoke,
            new Dictionary<string, string> { ["functionName"] = "orders-fn" });

        var result = await dispatcher.DispatchAsync(entry,
            new MeshDispatchEnvelope("order:create", new Dictionary<string, string>(), "{\"a\":1}"), CancellationToken.None);

        Assert.Equal("created", result.StatusCode);
        Assert.Equal("{\"ok\":true}", result.Body);
        client.Verify(x => x.SendMessageAsync<BenzeneMessageClientRequest, BenzeneMessageClientResponse>(
            It.Is<BenzeneMessageClientRequest>(r => r.Topic == "order:create" && r.Body == "{\"a\":1}"),
            "orders-fn", InvocationType.RequestResponse), Times.Once);
    }

    [Fact]
    public async Task Dispatch_MissingFunctionName_Throws()
    {
        var dispatcher = new AwsLambdaMeshServiceDispatcher(new Mock<IAwsLambdaClient>().Object);
        var entry = new MeshServiceRegistryEntry("orders", "", "", MeshServiceSource.AwsLambdaInvoke, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(entry, new MeshDispatchEnvelope("t", new Dictionary<string, string>(), ""), CancellationToken.None));
    }
}
