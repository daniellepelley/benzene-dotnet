using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Validation;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.TestHelpers;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.FluentValidation;
using Benzene.JsonSchema;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Plugins.JsonSchema;

// #99: a handler decorated [ValidationStatus] must have its status honoured by Benzene.JsonSchema
// the same way Benzene.FluentValidation already honours it, once a shared IValidationStatusMapper
// is registered (here, Benzene.FluentValidation's DefaultValidationStatusMapper - the mechanism is
// shared, Benzene.JsonSchema doesn't ship its own).
public class JsonSchemaStatusMapperPipelineTest
{
    // Deliberately an inline, $id-less schema (rather than re-loading schema.jsonc, which
    // JsonSchemaPipelineTest already loads under its own $id) - Json.Schema.Net's SchemaRegistry is
    // process-global and throws "Overwriting registered schemas is not permitted" if the same $id is
    // registered twice across test classes in the same run.
    private const string SchemaJson = /*lang=json*/ """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "id": { "type": "integer" },
            "name": { "type": "string", "maxLength": 3 },
            "mapped": { "type": "string" }
          },
          "required": [ "id", "name", "mapped" ]
        }
        """;

    private static readonly Json.Schema.JsonSchema Schema = Json.Schema.JsonSchema.FromText(SchemaJson);

    private const string StatusMappedTopic = "json-schema-status-mapped-test";
    private const string UndecoratedTopic = "json-schema-status-undecorated-test";

    [ValidationStatus(BenzeneResultStatus.BadRequest)]
    [Message(StatusMappedTopic)]
    public class StatusMappedHandler : IMessageHandler<ExampleRequestPayload, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(ExampleRequestPayload request)
        {
            return Task.FromResult(BenzeneResult.Ok("ok"));
        }
    }

    [Message(UndecoratedTopic)]
    public class UndecoratedHandler : IMessageHandler<ExampleRequestPayload, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(ExampleRequestPayload request)
        {
            return Task.FromResult(BenzeneResult.Ok("ok"));
        }
    }

    [Fact]
    public async Task ValidationStatusAttribute_OverridesFailureStatus_WhenMapperRegistered()
    {
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());
        serviceCollection.AddSingleton<IValidationStatusMapper, DefaultValidationStatusMapper>();
        serviceCollection
            .AddScoped<IJsonSchemaProvider<BenzeneMessageContext>>(x => new SimpleJsonSchemaProvider<BenzeneMessageContext>(Schema));

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline
            .UseJsonSchema()
            .UseMessageHandlers();

        var app = new BenzeneMessageApplication(pipeline.Build());

        var request = MessageBuilder.Create(StatusMappedTopic, new ExampleRequestPayload
        {
            Id = 42,
            Name = "foo-bar-foo-bar", // fails the schema's maxLength: 3 on "name"
            Mapped = "some-value"
        }).AsBenzeneMessage();

        var response = await app.HandleAsync(request, new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider()));

        Assert.Equal(BenzeneResultStatus.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UndecoratedHandler_KeepsValidationError_WhenMapperRegistered()
    {
        // Regression: a registered mapper must not change the outcome for a handler that carries
        // no [ValidationStatus] attribute - the mapper's own no-attribute fallback is ValidationError.
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());
        serviceCollection.AddSingleton<IValidationStatusMapper, DefaultValidationStatusMapper>();
        serviceCollection
            .AddScoped<IJsonSchemaProvider<BenzeneMessageContext>>(x => new SimpleJsonSchemaProvider<BenzeneMessageContext>(Schema));

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline
            .UseJsonSchema()
            .UseMessageHandlers();

        var app = new BenzeneMessageApplication(pipeline.Build());

        var request = MessageBuilder.Create(UndecoratedTopic, new ExampleRequestPayload
        {
            Id = 42,
            Name = "foo-bar-foo-bar", // fails the schema's maxLength: 3 on "name"
            Mapped = "some-value"
        }).AsBenzeneMessage();

        var response = await app.HandleAsync(request, new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider()));

        Assert.Equal(BenzeneResultStatus.ValidationError, response.StatusCode);
    }
}
