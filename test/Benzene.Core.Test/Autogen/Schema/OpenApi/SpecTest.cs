using System;
using System.Threading.Tasks;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.BenzeneMessage;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.TestHelpers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http;
using Benzene.Microsoft.Dependencies;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;
using Benzene.Test.Examples;
using Benzene.Testing;
using Benzene.Aws.Lambda.Core.TestHelpers;
using ByteBard.AsyncAPI.Readers;
using Microsoft.OpenApi.Readers;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi;

public class SpecTest
{
    // The handlers these specs are generated from, named rather than scanned for.
    //
    // UseMessageHandlers() with no arguments scans AppDomain.CurrentDomain.GetAssemblies(), so the
    // schema counts below used to be a count of every [Message] handler anywhere in the test process.
    // Adding one for an unrelated feature broke six assertions here with "Expected: 6, Actual: 8", and
    // loading any assembly at all — the docs tests compile and load one — could break them depending
    // on which test ran first. The assertions were the symptom; the ambient scan was the disease.
    private static readonly Type[] SpecHandlers =
    {
        typeof(ExampleMessageHandler),
        typeof(ExampleMessageHandlerV2),
        typeof(ExampleMessageWithGuidHandler),
        typeof(ExampleNoResponseMessageHandler),
        typeof(Benzene.Test.Aws.DynamoDb.ExampleOrderInsertedHandler),
        typeof(Benzene.Test.Diagnostics.FailDiagNotFoundHandler),
        typeof(Benzene.Test.Diagnostics.FailDiagThrowHandler),
        typeof(Benzene.Test.Diagnostics.LogLevelOkHandler),
        typeof(Benzene.Test.ResponseEvents.ResponseEventsCreateOrderHandler)
    };

    private AwsLambdaBenzeneTestHost CreateStandardHost()
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(x => x
                .UsingBenzene(x => x
                    .AddBenzene()
                    .AddBenzeneMessage()
                    .AddHttpMessageHandlers()
                    .SetApplicationInfo("Example App", "1.0", "Stuff")
                ))
            .Configure(app =>
            {
                app.UseBenzeneMessage(x => x
                    .UseSpec()
                    .UseMessageHandlers(SpecHandlers)
                );
            })
            .BuildHost();
    }

    private AwsLambdaBenzeneTestHost CreateIncompleteHost()
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(x => x
                .UsingBenzene(x => x
                    .AddBenzene()
                    .AddBenzeneMessage()
                    .SetApplicationInfo("Example App", "1.0", "Stuff")
                ))
            .Configure(app =>
            {
                app.UseBenzeneMessage(x => x
                    .UseSpec()
                    .UseMessageHandlers(SpecHandlers)
                );
            })
            .BuildHost();
    }

    [Fact]
    public async Task OpenApi_Test()
    {
        var host = CreateStandardHost();
        var response = await host.SendBenzeneMessageAsync(MessageBuilder.Create("benzene:spec", new SpecRequest("openapi","json")));
        var document = new OpenApiStringReader().Read(response.Body, out _);

        Assert.Equal(2, document.Components.Schemas.Count);
    }

    [Fact]
    public async Task AsyncApi_Test()
    {
        var host = CreateStandardHost();
        var response = await host.SendBenzeneMessageAsync(MessageBuilder.Create("benzene:spec", new SpecRequest("asyncapi","json")));
        var document = new AsyncApiStringReader().Read(response.Body, out _);

        Assert.Equal(6, document.Components.Schemas.Count);
    }

    [Fact]
    public async Task BenzeneApi_Test()
    {
        var host = CreateStandardHost();
        var response = await host.SendBenzeneMessageAsync(MessageBuilder.Create("benzene:spec", new SpecRequest("benzene", "json")));
        var document = new EventServiceDocumentDeserializer().Deserialize(response.Body);

        Assert.Equal(6, document.Components.Schemas.Count);
    }

    [Fact]
    public async Task OpenApi_MissingDependencies_Test()
    {
        var host = CreateIncompleteHost();
        var response = await host.SendBenzeneMessageAsync(MessageBuilder.Create("benzene:spec", new SpecRequest("openapi","json")));
        var document = new OpenApiStringReader().Read(response.Body, out _);

        Assert.Equal(0, document.Components.Schemas.Count);
    }

    [Fact]
    public async Task AsyncApi_MissingDependencies_Test()
    {
        var host = CreateIncompleteHost();
        var response = await host.SendBenzeneMessageAsync(MessageBuilder.Create("benzene:spec", new SpecRequest("asyncapi", "json")));
        var document = new AsyncApiStringReader().Read(response.Body, out _);

        Assert.Equal(6, document.Components.Schemas.Count);
    }

    [Fact]
    public async Task BenzeneApi_MissingDependencies_Test()
    {
        var host = CreateIncompleteHost();
        var response = await host.SendBenzeneMessageAsync(MessageBuilder.Create("benzene:spec", new SpecRequest("benzene", "json")));
        var document = new EventServiceDocumentDeserializer().Deserialize(response.Body);

        Assert.Equal(6, document.Components.Schemas.Count);
    }

    [Fact]
    public async Task BenzeneApi_InvalidFormatDefaultsToJson()
    {
        var host = CreateIncompleteHost();
        var response = await host.SendBenzeneMessageAsync(MessageBuilder.Create("benzene:spec", new SpecRequest("benzene", "foo")));
        var document = new EventServiceDocumentDeserializer().Deserialize(response.Body);

        Assert.Equal(6, document.Components.Schemas.Count);
    }

    [Fact]
    public async Task BenzeneApi_InvalidTypeDefaultsToBenzene()
    {
        var host = CreateIncompleteHost();
        var response = await host.SendBenzeneMessageAsync(MessageBuilder.Create("benzene:spec", new SpecRequest("benzene", "foo")));
        var document = new EventServiceDocumentDeserializer().Deserialize(response.Body);

        Assert.Equal(6, document.Components.Schemas.Count);
    }

    [Fact]
    public async Task BenzeneApi_NullRequestDefaultsToBenzene()
    {
        // A null request (empty body hitting the spec topic) must default to the benzene format -
        // the documented default and what an unknown/empty type string resolves to - not asyncapi.
        var serviceContainer = new MicrosoftBenzeneServiceContainer();
        serviceContainer
            .AddBenzene()
            .AddBenzeneMessage()
            .AddHttpMessageHandlers()
            .SetApplicationInfo("Example App", "1.0", "Stuff");

        var resolver = serviceContainer.CreateServiceResolverFactory().CreateScope();
        var handler = new SpecMessageHandler(resolver);

        var result = await handler.HandleAsync(null);

        // A benzene EventServiceDocument carries the app info; an asyncapi document would not
        // round-trip through this deserializer with the app title populated.
        var document = new EventServiceDocumentDeserializer().Deserialize(result.Payload.Content);
        Assert.Equal("Example App", document.Info.Title);
    }
}
