using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Core;
using Benzene.Mesh.Collector;
using Benzene.Resilience;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Round-16 composition finding (#250), fixed: like <c>MeshDispatchMessageHandler</c> (#185), the
/// <c>mesh:query:*</c> handlers in <c>Benzene.Mesh.Collector.Handlers</c> (<c>FleetQueryMessageHandler</c>
/// and its four siblings) now take an optional <see cref="Benzene.Abstractions.DI.ICancellationTokenAccessor"/>
/// collaborator, resolved at the point of use, and pass its token into <see cref="IMeshFleetReadModel"/> -
/// so <c>UseTimeout(...)</c> wrapping the fleet-query envelope - the same composition <c>MeshDispatchTest</c>
/// proves works for dispatch - now actually bounds a real, potentially I/O-bound read-model call (a
/// client-aborted fleet/topic/trace query on a trace-backed plane no longer keeps running, and paying
/// for, the full backend scan after the caller has gone away).
/// </summary>
public class MeshCollectorQueryCancellationTest
{
    private sealed class SlowReadModel : IMeshFleetReadModel
    {
        private readonly TimeSpan _delay;
        public bool ObservedCancellation { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }

        public SlowReadModel(TimeSpan delay) => _delay = delay;

        public async Task<FleetView> FleetAsync(MeshTimeRange? range = null, CancellationToken cancellationToken = default)
            => await FleetAsync(range, includeFlows: true, cancellationToken);

        public async Task<FleetView> FleetAsync(MeshTimeRange? range, bool includeFlows, CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            try
            {
                await Task.Delay(_delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
            return new FleetView();
        }

        public Task<ServiceView?> ServiceAsync(string name, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
            => Task.FromResult<ServiceView?>(null);

        public Task<TopicSummary?> TopicAsync(string id, string? version, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
            => Task.FromResult<TopicSummary?>(null);

        public async Task<TraceView?> TraceAsync(string traceId, CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            try
            {
                await Task.Delay(_delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
            return null;
        }

        public Task<CorrelationView?> CorrelationAsync(string correlationId, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CorrelationView?>(null);
    }

    // The review's own probe, mirroring MeshDispatchTest's
    // UseTimeout_AroundTheDispatchHandler_ActuallyBoundsTheRealDispatchCall: wrap a slow read-model
    // fleet query in UseTimeout(...) with a real seeded ICancellationTokenAccessor. For dispatch, this
    // bounds the call well short of the simulated work (#185, proven), and now it does for the fleet
    // query too: the handler resolves the accessor and threads the token through.
    [Fact]
    public async Task UseTimeout_AroundAFleetQuery_ActuallyBoundsTheRealReadModelCall()
    {
        var accessor = new CancellationTokenAccessor();
        var slow = new SlowReadModel(TimeSpan.FromSeconds(5));
        var handler = new FleetQueryMessageHandler(slow, cancellation: accessor);
        var timeoutMiddleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        // Unlike MeshDispatchMessageHandler, the query handlers don't catch a cancellation into a
        // result - it propagates out of HandleAsync, and TimeoutMiddleware (correctly, per its own
        // documented contract) translates a same-layer timer firing into a TimeoutException. That
        // exception IS the evidence the deadline actually reached the read-model call.
        await Assert.ThrowsAsync<TimeoutException>(() => timeoutMiddleware.HandleAsync(new object(), async () =>
        {
            await handler.HandleAsync(new FleetQuery());
        }));
        stopwatch.Stop();

        // Bounded well short of the read model's simulated 5s of work - the pre-fix behaviour is to run
        // the full 5s regardless of the 50ms deadline, so even generous slack for scheduler jitter leaves
        // a wide, unambiguous gap between "fixed" and "broken".
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"Expected the fleet query to be cancelled well short of the read model's 5s simulated work, but it took {stopwatch.Elapsed}.");
        Assert.True(slow.ObservedCancellation);
        Assert.NotEqual(CancellationToken.None, slow.ReceivedToken);
    }

    // Cover a second handler cheaply (the review calls out Fleet + Trace as the two to cover; the other
    // three siblings are mechanical clones of the same one-line fix).
    [Fact]
    public async Task UseTimeout_AroundATraceQuery_ActuallyBoundsTheRealReadModelCall()
    {
        var accessor = new CancellationTokenAccessor();
        var slow = new SlowReadModel(TimeSpan.FromSeconds(5));
        var handler = new TraceQueryMessageHandler(slow, cancellation: accessor);
        var timeoutMiddleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => timeoutMiddleware.HandleAsync(new object(), async () =>
        {
            await handler.HandleAsync(new TraceQuery { TraceId = "t1" });
        }));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"Expected the trace query to be cancelled well short of the read model's 5s simulated work, but it took {stopwatch.Elapsed}.");
        Assert.True(slow.ObservedCancellation);
        Assert.NotEqual(CancellationToken.None, slow.ReceivedToken);
    }
}
