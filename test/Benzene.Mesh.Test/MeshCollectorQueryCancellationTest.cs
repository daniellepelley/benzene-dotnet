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
/// Round-16 composition finding: unlike <c>MeshDispatchMessageHandler</c> (fixed for #185 via
/// <c>ICancellationTokenAccessor</c>), the <c>mesh:query:*</c> handlers in
/// <c>Benzene.Mesh.Collector.Handlers</c> (<c>FleetQueryMessageHandler</c> and its four siblings) take
/// no <see cref="Benzene.Abstractions.DI.ICancellationTokenAccessor"/> collaborator at all and always
/// call <see cref="IMeshFleetReadModel"/> with the default (never-cancelled) token - even though
/// <see cref="IMeshFleetReadModel"/> itself, and every downstream trace source (X-Ray/Jaeger/Tempo,
/// including the #230-fixed <c>BoundedFanOut</c>), was deliberately built to honor one. The plumbing
/// below the handler is real; the handler itself never uses it, so <c>UseTimeout(...)</c> wrapping the
/// fleet-query envelope - the same composition <c>MeshDispatchTest</c> proves works for dispatch - has
/// zero effect here, and a client-aborted fleet/topic/trace query on a trace-backed plane keeps running
/// (and keeps paying for) the full backend scan after the caller has gone away.
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

        public Task<TraceView?> TraceAsync(string traceId, CancellationToken cancellationToken = default)
            => Task.FromResult<TraceView?>(null);

        public Task<CorrelationView?> CorrelationAsync(string correlationId, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CorrelationView?>(null);
    }

    // The review's own probe, mirroring MeshDispatchTest's
    // UseTimeout_AroundTheDispatchHandler_ActuallyBoundsTheRealDispatchCall: wrap a slow read-model
    // fleet query in UseTimeout(...) with a real seeded ICancellationTokenAccessor. For dispatch, this
    // bounds the call well short of the simulated work (#185, proven). Here it does NOT: the handler has
    // no way to observe the accessor at all, so the query runs to completion regardless of the deadline.
    [Fact]
    public async Task UseTimeout_AroundAFleetQuery_DoesNotBoundTheRealReadModelCall()
    {
        var accessor = new CancellationTokenAccessor();
        var slow = new SlowReadModel(TimeSpan.FromSeconds(5));
        var handler = new FleetQueryMessageHandler(slow);
        var timeoutMiddleware = new TimeoutMiddleware<object>(accessor, TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        await timeoutMiddleware.HandleAsync(new object(), async () =>
        {
            await handler.HandleAsync(new FleetQuery());
        });
        stopwatch.Stop();

        // This is the RED assertion: a working composition would cancel around ~50ms, as
        // MeshDispatchTest proves for dispatch. Instead the full 5s of simulated backend work runs -
        // TimeoutMiddleware's deadline never reaches the read-model call because FleetQueryMessageHandler
        // never resolves the accessor it was wrapped in.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(4),
            $"Expected the un-cancellable query to run to completion (~5s) because no accessor is wired, but it took {stopwatch.Elapsed}.");
        Assert.False(slow.ObservedCancellation);
        Assert.Equal(CancellationToken.None, slow.ReceivedToken);
    }
}
