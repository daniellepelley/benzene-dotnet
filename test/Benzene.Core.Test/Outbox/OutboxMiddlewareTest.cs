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
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Outbox;

// OutboxMiddleware is exercised through its public surface, the same technique
// DefaultBenzeneMessageSenderTest uses: AddOutboundRouting -> resolved IBenzeneMessageSender.
public class OutboxMiddlewareTest
{
    private static (ServiceCollection Services, MicrosoftBenzeneServiceContainer Container) NewContainer()
    {
        var services = new ServiceCollection();
        services.AddTransient<ISerializer, JsonSerializer>();
        var container = new MicrosoftBenzeneServiceContainer(services);
        return (services, container);
    }

    [Fact]
    public async Task Capture_StoresEnvelope_SetsAcceptedResponse_AndDoesNotCallNext()
    {
        var (services, container) = NewContainer();
        container.AddOutbox();
        container.AddInMemoryOutboxStore();

        var terminalCalled = false;
        container.AddOutboundRouting(routing => routing
            .Route("test:topic", pipeline => pipeline
                .UseOutbox()
                .OnRequest(_ => terminalCalled = true)));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var result = await sender.SendAsync<string, Void>("test:topic", "payload");

        Assert.False(terminalCalled);
        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);

        var store = resolver.GetService<IOutboxStore>();
        var due = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        var envelope = Assert.Single(due);
        Assert.Equal("test:topic", envelope.Topic);
        Assert.Equal(OutboxStatus.Pending, envelope.Status);
    }

    [Fact]
    public async Task Capture_NonVoidResponseRequested_ThrowsResponseTypeMismatchException()
    {
        var (services, container) = NewContainer();
        container.AddOutbox();
        container.AddInMemoryOutboxStore();
        container.AddOutboundRouting(routing => routing
            .Route("test:topic", pipeline => pipeline.UseOutbox()));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var exception = await Assert.ThrowsAsync<OutboundResponseTypeMismatchException>(
            () => sender.SendAsync<string, string>("test:topic", "payload"));

        Assert.Equal("test:topic", exception.Topic);
        Assert.Equal(typeof(Void), exception.ActualResponseType);
        Assert.Equal(typeof(string), exception.RequestedResponseType);
    }

    [Fact]
    public async Task Capture_NoIdempotencyKeyHeader_StampsEnvelopeIdByDefault()
    {
        var (services, container) = NewContainer();
        container.AddOutbox();
        container.AddInMemoryOutboxStore();
        container.AddOutboundRouting(routing => routing
            .Route("test:topic", pipeline => pipeline.UseOutbox()));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();
        await sender.SendAsync<string, Void>("test:topic", "payload");

        var store = resolver.GetService<IOutboxStore>();
        var envelope = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        Assert.Equal(envelope.Id, envelope.Headers[OutboxDefaults.IdempotencyKeyHeaderName]);
    }

    [Fact]
    public async Task Capture_ExistingIdempotencyKeyHeader_IsPreservedNotOverwritten()
    {
        var (services, container) = NewContainer();
        container.AddOutbox();
        container.AddInMemoryOutboxStore();
        container.AddOutboundRouting(routing => routing
            .Route("test:topic", pipeline => pipeline.UseOutbox()));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();
        var callerHeaders = new Dictionary<string, string> { [OutboxDefaults.IdempotencyKeyHeaderName] = "caller-supplied" };
        await sender.SendAsync<string, Void>("test:topic", "payload", callerHeaders);

        var store = resolver.GetService<IOutboxStore>();
        var envelope = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        Assert.Equal("caller-supplied", envelope.Headers[OutboxDefaults.IdempotencyKeyHeaderName]);
    }

    [Fact]
    public async Task Capture_StampIdempotencyKeyDisabled_HeaderNotAdded()
    {
        var (services, container) = NewContainer();
        container.AddOutbox();
        container.AddInMemoryOutboxStore();
        container.AddOutboundRouting(routing => routing
            .Route("test:topic", pipeline => pipeline.UseOutbox(o => o.StampIdempotencyKey = false)));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();
        await sender.SendAsync<string, Void>("test:topic", "payload");

        var store = resolver.GetService<IOutboxStore>();
        var envelope = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        Assert.False(envelope.Headers.ContainsKey(OutboxDefaults.IdempotencyKeyHeaderName));
    }

    [Fact]
    public async Task Dispatch_PassesThrough_AndStoredHeadersWinOverAmbientStamps()
    {
        var (services, container) = NewContainer();
        container.AddOutbox();
        container.AddInMemoryOutboxStore();

        OutboundContext? recorded = null;
        container.AddOutboundRouting(routing => routing
            .Route("test:topic", pipeline => pipeline
                // Simulates an ambient stamping middleware (e.g. UseW3CTraceContext()) running before
                // UseOutbox() and setting a header from the RELAY host's own ambient context.
                .OnRequest(ctx => ctx.Headers["traceparent"] = "ambient-value")
                .UseOutbox()
                .OnRequest(ctx =>
                {
                    recorded = ctx;
                    ctx.Response = BenzeneResult.Accepted<Void>();
                })));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());

        // Simulate what IOutboxDispatcher does before re-sending a captured envelope: mark the
        // current scope's OutboxDispatchScope with the envelope's originally captured headers.
        var dispatchScope = resolver.GetService<OutboxDispatchScope>();
        dispatchScope.Begin(new Dictionary<string, string> { ["traceparent"] = "stored-business-time-value" });

        var sender = resolver.GetService<IBenzeneMessageSender>();
        var result = await sender.SendAsync<string, Void>("test:topic", "payload");

        Assert.NotNull(recorded);
        Assert.Equal("stored-business-time-value", recorded!.Headers["traceparent"]);
        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Capture_StoreThrows_PropagatesException()
    {
        var (services, container) = NewContainer();
        container.AddOutbox();

        var mockStore = new Mock<IOutboxStore>();
        mockStore
            .Setup(s => s.AddAsync(It.IsAny<IEnumerable<OutboxEnvelope>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        services.AddSingleton<IOutboxStore>(mockStore.Object);

        container.AddOutboundRouting(routing => routing
            .Route("test:topic", pipeline => pipeline.UseOutbox()));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync<string, Void>("test:topic", "payload"));
    }

    [Fact]
    public async Task Capture_TransactionalWriteMode_StagesInsteadOfWritingToStore()
    {
        var (services, container) = NewContainer();
        container.AddOutbox();
        container.AddInMemoryOutboxStore();
        container.AddOutboundRouting(routing => routing
            .Route("test:topic", pipeline => pipeline.UseOutbox(o => o.WriteMode = OutboxWriteMode.Transactional)));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();
        await sender.SendAsync<string, Void>("test:topic", "payload");

        var store = resolver.GetService<IOutboxStore>();
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        var stage = resolver.GetService<BufferedOutboxStage>();
        var staged = Assert.Single(stage.DrainStaged());
        Assert.Equal("test:topic", staged.Topic);
    }
}
