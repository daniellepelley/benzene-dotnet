using System;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Info;
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

namespace Benzene.Test.Clients.InProcess;

/// <summary>
/// End-to-end coverage for the in-process transport: an outbound route converted via
/// <c>.UseInProcess()</c> dispatches straight to a handler registered by
/// <c>AddInProcessMessaging</c>, without any wire transport in between.
/// </summary>
public class InProcessTransportTest
{
    public class EchoHandler : IMessageHandler<string, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(string request)
            => Task.FromResult(BenzeneResult.Ok($"echo:{request}"));
    }

    public class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    public class ScopeMarkerHandler : IMessageHandler<string, string>
    {
        private readonly ScopeMarker _marker;

        public ScopeMarkerHandler(ScopeMarker marker)
        {
            _marker = marker;
        }

        public Task<IBenzeneResult<string>> HandleAsync(string request)
            => Task.FromResult(BenzeneResult.Ok(_marker.Id.ToString()));
    }

    [Fact]
    public async Task SendAsync_RoutedThroughInProcess_DispatchesToTheRegisteredHandlerAndReturnsATypedResponse()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
            router => router.AddMessageHandler<EchoHandler, string, string>("order:echo")));
        container.AddOutboundRouting(routing => routing
            .Route("order:echo", pipeline => pipeline.UseInProcess()));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var result = await sender.SendAsync<string, string>("order:echo", "hello");

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Equal("echo:hello", result.Payload);
    }

    [Fact]
    public async Task SendAsync_RoutedThroughInProcess_UnhandledTopic_ReturnsNotFoundInsteadOfThrowing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        // No handler registered for "order:echo" at all - matches MessageRouter's existing behaviour
        // for any other transport, so an in-process route to a topic nobody handles degrades honestly
        // rather than crashing or requiring a dedicated startup check.
        container.AddInProcessMessaging(pipeline => pipeline.UseMessageHandlers());
        container.AddOutboundRouting(routing => routing
            .Route("order:echo", pipeline => pipeline.UseInProcess()));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var result = await sender.SendAsync<string, string>("order:echo", "hello");

        Assert.Equal(BenzeneResultStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task SendAsync_RoutedThroughInProcess_HandlerRunsInItsOwnFreshDiScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ScopeMarker>();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
            router => router.AddMessageHandler<ScopeMarkerHandler, string, string>("order:marker")));
        container.AddOutboundRouting(routing => routing
            .Route("order:marker", pipeline => pipeline.UseInProcess()));

        var provider = services.BuildServiceProvider();
        var resolver = new MicrosoftServiceResolverAdapter(provider);
        var callerMarker = resolver.GetService<ScopeMarker>();
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var result = await sender.SendAsync<string, string>("order:marker", "hello");

        // The dispatched handler got its own scope (via IServiceResolverFactory), so it resolved a
        // different ScopeMarker instance than the caller's own - matching the isolation a real
        // out-of-process transport would give the receiving side.
        Assert.NotEqual(callerMarker.Id.ToString(), result.Payload);
    }

    [Fact]
    public void AddInProcessMessaging_RegistersItsOwnTransportInfo_NotBenzenesWireEndpointTransportInfo()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(pipeline => pipeline.UseMessageHandlers());

        var provider = services.BuildServiceProvider();
        var transportNames = provider.GetServices<ITransportInfo>().Select(t => t.Name).ToArray();

        // A service that only ever calls AddInProcessMessaging() never exposes a "benzene" wire
        // endpoint (no UseBenzeneMessage() HTTP/Lambda registration happened), so it must not
        // advertise one via ITransportInfo just because it shares the BenzeneMessage plumbing.
        Assert.Contains(TransportNames.InProcess, transportNames);
        Assert.DoesNotContain(TransportNames.Benzene, transportNames);
    }
}
