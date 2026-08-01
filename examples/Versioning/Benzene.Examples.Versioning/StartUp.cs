using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.BenzeneMessage;
using Benzene.Aws.Lambda.Sns;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Versioning;
using Benzene.Core.Versioning.Schemas;
using Benzene.Examples.Versioning.Services;
using Benzene.Http;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using InvV1 = Benzene.Examples.Versioning.Contracts.Inventory.V1;
using InvV2 = Benzene.Examples.Versioning.Contracts.Inventory.V2;
using InvV3 = Benzene.Examples.Versioning.Contracts.Inventory.V3;

namespace Benzene.Examples.Versioning;

/// <summary>
/// A payload-versioning demo, hosted as one AWS Lambda over four transports (the BenzeneMessage envelope,
/// API Gateway, SQS and SNS). It dogfoods BOTH versioning axes of docs/specification/versioning.md:
///
///  * Mechanism A - handler-version dispatch: the topic <c>order:create</c> has two handlers
///    (<see cref="Handlers.CreateOrderV1MessageHandler"/> / <see cref="Handlers.CreateOrderV2MessageHandler"/>),
///    one per version; the incoming <c>benzene-version</c> selects which runs. No casters are registered
///    for that topic, so the casting pipeline below passes it straight through.
///
///  * Mechanism B - transparent payload casting: the topic <c>inventory:adjust</c> has three payload
///    versions and ONE handler (<see cref="Handlers.AdjustInventoryMessageHandler"/>, on V3). Only the
///    adjacent casters V1&lt;-&gt;V2 and V2&lt;-&gt;V3 are registered, so a V1 producer is upcast by CHAINING
///    V1-&gt;V2-&gt;V3 (and the V3 response downcast V3-&gt;V2-&gt;V1) - there is deliberately no direct V1&lt;-&gt;V3 caster.
/// </summary>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
    {
        return new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration);
        services.AddLogging();

        // The stand-in "data store" the handlers record to, so a fire-and-forget SQS/SNS test can still
        // observe which version handler ran / what the casting pipeline delivered.
        services.AddSingleton<IProcessedLog, InMemoryProcessedLog>();

        services.UsingBenzene(x =>
        {
            x.AddBenzene()
                .SetApplicationInfo("Benzene Versioning Example", "1.0.0",
                    "Dogfoods handler-version dispatch and transparent payload casting over AWS Lambda transports.")
                .AddBenzeneMessage()
                // Scan THIS assembly for the [Message]/[HttpEndpoint] handlers. AddHttpMessageHandlers
                // registers the HTTP-endpoint routes so inventory:adjust is reachable at POST
                // /inventory/adjust over API Gateway.
                .AddMessageHandlers(typeof(StartUp).Assembly)
                .AddHttpMessageHandlers()
                // Register the framework-default request/response mappers the casting decorators wrap.
                .AddContextItems();

            RegisterInventoryCasters(x);
        });
    }

    /// <summary>
    /// Mechanism B caster definitions for <c>inventory:adjust</c>. Registering ONLY the adjacent hops
    /// (V1&lt;-&gt;V2 and V2&lt;-&gt;V3) is the whole point: <see cref="SchemaCastDefinitionsExpander"/> composes the
    /// missing V1&lt;-&gt;V3 pairs by chaining, so a V1 request travels V1-&gt;V2-&gt;V3 to reach the single V3 handler
    /// and its response travels V3-&gt;V2-&gt;V1 back. Each newer version's added field is seeded by the
    /// upcaster, which is how a plain V1 request can prove, from its downcast V1 response, that both hops
    /// ran. Enabling the decorators that USE these casters happens in <see cref="Configure"/>, after the
    /// transports are wired - see there for why.
    /// </summary>
    private static void RegisterInventoryCasters(IBenzeneServiceContainer x)
    {
        x.RegisterSchemaCastDefinitions(builder => builder
                // Upcasts (request path): seed the field each version introduced.
                .Add<InvV1.InventoryAdjustment, InvV2.InventoryAdjustment>(
                    Topics.InventoryAdjust, Versions.V1, Versions.V2,
                    f => f.RegisterInitValue(o => o.WarehouseId, "wh-main"))
                .Add<InvV2.InventoryAdjustment, InvV3.InventoryAdjustment>(
                    Topics.InventoryAdjust, Versions.V2, Versions.V3,
                    f => f.RegisterInitValue(o => o.Reason, "unspecified"))
                // Downcasts (response path): drop the field the older version never had. Auto property-map.
                .Add<InvV3.InventoryAdjustment, InvV2.InventoryAdjustment>(
                    Topics.InventoryAdjust, Versions.V3, Versions.V2)
                .Add<InvV2.InventoryAdjustment, InvV1.InventoryAdjustment>(
                    Topics.InventoryAdjust, Versions.V2, Versions.V1))
            .RegisterPayloadSchemaVersions(new[]
            {
                new PayloadSchemaVersions
                {
                    Topic = Topics.InventoryAdjust,
                    // Every live version may arrive, and a response may be requested in any of them - the
                    // expander composes every needed (from,to) pair from the four adjacent casters above.
                    FromSchemas = new[] { Versions.V1, Versions.V2, Versions.V3 },
                    ToSchemas = new[] { Versions.V1, Versions.V2, Versions.V3 }
                }
            });
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        app.UseAwsLambda(aws =>
        {
            // The BenzeneMessage envelope pipeline, reused directly and mounted on API Gateway as the
            // /benzene-message endpoint.
            var benzeneMessagePipeline = aws.Create<BenzeneMessageContext>()
                .UseMessageHandlers(_ => { });

            aws.UseBenzeneMessage(benzeneMessagePipeline);

            // API Gateway routes the [HttpEndpoint("POST","/inventory/adjust")] handler; the version
            // rides in the benzene-version request header, read by the ApiGateway version getter.
            aws.UseApiGateway(api => api
                .UseMessageHandlers(_ => { }));

            aws.UseSqs(sqs => sqs
                .UseMessageHandlers(_ => { }));

            aws.UseSns(sns => sns
                .UseMessageHandlers(_ => { }));
        });

        // Enable transparent payload casting for every transport - AFTER UseAwsLambda has wired them.
        // Ordering matters: each AWS transport's own registration (AddApiGateway/AddSqs/AddSns) registers
        // its IRequestMapper<TContext> with a last-wins AddScoped, so if the casting decorators were
        // registered earlier (in ConfigureServices) the transport would overwrite them and the request
        // upcast would silently not run. Registering them here, after the transports, makes the decorators
        // the final (winning) registration for each context. A topic with no casters (order:create) is
        // unaffected - the decorators pass it straight through.
        app.Register(container => container
            .UsePayloadVersionCasting<BenzeneMessageContext>()
            .UsePayloadVersionCasting<ApiGatewayContext>()
            .UsePayloadVersionCasting<SqsMessageContext>()
            .UsePayloadVersionCasting<SnsRecordContext>());
    }
}

/// <summary>
/// AWS Lambda entry point hosting <see cref="StartUp"/>. Point the function-handler setting at
/// <c>Benzene.Examples.Versioning::Benzene.Examples.Versioning.Function::FunctionHandlerAsync</c>.
/// </summary>
public class Function : AwsLambdaHost<StartUp>;
