using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Outbox;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
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
        await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        await store.MarkDispatchedAsync("old-dispatched");

        now = now.AddDays(10);
        var dispatcher = new OutboxDispatcher(store, factory, new OutboxOptions { RetentionPeriod = TimeSpan.FromDays(7) }, now: () => now);

        var result = await dispatcher.RunOnceAsync();

        Assert.Equal(1, result.DeletedRetired);

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
}
