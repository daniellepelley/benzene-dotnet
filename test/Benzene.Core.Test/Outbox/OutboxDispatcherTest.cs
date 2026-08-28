using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Outbox;
using Benzene.Results;
using Benzene.Test.Logging.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Outbox;

public class OutboxDispatcherTest
{
    private static OutboxEnvelope NewEnvelope(
        string id = "env-1",
        string topic = "test:topic",
        string payload = "\"hello\"",
        IReadOnlyDictionary<string, string>? headers = null,
        DateTimeOffset? createdAtUtc = null,
        int attemptCount = 0)
        => new(id, topic, payload, typeof(string).AssemblyQualifiedName!, headers ?? new Dictionary<string, string>(), createdAtUtc ?? DateTimeOffset.UtcNow, attemptCount);

    // Builds a real DI container with an outbound route (no UseOutbox() - the dispatcher's own
    // scope + OutboxDispatchScope marker are exercised directly against a plain route here, since
    // OutboxMiddlewareTest already covers the pass-through interaction with UseOutbox()) and a real
    // InMemoryOutboxStore, so OutboxDispatcher is exercised end to end through the actual sender/
    // pipeline machinery rather than mocked.
    private static (MicrosoftServiceResolverFactory Factory, InMemoryOutboxStore Store) BuildFactory(
        Action<OutboundRoutingBuilder> configureRouting, Func<DateTimeOffset>? now = null)
    {
        var services = new ServiceCollection();
        services.AddTransient<ISerializer, JsonSerializer>();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddOutbox();

        var store = new InMemoryOutboxStore(now);
        services.AddSingleton<IOutboxStore>(store);

        container.AddOutboundRouting(configureRouting);

        var factory = new MicrosoftServiceResolverFactory(services.BuildServiceProvider());
        return (factory, store);
    }

    // Same as BuildFactory, but wires an already-constructed store instead of creating its own - used
    // to simulate two independent worker processes (each its own DI container / IBenzeneMessageSender)
    // racing the same outbox.
    private static MicrosoftServiceResolverFactory BuildFactoryOnStore(
        IOutboxStore store, Action<OutboundRoutingBuilder> configureRouting)
    {
        var services = new ServiceCollection();
        services.AddTransient<ISerializer, JsonSerializer>();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddOutbox();
        services.AddSingleton(store);

        container.AddOutboundRouting(configureRouting);

        return new MicrosoftServiceResolverFactory(services.BuildServiceProvider());
    }

    [Fact]
    public async Task RunOnceAsync_SuccessfulSend_MarksDispatched_AndSendsThroughTheRoutePipeline()
    {
        OutboundContext? recorded = null;
        var (factory, store) = BuildFactory(routing => routing
            .Route("test:topic", pipeline => pipeline.OnRequest(ctx =>
            {
                recorded = ctx;
                ctx.Response = BenzeneResult.Accepted<Void>();
            })));
        await store.AddAsync([NewEnvelope(headers: new Dictionary<string, string> { ["x-test"] = "value" })]);

        var dispatcher = new OutboxDispatcher(store, factory, new OutboxOptions());
        var result = await dispatcher.RunOnceAsync();

        Assert.Equal(1, result.Dispatched);
        Assert.Equal(0, result.Rescheduled);
        Assert.Equal(0, result.Parked);
        Assert.NotNull(recorded);
        Assert.Equal("hello", recorded!.Request);
        Assert.Equal("value", recorded.Headers["x-test"]);

        // Dispatched: no longer claimable.
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        factory.Dispose();
    }

    [Fact]
    public async Task RunOnceAsync_FailedSend_ReschedulesWithBackoff()
    {
        var (factory, store) = BuildFactory(routing => routing
            .Route("test:topic", pipeline => pipeline.OnRequest(_ => throw new InvalidOperationException("boom"))));
        await store.AddAsync([NewEnvelope()]);

        var dispatcher = new OutboxDispatcher(store, factory, new OutboxOptions { MaxAttempts = 5, BackoffBase = TimeSpan.FromSeconds(30) });
        var result = await dispatcher.RunOnceAsync();

        Assert.Equal(0, result.Dispatched);
        Assert.Equal(1, result.Rescheduled);
        Assert.Equal(0, result.Parked);
        // Rescheduled into the future - not immediately due again.
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        factory.Dispose();
    }

    [Fact]
    public async Task RunOnceAsync_MaxAttemptsReached_Parks()
    {
        var (factory, store) = BuildFactory(routing => routing
            .Route("test:topic", pipeline => pipeline.OnRequest(_ => throw new InvalidOperationException("boom"))));
        // Already failed twice; the next failure hits MaxAttempts = 3 and parks.
        await store.AddAsync([NewEnvelope(attemptCount: 2)]);

        var dispatcher = new OutboxDispatcher(store, factory, new OutboxOptions { MaxAttempts = 3 });
        var result = await dispatcher.RunOnceAsync();

        Assert.Equal(0, result.Dispatched);
        Assert.Equal(0, result.Rescheduled);
        Assert.Equal(1, result.Parked);
        Assert.Null(await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1)));

        factory.Dispose();
    }

    [Fact]
    public async Task RunOnceAsync_DeletesRetentionExpiredDispatchedEnvelopes()
    {
        var now = DateTimeOffset.UtcNow;
        var (factory, store) = BuildFactory(
            routing => routing.Route("test:topic", pipeline => pipeline.OnRequest(ctx => ctx.Response = BenzeneResult.Accepted<Void>())),
            () => now);
        await store.AddAsync([NewEnvelope("old-dispatched", createdAtUtc: now)]);
        var claimed = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));
        await store.MarkDispatchedAsync("old-dispatched", claimed.LeaseToken!);

        now = now.AddDays(10);
        var dispatcher = new OutboxDispatcher(store, factory, new OutboxOptions { RetentionPeriod = TimeSpan.FromDays(7) }, now: () => now);

        var result = await dispatcher.RunOnceAsync();

        Assert.Equal(1, result.DeletedRetired);

        factory.Dispose();
    }

    /// <summary>
    /// A test double wrapping a real <see cref="InMemoryOutboxStore"/> that throws (not returns
    /// <see langword="false"/>) from <see cref="MarkDispatchedAsync"/> the first <c>ThrowCount</c> times
    /// it is called, then delegates normally - used to simulate a routine transient settle-call failure
    /// (DynamoDB throttling, a network blip) distinct from the fencing "reclaimed" (<see langword="false"/>)
    /// case that already has its own coverage.
    /// </summary>
    private sealed class ThrowsOnMarkDispatchedStore : IOutboxStore
    {
        private readonly InMemoryOutboxStore _inner;
        private int _remainingThrows;

        public ThrowsOnMarkDispatchedStore(InMemoryOutboxStore inner, int throwCount)
        {
            _inner = inner;
            _remainingThrows = throwCount;
        }

        public int MarkDispatchedCallCount { get; private set; }

        public Task AddAsync(IEnumerable<OutboxEnvelope> envelopes, CancellationToken cancellationToken = default)
            => _inner.AddAsync(envelopes, cancellationToken);

        public Task<IReadOnlyList<OutboxEnvelope>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default)
            => _inner.ClaimDueAsync(batchSize, lease, cancellationToken);

        public Task<OutboxEnvelope?> ClaimAsync(string id, TimeSpan lease, CancellationToken cancellationToken = default)
            => _inner.ClaimAsync(id, lease, cancellationToken);

        public Task<bool> MarkDispatchedAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
        {
            MarkDispatchedCallCount++;
            if (_remainingThrows > 0)
            {
                _remainingThrows--;
                throw new InvalidOperationException("simulated transient store failure settling MarkDispatchedAsync");
            }

            return _inner.MarkDispatchedAsync(id, leaseToken, cancellationToken);
        }

        public Task<bool> RescheduleAsync(string id, int attemptCount, TimeSpan delay, string error, string leaseToken, CancellationToken cancellationToken = default)
            => _inner.RescheduleAsync(id, attemptCount, delay, error, leaseToken, cancellationToken);

        public Task<bool> ParkAsync(string id, string error, string leaseToken, CancellationToken cancellationToken = default)
            => _inner.ParkAsync(id, error, leaseToken, cancellationToken);

        public Task<int> DeleteDispatchedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => _inner.DeleteDispatchedBeforeAsync(cutoff, cancellationToken);
    }

    [Fact]
    public async Task DispatchOneAsync_MarkDispatchedAsyncThrowsOnce_RetriesAndStillReportsDispatched_NoDuplicateResendDriven()
    {
        // #254: a settle-call throw AFTER a successful send must not be treated like a failed send -
        // that would reschedule/park an already-delivered envelope, guaranteeing a duplicate. One retry
        // clears a routine transient failure and the outcome is still Dispatched.
        var now = DateTimeOffset.UtcNow;
        var innerStore = new InMemoryOutboxStore(() => now);
        await innerStore.AddAsync([NewEnvelope()]);
        var throwingStore = new ThrowsOnMarkDispatchedStore(innerStore, throwCount: 1);

        var sendCount = 0;
        var factory = BuildFactoryOnStore(throwingStore, routing => routing
            .Route("test:topic", pipeline => pipeline.OnRequest(ctx =>
            {
                Interlocked.Increment(ref sendCount);
                ctx.Response = BenzeneResult.Accepted<Void>();
            })));

        var loggerFactory = new FakeLoggerFactory();
        var dispatcher = new OutboxDispatcher(
            throwingStore, factory, new OutboxOptions(), logger: loggerFactory.CreateLogger("Dispatcher"));

        var outcome = await dispatcher.DispatchOneAsync("env-1");

        Assert.Equal(OutboxDispatchOutcome.Dispatched, outcome);
        Assert.Equal(2, throwingStore.MarkDispatchedCallCount); // threw once, retried once, succeeded.
        Assert.Equal(1, sendCount); // exactly one send - no duplicate resend was driven by the settle throw.
        Assert.Contains(loggerFactory.Collector.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("threw on attempt"));

        // Genuinely dispatched - no longer claimable/recoverable via the sweeper.
        Assert.Empty(await innerStore.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        factory.Dispose();
    }

    [Fact]
    public async Task DispatchOneAsync_MarkDispatchedAsyncThrowsOnBothAttempts_ReturnsSentButUnsettled_EnvelopeStaysRecoverable()
    {
        // #254: if the settle call still fails after the one retry, the envelope must be left claimed
        // (not rescheduled/parked as a failed send) so the sweeper can reclaim it once its lease lapses -
        // it must remain visible/recoverable, not silently lost.
        var now = DateTimeOffset.UtcNow;
        var innerStore = new InMemoryOutboxStore(() => now);
        await innerStore.AddAsync([NewEnvelope()]);
        var throwingStore = new ThrowsOnMarkDispatchedStore(innerStore, throwCount: 2);

        var sendCount = 0;
        var factory = BuildFactoryOnStore(throwingStore, routing => routing
            .Route("test:topic", pipeline => pipeline.OnRequest(ctx =>
            {
                Interlocked.Increment(ref sendCount);
                ctx.Response = BenzeneResult.Accepted<Void>();
            })));

        var loggerFactory = new FakeLoggerFactory();
        var dispatcher = new OutboxDispatcher(
            throwingStore, factory, new OutboxOptions { ClaimLease = TimeSpan.FromMinutes(1) },
            logger: loggerFactory.CreateLogger("Dispatcher"), now: () => now);

        var outcome = await dispatcher.DispatchOneAsync("env-1");

        Assert.Equal(OutboxDispatchOutcome.SentButUnsettled, outcome);
        Assert.Equal(2, throwingStore.MarkDispatchedCallCount); // one attempt + one retry, both threw.
        Assert.Equal(1, sendCount); // the send itself only happened once - not treated as a failed send.
        Assert.Contains(loggerFactory.Collector.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("SENT-BUT-UNSETTLED"));

        // Not immediately reclaimable - the original lease is still outstanding (it was neither settled
        // nor released), exactly like any other still-live claim.
        Assert.Null(await innerStore.ClaimAsync("env-1", TimeSpan.FromMinutes(1)));

        // But once that lease naturally lapses, the sweeper reclaims it exactly like any other lost
        // claim - proving the envelope was left recoverable, not silently lost.
        now = now.AddMinutes(2);
        var reclaimed = await innerStore.ClaimAsync("env-1", TimeSpan.FromMinutes(1));
        Assert.NotNull(reclaimed);
        Assert.Equal(OutboxStatus.Pending, reclaimed!.Status);

        factory.Dispose();
    }

    [Fact]
    public async Task DispatchOneAsync_UnknownEnvelope_ReturnsClaimRefused()
    {
        var (factory, store) = BuildFactory(routing => routing.Route("test:topic", pipeline => pipeline.OnRequest(_ => { })));
        var dispatcher = new OutboxDispatcher(store, factory, new OutboxOptions());

        var outcome = await dispatcher.DispatchOneAsync("does-not-exist");

        Assert.Equal(OutboxDispatchOutcome.ClaimRefused, outcome);

        factory.Dispose();
    }

    [Fact]
    public async Task DispatchOneAsync_AlreadyLeasedByAnotherClaimer_ReturnsClaimRefused()
    {
        var (factory, store) = BuildFactory(routing => routing.Route("test:topic", pipeline => pipeline.OnRequest(_ => { })));
        await store.AddAsync([NewEnvelope()]);
        await store.ClaimAsync("env-1", TimeSpan.FromMinutes(5)); // simulates a concurrent claimer

        var dispatcher = new OutboxDispatcher(store, factory, new OutboxOptions());
        var outcome = await dispatcher.DispatchOneAsync("env-1");

        Assert.Equal(OutboxDispatchOutcome.ClaimRefused, outcome);

        factory.Dispose();
    }

    [Fact]
    public async Task DispatchOneAsync_SuccessfulSend_ReturnsDispatched()
    {
        var (factory, store) = BuildFactory(routing => routing
            .Route("test:topic", pipeline => pipeline.OnRequest(ctx => ctx.Response = BenzeneResult.Accepted<Void>())));
        await store.AddAsync([NewEnvelope()]);

        var dispatcher = new OutboxDispatcher(store, factory, new OutboxOptions());
        var outcome = await dispatcher.DispatchOneAsync("env-1");

        Assert.Equal(OutboxDispatchOutcome.Dispatched, outcome);

        factory.Dispose();
    }

    /// <summary>
    /// Regression test for the round-6 stress-test finding: Worker A claims with a short lease and its
    /// send stalls in flight; while A is still "sending" (inside its own route handler, below), A's
    /// lease naturally lapses and Worker B reclaims the same envelope and fully dispatches it (claim +
    /// send + settle). A's send then completes (the real double-send: <c>sendCount == 2</c> - fencing
    /// cannot recall a send already committed to the transport) and <see cref="OutboxDispatcher"/>
    /// tries to settle A's claim with its now-stale token.
    /// </summary>
    /// <remarks>
    /// Before fencing this settle would have silently succeeded (or worse, raced B's own write), so a
    /// spurious double-write and a corrupted final state were both possible. After fencing, exactly one
    /// settle (B's) succeeds and the double-dispatch bug is closed at the STORE level: A's stale write
    /// is rejected (logged as a warning, not an error - B legitimately owns the outcome now) and the
    /// envelope is exactly as B left it - Dispatched, not reopened for a third claim. Closing the
    /// double-<em>send</em> itself (not just the store-level double-write) additionally depends on
    /// <see cref="OutboxDispatcher"/> checking the settle result before treating the message as
    /// delivered, which it does (that's the warning asserted below) - but no fencing scheme can un-send
    /// a message a stale claimant already handed to the transport before its lease lapsed; see
    /// <c>Benzene.Outbox/CLAUDE.md</c>'s "Claim fencing" section.
    /// </remarks>
    [Fact]
    public async Task LiveButSlowClaimant_ReclaimedByAnotherWorker_ExactlyOneSettleSucceeds_NoStateCorruption()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutboxStore(() => now);
        var sendCount = 0;

        var factoryA = BuildFactoryOnStore(store, routing => routing.Route("test:topic", pipeline => pipeline.OnRequest(ctx =>
        {
            // This is Worker A's send actually reaching the transport. While it's "in flight", A's
            // lease lapses and Worker B reclaims + fully dispatches the same envelope independently -
            // simulating a GC pause/network stall that outlives A's lease.
            Interlocked.Increment(ref sendCount);
            now = now.AddSeconds(2);
            var claimB = store.ClaimAsync("env-1", TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
            Interlocked.Increment(ref sendCount); // Worker B's own, independent send.
            var bSettled = store.MarkDispatchedAsync("env-1", claimB!.LeaseToken!).GetAwaiter().GetResult();
            Assert.True(bSettled);

            ctx.Response = BenzeneResult.Accepted<Void>();
        })));
        await store.AddAsync([NewEnvelope()]);

        var loggerFactory = new FakeLoggerFactory();
        var dispatcherA = new OutboxDispatcher(
            store, factoryA, new OutboxOptions { ClaimLease = TimeSpan.FromSeconds(1) },
            logger: loggerFactory.CreateLogger("WorkerA"), now: () => now);

        // Worker A's own dispatch: claims, sends (triggering B's reclaim+dispatch above mid-send), and
        // then tries to settle with what is now a stale token.
        var outcomeA = await dispatcherA.DispatchOneAsync("env-1");

        // A's own send genuinely happened, so its dispatch outcome is still "Dispatched" from A's point
        // of view - it is the STORE write, not the send, that fencing refuses.
        Assert.Equal(OutboxDispatchOutcome.Dispatched, outcomeA);
        Assert.Equal(2, sendCount); // Both workers really sent - fencing cannot prevent that part.

        // A's stale settle was refused and logged as a warning (not an error - B owns the outcome now).
        Assert.Contains(loggerFactory.Collector.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("reclaimed"));

        // The envelope is exactly as B left it - Dispatched, not reopened for a third claim.
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        factoryA.Dispose();
    }

    /// <summary>
    /// The more severe form of the round-6 bug: without fencing, a stale claimant whose OWN send
    /// failed could call <see cref="IOutboxStore.RescheduleAsync"/> with its stale token and flip an
    /// envelope the new holder already marked <see cref="OutboxStatus.Dispatched"/> back to
    /// <see cref="OutboxStatus.Pending"/> - resurrecting an already-delivered envelope for a THIRD
    /// send. Fencing must reject that stale reschedule too.
    /// </summary>
    [Fact]
    public async Task StaleClaimant_RescheduleAfterReclaim_DoesNotResurrectAnAlreadyDispatchedEnvelope()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutboxStore(() => now);
        await store.AddAsync([NewEnvelope()]);

        var claimA = await store.ClaimAsync("env-1", TimeSpan.FromSeconds(1));
        Assert.NotNull(claimA);

        now = now.AddSeconds(2); // A's lease lapses
        var claimB = await store.ClaimAsync("env-1", TimeSpan.FromMinutes(5));
        Assert.NotNull(claimB);
        var dispatched = await store.MarkDispatchedAsync("env-1", claimB!.LeaseToken!);
        Assert.True(dispatched);

        // A, believing its send failed, tries to reschedule with its stale token.
        var staleRescheduled = await store.RescheduleAsync("env-1", 1, TimeSpan.FromMinutes(1), "A thinks it failed", claimA!.LeaseToken!);

        Assert.False(staleRescheduled);
        // Still Dispatched - not resurrected as Pending, so no third claim/send is possible.
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));
        Assert.Null(await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1)));
    }
}
