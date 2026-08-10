using System;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Clients;
using Benzene.Clients.InProcess;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Clients.InProcess;

/// <summary>
/// Coverage for named in-process pipelines: several modules registered within one
/// <c>AddInProcessMessaging(...)</c> call, routed independently via <c>.UseInProcess(name)</c>, and
/// the safety nets around getting a name wrong - a second top-level call, a duplicate name within
/// one call, a route naming a pipeline nothing registered.
/// </summary>
public class InProcessNamedPipelinesTest
{
    private class EchoHandler : IMessageHandler<string, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(string request)
            => Task.FromResult(BenzeneResult.Ok($"echo:{request}"));
    }

    private class ShoutHandler : IMessageHandler<string, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(string request)
            => Task.FromResult(BenzeneResult.Ok($"{request.ToUpperInvariant()}!"));
    }

    [Fact]
    public async Task TwoNamedPipelinesInOneCall_EachRouteDispatchesToItsOwnPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry
            .Add("billing", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<EchoHandler, string, string>("billing:echo")))
            .Add("shipping", pipeline => pipeline.UseMessageHandlers(Array.Empty<Type>(),
                router => router.AddMessageHandler<ShoutHandler, string, string>("shipping:shout"))));

        container.AddOutboundRouting(routing => routing
            .Route("billing:echo", pipeline => pipeline.UseInProcess("billing"))
            .Route("shipping:shout", pipeline => pipeline.UseInProcess("shipping")));

        var resolver = new MicrosoftServiceResolverAdapter(services.BuildServiceProvider());
        var sender = resolver.GetService<IBenzeneMessageSender>();

        var billingResult = await sender.SendAsync<string, string>("billing:echo", "hello");
        var shippingResult = await sender.SendAsync<string, string>("shipping:shout", "hello");

        Assert.Equal("echo:hello", billingResult.Payload);
        Assert.Equal("HELLO!", shippingResult.Payload);
    }

    [Fact]
    public void ASecondTopLevelAddInProcessMessagingCall_ThrowsRatherThanSilentlyShadowingTheFirst()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(pipeline => pipeline.UseMessageHandlers());

        var exception = Assert.Throws<InProcessMessagingAlreadyRegisteredException>(
            () => container.AddInProcessMessaging(pipeline => pipeline.UseMessageHandlers()));

        Assert.Contains("already called", exception.Message);
    }

    [Fact]
    public void ASecondTopLevelCall_ThrowsEvenWhenTheFirstUsedTheNamedOverload()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry.Add("billing", pipeline => pipeline.UseMessageHandlers()));

        Assert.Throws<InProcessMessagingAlreadyRegisteredException>(
            () => container.AddInProcessMessaging(registry => registry.Add("shipping", pipeline => pipeline.UseMessageHandlers())));
    }

    [Fact]
    public void TheSameNameAddedTwiceWithinOneCall_ThrowsNamingTheDuplicate()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        var exception = Assert.Throws<DuplicateInProcessPipelineException>(() =>
            container.AddInProcessMessaging(registry => registry
                .Add("billing", pipeline => pipeline.UseMessageHandlers())
                .Add("billing", pipeline => pipeline.UseMessageHandlers())));

        Assert.Equal("billing", exception.Name);
    }

    [Fact]
    public void Resolve_AnUnregisteredName_ThrowsListingTheNamesThatAreRegistered()
    {
        var registry = new InProcessDispatcherRegistry(
            new System.Collections.Generic.Dictionary<string, Abstractions.Middleware.IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>>());

        var exception = Assert.Throws<InProcessPipelineNotFoundException>(() => registry.Resolve("billing"));

        Assert.Equal("billing", exception.Name);
        Assert.Contains("never called", exception.Message);
    }

    [Fact]
    public void StartUpCheck_ARouteNamingAPipelineNothingRegistered_ThrowsAtStartUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        // "billing" is routed to but AddInProcessMessaging never registers it - a typo, or a
        // forgotten registration.
        container.AddOutboundRouting(routing => routing
            .Route("billing:charge", pipeline => pipeline.UseInProcess("billing")));

        using var scope = new MicrosoftServiceResolverFactory(services.BuildServiceProvider()).CreateScope();

        var exception = Assert.Throws<MissingInProcessPipelineException>(
            () => new InProcessRouteStartUpCheck().Check(scope));

        Assert.Contains("billing", exception.MissingNames);
        Assert.Contains("never called", exception.Message);
    }

    [Fact]
    public void StartUpCheck_EveryRoutedNameIsRegistered_Passes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry.Add("billing", pipeline => pipeline.UseMessageHandlers()));
        container.AddOutboundRouting(routing => routing
            .Route("billing:charge", pipeline => pipeline.UseInProcess("billing")));

        using var scope = new MicrosoftServiceResolverFactory(services.BuildServiceProvider()).CreateScope();

        // Does not throw.
        new InProcessRouteStartUpCheck().Check(scope);
    }

    [Fact]
    public void StartUpCheck_NoInProcessRoutesAtAll_Passes()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        using var scope = new MicrosoftServiceResolverFactory(services.BuildServiceProvider()).CreateScope();

        // Does not throw - nothing routes in-process, so there is nothing to validate.
        new InProcessRouteStartUpCheck().Check(scope);
    }

    [Fact]
    public void UseInProcess_RegistersTheStartUpCheckAlongsideTheOthers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddInProcessMessaging(registry => registry.Add("billing", pipeline => pipeline.UseMessageHandlers()));
        container.AddOutboundRouting(routing => routing
            .Route("billing:charge", pipeline => pipeline.UseInProcess("billing")));

        using var scope = new MicrosoftServiceResolverFactory(services.BuildServiceProvider()).CreateScope();

        Assert.Contains("in-process-routes", scope.GetServices<IStartUpCheck>().Select(x => x.Name));
    }
}
