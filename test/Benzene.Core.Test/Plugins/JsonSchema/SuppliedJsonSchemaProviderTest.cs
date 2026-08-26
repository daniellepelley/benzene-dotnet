using System.Threading.Tasks;
using Benzene.Abstractions.Messages;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.TestHelpers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.JsonSchema;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Plugins.JsonSchema;

public class SuppliedJsonSchemaProviderTest
{
    private const string StrictSchemaJson = /*lang=json*/ """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "name": { "type": "string", "maxLength": 5 }
          },
          "required": [ "name" ]
        }
        """;

    private static async Task<(string StatusCode, string Body)> SendWithBodyAsync(
        SuppliedJsonSchemaCatalog catalog, string name)
    {
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x
            .AddBenzeneMessage()
            // Registered in ConfigureServices - must win over UseJsonSchema's default provider.
            .AddSuppliedJsonSchemas(catalog));

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(
            new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline
            .UseJsonSchema()
            .UseMessageHandlers();

        var app = new BenzeneMessageApplication(pipeline.Build());

        var request = MessageBuilder.Create(Defaults.Topic, new ExampleRequestPayload
        {
            Id = 42,
            Name = name,
            Mapped = "some-value"
        }).AsBenzeneMessage();

        var response = await app.HandleAsync(request,
            new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider()));
        return (response.StatusCode, response.Body);
    }

    private static async Task<string> SendAsync(SuppliedJsonSchemaCatalog catalog, string name)
    {
        return (await SendWithBodyAsync(catalog, name)).StatusCode;
    }

    [Theory]
    [InlineData("foo", BenzeneResultStatus.Ok)]
    [InlineData("foo-bar-foo-bar", BenzeneResultStatus.ValidationError)]
    public async Task MappedRequestType_ValidatesAgainstSuppliedSchema(string name, string expectedStatus)
    {
        var catalog = new SuppliedJsonSchemaCatalog()
            .AddJson(typeof(ExampleRequestPayload), StrictSchemaJson);

        Assert.Equal(expectedStatus, await SendAsync(catalog, name));
    }

    [Fact]
    public async Task ValidationFailure_CarriesTheHumanMessage()
    {
        // Same failure contract as FluentValidation/DataAnnotations: the ValidationError result's
        // errors carry a human-readable message per failing property. The field/code structure
        // itself (JsonSchemaValidationErrors.Format - see JsonSchemaValidationErrorsTest) travels on
        // IBenzeneResult.Errors[].Field/.Code, not folded into the message text (see
        // work/benzene-result-errors-ruling.md §5.1: "Benzene.JsonSchema should stop prefixing the
        // pointer into the message string once Field exists") - so "detail" (the joined message
        // text) never contains the pointer, even though (since work/archive/problem-details-plan-2026-08.md Phase 3)
        // the wire body's "errors" member carries that same pointer verbatim in "field".
        var catalog = new SuppliedJsonSchemaCatalog()
            .AddJson(typeof(ExampleRequestPayload), StrictSchemaJson);

        var response = await SendWithBodyAsync(catalog, "foo-bar-foo-bar");

        Assert.Equal(BenzeneResultStatus.ValidationError, response.StatusCode);

        var problem = new Benzene.Core.MessageHandlers.Serialization.JsonSerializer().Deserialize<ProblemDetails>(response.Body);
        Assert.DoesNotContain("/name", problem.Detail);
        Assert.Contains("characters", problem.Detail);
        Assert.Equal("/name", Assert.Single(problem.Errors).Field);
    }

    [Fact]
    public async Task UnmappedRequestType_FallsBackToGeneratedSchema()
    {
        // Empty catalog: the provider falls back to the default generated-from-type schema,
        // which this valid payload passes.
        Assert.Equal(BenzeneResultStatus.Ok, await SendAsync(new SuppliedJsonSchemaCatalog(), "foo"));
    }

    private const string VersionBlindnessV1SchemaJson = /*lang=json*/ """
        { "type": "object", "properties": { "id": { "type": "integer" } }, "required": [ "id" ] }
        """;

    private const string VersionBlindnessV2SchemaJson = /*lang=json*/ """
        { "type": "object", "properties": { "id": { "type": "string" } }, "required": [ "id" ] }
        """;

    [Fact]
    public async Task TwoHandlerVersions_CatalogLookup_UsesTheDeclaredVersionsRequestType()
    {
        // WP-P (work/bug-fix-designs-round7-10-2026-08.md), task #69: without version-augmentation,
        // resolving the request type for the catalog lookup is version-blind too - it would resolve
        // VersionBlindnessV2Request's catalog entry (the max-ordinal fallback) for a v1 request and
        // reject the valid v1 int payload against v2's string-typed schema.
        var catalog = new SuppliedJsonSchemaCatalog()
            .AddJson(typeof(VersionBlindnessV1Request), VersionBlindnessV1SchemaJson)
            .AddJson(typeof(VersionBlindnessV2Request), VersionBlindnessV2SchemaJson);

        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage().AddSuppliedJsonSchemas(catalog));

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(
            new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline.UseJsonSchema().UseMessageHandlers();

        var app = new BenzeneMessageApplication(pipeline.Build());

        var request = MessageBuilder.Create(VersionBlindnessDefaults.Topic, new VersionBlindnessV1Request { Id = 42 })
            .WithHeader(MessageVersionHeaders.Default, "v1")
            .AsBenzeneMessage();

        var response = await app.HandleAsync(request,
            new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider()));

        Assert.Equal(BenzeneResultStatus.Ok, response.StatusCode);
    }
}
