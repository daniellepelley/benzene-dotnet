using System;
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
