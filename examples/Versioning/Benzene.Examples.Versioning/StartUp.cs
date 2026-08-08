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
///    for that topic, so the casting pipeline passes it straight through.
///
///  * Mechanism B - transparent payload casting: the topic <c>inventory:adjust</c> has three payload
///    versions and ONE handler (<see cref="Handlers.AdjustInventoryMessageHandler"/>, on V3), wired with a
///    single <see cref="PayloadVersioningExtensions.AddPayloadVersioning"/> call. Only the adjacent UPcasts
///    V1-&gt;V2 and V2-&gt;V3 are declared; the framework composes the missing V1-&gt;V3 pair by CHAINING, and
///    synthesises the V3-&gt;V2-&gt;V1 downcasts - so a V1 producer is upcast V1-&gt;V2-&gt;V3 to reach the handler and
///    the response is downcast back, with no direct V1&lt;-&gt;V3 caster and no hand-written downcasters.
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
            x.SetApplicationInfo("Benzene Versioning Example", "1.0.0",
                    "Dogfoods handler-version dispatch and transparent payload casting over AWS Lambda transports.")
                .AddBenzeneMessage()
                // Scan THIS assembly for the [Message]/[HttpEndpoint] handlers. AddHttpMessageHandlers
                // registers the HTTP-endpoint routes so inventory:adjust is reachable at POST
                // /inventory/adjust over API Gateway.
                .AddMessageHandlers(typeof(StartUp).Assembly)
                .AddHttpMessageHandlers()
                .AddContextItems();

            AddInventoryVersioning(x);
        });
    }

    /// <summary>
    /// Mechanism B, in one call. Declaring only the adjacent UPcasts (V1-&gt;V2, V2-&gt;V3) is the whole point:
    /// the framework composes the missing V1-&gt;V3 pair by chaining, and synthesises the reverse
    /// V3-&gt;V2-&gt;V1 downcasts (each a field-drop). Each newer version's added field is seeded by its upcaster,
    /// which is how a plain V1 request proves, from its downcast V1 response, that both hops ran. The caster
    /// graph is validated here, at startup - a missing path throws now, not on the first message.
    ///
    /// Enabling casting for the four transports via <c>ForContext</c> happens here in
    /// <c>ConfigureServices</c>: it is order-independent because the transports register their default
    /// request mapper with <c>TryAdd</c>, so these decorators win regardless of when the transport is wired.
    /// </summary>
    private static void AddInventoryVersioning(IBenzeneServiceContainer x)
    {
        x.AddPayloadVersioning(versioning => versioning
            .ForContext<BenzeneMessageContext>()
            .ForContext<ApiGatewayContext>()
            .ForContext<SqsMessageContext>()
            .ForContext<SnsRecordContext>()
            .Topic(Topics.InventoryAdjust, topic => topic
                .Version<InvV1.InventoryAdjustment>(Versions.V1)
                .Version<InvV2.InventoryAdjustment>(Versions.V2)
                .Version<InvV3.InventoryAdjustment>(Versions.V3)
                .Upcast<InvV1.InventoryAdjustment, InvV2.InventoryAdjustment>(
                    f => f.RegisterInitValue(o => o.WarehouseId, "wh-main"))
                .Upcast<InvV2.InventoryAdjustment, InvV3.InventoryAdjustment>(
                    f => f.RegisterInitValue(o => o.Reason, "unspecified"))));
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
    }
}

/// <summary>
/// AWS Lambda entry point hosting <see cref="StartUp"/>. Point the function-handler setting at
/// <c>Benzene.Examples.Versioning::Benzene.Examples.Versioning.Function::FunctionHandlerAsync</c>.
/// </summary>
public class Function : AwsLambdaHost<StartUp>;
