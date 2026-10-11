using System.Linq;
using System.Text.Json.Nodes;
using Benzene.Abstractions.Messages;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Benzene.Schema.OpenApi;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Autogen.Schema.OpenApi;

/// <summary>
/// Send declarations (contract-document.md §3.2) reach the document from every registered
/// <see cref="IMessageDefinitionFinder{TMessageDefinition}"/> of <see cref="IMessageSenderDefinition"/> -
/// the application's own <see cref="MeshOutboundRegistry"/> and each generated client's
/// <c>{Service}ServiceClientSends</c> - not just the last one registered.
/// </summary>
public class SpecBuilderSendsTest
{
    private sealed class ClientSends : IMessageDefinitionFinder<IMessageSenderDefinition>
    {
        private readonly IMessageSenderDefinition[] _definitions;

        public ClientSends(params IMessageSenderDefinition[] definitions) => _definitions = definitions;

        public IMessageSenderDefinition[] FindDefinitions() => _definitions;
    }

    private static JsonObject BuildDocument(params IMessageDefinitionFinder<IMessageSenderDefinition>[] finders)
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.SetApplicationInfo("orders", string.Empty, string.Empty);
        foreach (var finder in finders)
        {
            container.AddSingleton(finder);
        }

        using var factory = container.CreateServiceResolverFactory();
        var json = new SpecBuilder().CreateSpec(factory.CreateScope(), new SpecRequest("benzene", "json"));
        return JsonNode.Parse(json)!.AsObject();
    }

    private static string[] SendTopics(JsonObject document) =>
        document["sends"]!.AsArray().Select(x => x!["topic"]!.GetValue<string>()).ToArray();

    [Fact]
    public void EveryRegisteredFinder_ContributesItsSends()
    {
        var document = BuildDocument(
            new MeshOutboundRegistry().Register<ExampleRequestPayload>("payments:take"),
            new ClientSends(MessageSenderDefinition.CreateInstance("inventory:reserve", "",
                typeof(ExampleRequestPayload), typeof(ExampleResponsePayload), typeof(Void))));

        Assert.Equal(new[] { "inventory:reserve", "payments:take" }, SendTopics(document).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ATopicAndVersionDeclaredByTwoFinders_IsListedOnce()
    {
        var document = BuildDocument(
            new MeshOutboundRegistry().Register<ExampleRequestPayload, ExampleResponsePayload>("inventory:reserve", "v2"),
            new ClientSends(MessageSenderDefinition.CreateInstance("inventory:reserve", "v2",
                typeof(ExampleRequestPayload), typeof(ExampleResponsePayload), typeof(Void))));

        var reserve = Assert.Single(document["sends"]!.AsArray());
        Assert.Equal("v2", reserve!["version"]!.GetValue<string>());
    }

    [Fact]
    public void TheSameTopicAtTwoVersions_IsTwoSends()
    {
        var document = BuildDocument(
            new MeshOutboundRegistry().Register<ExampleRequestPayload>("inventory:reserve", "v1"),
            new ClientSends(MessageSenderDefinition.CreateInstance("inventory:reserve", "v2",
                typeof(ExampleRequestPayload), typeof(ExampleResponsePayload), typeof(Void))));

        Assert.Equal(2, document["sends"]!.AsArray().Count);
    }
}
