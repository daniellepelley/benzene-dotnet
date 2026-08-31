using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
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

    /// <summary>Throws instead of returning, for #186 (audit-then-rethrow on a dispatch failure).</summary>
    private sealed class ThrowingDispatcher : IMeshServiceDispatcher
    {
        private readonly Exception _exception;
        public ThrowingDispatcher(string key, Exception exception) { Key = key; _exception = exception; }
        public string Key { get; }

        public Task<MeshDispatchResult> DispatchAsync(MeshServiceRegistryEntry entry, MeshDispatchEnvelope envelope, CancellationToken cancellationToken)
            => throw _exception;
    }

    /// <summary>Awaits on whatever token it is given, and records it, for #185 (ambient cancellation).</summary>
    private sealed class SlowDispatcher : IMeshServiceDispatcher
    {
        public string Key => MeshServiceSource.Http;
        public CancellationToken? ObservedToken { get; private set; }

        public async Task<MeshDispatchResult> DispatchAsync(MeshServiceRegistryEntry entry, MeshDispatchEnvelope envelope, CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            // With #185 fixed, a real UseTimeout() deadline cancels this well before 5 seconds; with
            // the old hardcoded CancellationToken.None, this token could never fire and the test would
            // hang instead of observing a TimeoutException.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new MeshDispatchResult("ok", "{}");
        }
    }

    /// <summary>Captures formatted log messages, so a test can assert on the audit line's content.</summary>
    private sealed class RecordingLogger : ILogger<MeshDispatchMessageHandler>
    {
        public readonly List<string> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
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

    // --- #187a: validate the target before charging the per-target rate limit ---

    [Fact]
    public async Task UnknownService_RepeatedCalls_NeverChargeTheRateLimiter()
    {
        // Before #187a, the limiter was charged BEFORE the not-found check, so an arbitrary,
        // never-registered service name could pin a permanent rate-limit window. With the check moved
        // first, a not-found service costs the limiter nothing - two calls against a limit of 1 both
        // still return not-found, never rate-limited (which is what the pre-fix order would produce on
        // the second call).
        var guardOptions = new MeshDispatchGuardOptions { MaxPerMinutePerTarget = 1 };
        var limiter = new MeshDispatchRateLimiter();
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(false));
        var handler = new MeshDispatchMessageHandler(
            gate, new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>()),
            Array.Empty<IMeshServiceDispatcher>(), guardOptions, limiter);
        var request = new MeshDispatchRequest { Service = "ghost", Topic = "x" };

        var first = await handler.HandleAsync(request);
        var second = await handler.HandleAsync(request);

        Assert.Equal("not-found", first.Status);
        Assert.Equal("not-found", second.Status);
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

    // --- #186: a thrown dispatch is audited, then rethrown untouched ---

    [Fact]
    public async Task DispatchThrows_AuditsDispatchFailedWithExceptionType_ThenRethrows()
    {
        var thrown = new InvalidOperationException("target is unreachable");
        var dispatcher = new ThrowingDispatcher(MeshServiceSource.Http, thrown);
        var logger = new RecordingLogger();
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(false));
        var handler = new MeshDispatchMessageHandler(
            gate, HttpRegistry(), new IMeshServiceDispatcher[] { dispatcher }, logger: logger);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new MeshDispatchRequest { Service = "orders", Topic = "order:create", Body = "{}" }));

        // Propagation semantics are unchanged - the SAME exception surfaces, not swallowed or wrapped.
        Assert.Same(thrown, actual);
        // ...but every other exit path audits, and now this one does too.
        var message = Assert.Single(logger.Messages);
        Assert.Contains("outcome=dispatch-failed", message);
        Assert.Contains("exceptionType=InvalidOperationException", message);
    }

    // --- #185: the dispatch observes the ambient cancellation token, not a hardcoded CancellationToken.None ---

    [Fact]
    public async Task WrappedInUseTimeout_PassesTheAmbientCancellationToken_NotHardcodedNone()
    {
        var accessor = new CancellationTokenAccessor();
        var dispatcher = new SlowDispatcher();
        var gate = new MeshDispatchGate(new MeshDispatchOptions(), new StubEnvironment(false));
        var handler = new MeshDispatchMessageHandler(
            gate, HttpRegistry(), new IMeshServiceDispatcher[] { dispatcher }, cancellation: accessor);
        // The exact same TimeoutMiddleware<TContext> Extensions.UseTimeout(...) wires up - see
        // test/Benzene.Core.Test/Resilience/TimeoutMiddlewareTest.cs for the pipeline-builder-level coverage.
        var timeout = new TimeoutMiddleware<object>(accessor, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => timeout.HandleAsync(new object(), () =>
            handler.HandleAsync(new MeshDispatchRequest { Service = "orders", Topic = "order:create", Body = "{}" })));

        // The dispatcher received the wrapped, cancellable token UseTimeout put into the accessor - not
        // CancellationToken.None - and the middleware's deadline actually fired it.
        Assert.NotNull(dispatcher.ObservedToken);
        Assert.NotEqual(CancellationToken.None, dispatcher.ObservedToken!.Value);
        Assert.True(dispatcher.ObservedToken.Value.IsCancellationRequested);
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

    private static int WindowCount(MeshDispatchRateLimiter limiter)
    {
        var field = typeof(MeshDispatchRateLimiter).GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var windows = (System.Collections.IDictionary)field.GetValue(limiter)!;
        return windows.Count;
    }
}

/// <summary>
/// #187b: the limiter self-prunes past a size threshold, even with nothing calling
/// <see cref="MeshDispatchRateLimiter.Prune"/>. Also covers #254, a TOCTOU race inside
/// <see cref="MeshDispatchRateLimiter.Prune"/> itself.
/// </summary>
public class MeshDispatchRateLimiterTest
{
    private static int WindowCount(MeshDispatchRateLimiter limiter)
    {
        var field = typeof(MeshDispatchRateLimiter).GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var windows = (System.Collections.IDictionary)field.GetValue(limiter)!;
        return windows.Count;
    }

    [Fact]
    public void TryAcquire_SelfPrunesPastThreshold_KeepsTheWindowMapBounded()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limiter = new MeshDispatchRateLimiter(() => now);

        // Push the map past the self-prune threshold with distinct keys, all landing in the same
        // about-to-be-stale window.
        for (var i = 0; i < 513; i++)
        {
            limiter.TryAcquire($"target:svc-{i}", 100, out _);
        }
        Assert.Equal(513, WindowCount(limiter));

        // Roll past the window boundary and acquire once more. Only the sibling
        // Benzene.Mesh.Artifacts guard middleware calls Prune() directly - this limiter must self-prune
        // before adding the new entry, because _windows.Count (513) already exceeds the threshold (512).
        now = now.AddMinutes(2);
        limiter.TryAcquire("target:new-key", 100, out _);

        // Every stale window is gone - proof the map stayed bounded without an explicit Prune() call.
        // (Without the self-prune, this would be 514: 513 stale windows plus the new one.)
        Assert.Equal(1, WindowCount(limiter));
    }

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

/// <summary>#187 noted gap: <see cref="HttpMeshServiceDispatcher"/> caps the target's response the same way the request side is capped.</summary>
public class HttpMeshServiceDispatcherTest
{
    private static MeshServiceRegistryEntry HttpEntry() =>
        new("orders", "https://orders.example/spec", "https://orders.example/health");

    [Fact]
    public async Task DispatchAsync_ResponseWithinCap_ReturnsBodyUnchanged()
    {
        var body = new string('a', 100);
        var dispatcher = new HttpMeshServiceDispatcher(new HttpClient(new FixedBodyHttpMessageHandler(body)), maxResponseBytes: 1_000);

        var result = await dispatcher.DispatchAsync(HttpEntry(),
            new MeshDispatchEnvelope("t", new Dictionary<string, string>(), "{}"), CancellationToken.None);

        Assert.Equal(body, result.Body);
        Assert.DoesNotContain(HttpMeshServiceDispatcher.TruncatedMarker, result.Body!);
    }

    [Fact]
    public async Task DispatchAsync_ResponseExceedsCap_TruncatesAndAppendsMarker_RatherThanThrowing()
    {
        var body = new string('b', 1_000);
        var dispatcher = new HttpMeshServiceDispatcher(new HttpClient(new FixedBodyHttpMessageHandler(body)), maxResponseBytes: 100);

        var result = await dispatcher.DispatchAsync(HttpEntry(),
            new MeshDispatchEnvelope("t", new Dictionary<string, string>(), "{}"), CancellationToken.None);

        // Truncated at the cap, not thrown: the target DID respond, and the marker is the audit-visible
        // record of what happened rather than losing the response (and the status code) entirely.
        Assert.StartsWith(new string('b', 100), result.Body!);
        Assert.EndsWith(HttpMeshServiceDispatcher.TruncatedMarker, result.Body!);
        Assert.Equal(100 + HttpMeshServiceDispatcher.TruncatedMarker.Length, result.Body!.Length);
    }

    // --- #246: the truncation point backs off to the last COMPLETE UTF-8 sequence at or before the
    // byte cap, so a response cut mid-multi-byte-character never decodes into a dangling lead/
    // continuation byte (which Encoding.UTF8.GetString would otherwise silently render as U+FFFD
    // right before TruncatedMarker). -------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_ResponseExceedsCap_MidMultiByteCharacter_BacksOffToLastCompleteCharacter()
    {
        // 'é' (U+00E9) is a 2-byte UTF-8 sequence (0xC3 0xA9). 60 of them is 120 bytes; a 101-byte cap
        // lands exactly one byte into the 51st character's sequence - a genuine mid-character cut.
        var body = new string('é', 60);
        var dispatcher = new HttpMeshServiceDispatcher(new HttpClient(new FixedBodyHttpMessageHandler(body)), maxResponseBytes: 101);

        var result = await dispatcher.DispatchAsync(HttpEntry(),
            new MeshDispatchEnvelope("t", new Dictionary<string, string>(), "{}"), CancellationToken.None);

        // Backs off to the 50 complete characters (100 bytes), dropping the dangling lead byte -
        // never a U+FFFD replacement glyph ahead of the marker.
        Assert.Equal(new string('é', 50) + HttpMeshServiceDispatcher.TruncatedMarker, result.Body);
        Assert.DoesNotContain('�', result.Body!);
    }

    [Fact]
    public async Task DispatchAsync_ResponseExceedsCap_AtCleanMultiByteCharacterBoundary_TruncatesExactlyAtCap()
    {
        // Same multi-byte body, but a cap (100) that already lands exactly on a character boundary -
        // the fix must not over-trim a genuinely clean cut.
        var body = new string('é', 60);
        var dispatcher = new HttpMeshServiceDispatcher(new HttpClient(new FixedBodyHttpMessageHandler(body)), maxResponseBytes: 100);

        var result = await dispatcher.DispatchAsync(HttpEntry(),
            new MeshDispatchEnvelope("t", new Dictionary<string, string>(), "{}"), CancellationToken.None);

        Assert.Equal(new string('é', 50) + HttpMeshServiceDispatcher.TruncatedMarker, result.Body);
    }

    [Fact]
    public void DefaultMaxResponseBytes_MatchesTheRequestSideCapDefault()
    {
        // The response cap defaults to the SAME value as the existing request-side cap
        // (MeshDispatchGuardOptions.MaxRequestBytes) - a symmetric bound, not a new arbitrary number.
        Assert.Equal(MeshDispatchGuardOptions.DefaultMaxRequestBytes, HttpMeshServiceDispatcher.DefaultMaxResponseBytes);
    }

    private sealed class FixedBodyHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FixedBodyHttpMessageHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
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
