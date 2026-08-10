using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Clients.InProcess;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients.InProcess;

/// <summary>
/// Coverage for <c>.UseInProcessFanOut(...)</c>: one outbound send dispatched to several named
/// in-process pipelines concurrently, each under its own topic, each isolated from the others'
/// failures - matching what real SNS fan-out actually does (accepted once published, no visibility
/// into subscriber outcomes). See <see cref="InProcessFanOutTarget"/>'s doc comment for why each
/// target needs its own topic: Benzene's (topic, version) → at most one handler invariant is
/// process-wide, not per in-process pipeline.
/// </summary>
public class InProcessFanOutTest
{
    private static ConcurrentBag<string> Calls = new();

    private class RecordingHandler : IMessageHandler<string, string>
    {
        private readonly string _name;
        public RecordingHandler(string name) => _name = name;
        public Task<IBenzeneResult<string>> HandleAsync(string request)
        {
            Calls.Add(_name);
            return Task.FromResult(BenzeneResult.Ok(request));
        }
    }

    private class BillingHandler : RecordingHandler
    {
        public BillingHandler() : base("billing") { }
    }

    private class ShippingHandler : RecordingHandler
    {
        public ShippingHandler() : base("shipping") { }
    }

    private class ThrowingHandler : IMessageHandler<string, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(string request) =>
            throw new InvalidOperationException("boom");
    }

    private class FailingHandler : IMessageHandler<string, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(string request) =>
            Task.FromResult(BenzeneResult.ValidationError<string>("nope"));
    }

    private static ServiceCollection ServicesWithLogging()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    [Fact]
    public async Task SendAsync_RoutedThroughFanOut_DispatchesToEveryTargetUnderItsOwnTopic()
    {
        Calls = new ConcurrentBag<string>();
        var services = ServicesWithLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry
            .Add("billing", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<BillingHandler, string, string>("billing:order-created")))
            .Add("shipping", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<ShippingHandler, string, string>("shipping:order-created"))));

        container.AddOutboundRouting(routing => routing
            .Route("order:created", pipeline => pipeline.UseInProcessFanOut(
                new InProcessFanOutTarget("billing", "billing:order-created"),
                new InProcessFanOutTarget("shipping", "shipping:order-created"))));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var result = await sender.SendAsync<string, Void>("order:created", "hello");

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Equal(new[] { "billing", "shipping" }, Calls.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SendAsync_OneConsumerThrows_TheOtherStillReceivesTheMessageAndTheCallerStillSucceeds()
    {
        Calls = new ConcurrentBag<string>();
        var services = ServicesWithLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry
            .Add("broken", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<ThrowingHandler, string, string>("broken:order-created")))
            .Add("shipping", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<ShippingHandler, string, string>("shipping:order-created"))));

        container.AddOutboundRouting(routing => routing
            .Route("order:created", pipeline => pipeline.UseInProcessFanOut(
                new InProcessFanOutTarget("broken", "broken:order-created"),
                new InProcessFanOutTarget("shipping", "shipping:order-created"))));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var result = await sender.SendAsync<string, Void>("order:created", "hello");

        // The throwing consumer is isolated: it does not fail the caller's response, and the other
        // consumer still ran.
        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Contains("shipping", Calls);
    }

    [Fact]
    public async Task SendAsync_OneConsumerReturnsAFailureStatus_DoesNotAffectTheCallerOrTheOtherConsumer()
    {
        Calls = new ConcurrentBag<string>();
        var services = ServicesWithLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry
            .Add("failing", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<FailingHandler, string, string>("failing:order-created")))
            .Add("shipping", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<ShippingHandler, string, string>("shipping:order-created"))));

        container.AddOutboundRouting(routing => routing
            .Route("order:created", pipeline => pipeline.UseInProcessFanOut(
                new InProcessFanOutTarget("failing", "failing:order-created"),
                new InProcessFanOutTarget("shipping", "shipping:order-created"))));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var result = await sender.SendAsync<string, Void>("order:created", "hello");

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Contains("shipping", Calls);
    }

    [Fact]
    public async Task SendAsync_RequestingANonVoidResponse_ThrowsTheSameMismatchExceptionAsSqsOrSns()
    {
        var services = ServicesWithLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry
            .Add("billing", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<BillingHandler, string, string>("billing:order-created"))));

        container.AddOutboundRouting(routing => routing
            .Route("order:created", pipeline => pipeline.UseInProcessFanOut(
                new InProcessFanOutTarget("billing", "billing:order-created"))));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        // Fan-out always produces Void, exactly like SQS/SNS - requesting a real response type is the
        // same caller mistake DefaultBenzeneMessageSender already names for those transports.
        await Assert.ThrowsAsync<OutboundResponseTypeMismatchException>(
            () => sender.SendAsync<string, string>("order:created", "hello"));
    }

    [Fact]
    public void UseInProcessFanOut_WithNoTargets_ThrowsRatherThanBuildingAUselessRoute()
    {
        var services = ServicesWithLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        Assert.Throws<ArgumentException>(() =>
            container.AddOutboundRouting(routing => routing
                .Route("order:created", pipeline => pipeline.UseInProcessFanOut())));
    }

    [Fact]
    public void UseInProcessFanOut_TwoTargetsShareATopic_ThrowsRatherThanSilentlyMisrouting()
    {
        var services = ServicesWithLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        // This is exactly the mistake that produces a silent misroute if left uncaught: two targets
        // both claiming "order:created" would mean two different in-process pipelines need a handler
        // for the identical topic, which Benzene's process-wide topic model does not allow.
        var exception = Assert.Throws<DuplicateInProcessFanOutTargetException>(() =>
            container.AddOutboundRouting(routing => routing
                .Route("order:created", pipeline => pipeline.UseInProcessFanOut(
                    new InProcessFanOutTarget("billing", "order:created"),
                    new InProcessFanOutTarget("shipping", "order:created")))));

        Assert.Equal("order:created", exception.Topic);
    }

    [Fact]
    public async Task SendAsync_OneTargetsPipelineIsNeverRegistered_ThrowsTheSamePipelineNotFoundExceptionAsASingleTargetRoute()
    {
        var services = ServicesWithLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry
            .Add("billing", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<BillingHandler, string, string>("billing:order-created"))));

        // "shipping" is never registered.
        container.AddOutboundRouting(routing => routing
            .Route("order:created", pipeline => pipeline.UseInProcessFanOut(
                new InProcessFanOutTarget("billing", "billing:order-created"),
                new InProcessFanOutTarget("shipping", "shipping:order-created"))));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var exception = await Assert.ThrowsAsync<InProcessPipelineNotFoundException>(
            () => sender.SendAsync<string, Void>("order:created", "hello"));

        Assert.Equal("shipping", exception.Name);
    }
}
