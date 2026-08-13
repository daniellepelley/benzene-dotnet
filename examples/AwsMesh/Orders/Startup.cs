using Benzene.Abstractions.Hosting;
using Benzene.Examples.AwsMesh.Orders.Clients.PaymentsCapture;
using Benzene.Examples.AwsMesh.Orders.Handlers;
using Benzene.Examples.AwsMesh.Orders.HealthChecks;
using Benzene.Examples.AwsMesh.Orders.Model;
using Benzene.Examples.AwsMesh.Shared;
using Benzene.HealthChecks.Core;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.AwsMesh.Orders;

/// <summary>
/// The orders-api Cloud Service, hosted as an AWS Lambda. Via the shared wiring it exposes the full
/// Cloud Service Profile over HTTP, answers the mesh's direct-invoke interrogation, and routes its
/// domain handlers over SQS, SNS and EventBridge too — every pipeline logged with correlation ids and
/// every payload FluentValidation-checked.
/// </summary>
public class Startup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        MeshServiceWiring.ConfigureServices(services, "orders", typeof(Startup).Assembly,
            // orders-api → payments-api: on create, send payments:capture to the payments SQS queue
            // (a point-to-point command — one consumer, must arrive). The payload type is the GENERATED
            // contract type, so the edge the mesh draws is declared with payments-api's own request shape
            // rather than a hand-copied mirror of it.
            OutboundSend.Sqs("payments:capture", typeof(CapturePayment), "PAYMENTS_QUEUE_URL"),
            // orders-api → inventory-api + notifications-api: publish order:placed to SNS, which fans it
            // out to every subscriber (a domain event, not a command).
            OutboundSend.Sns("order:placed", typeof(OutboundOrderPlaced), "ORDER_PLACED_TOPIC_ARN"),
            // Not a business edge — required purely because the generated payments client lists
            // benzene:healthcheck in its RequiredTopics unconditionally, and the outbound-routing start-up
            // check (Enforce by default) fails the host for any required topic with no route. Routed to the
            // same queue and NOT declared as an event, so it doesn't invent an orders → payments health
            // edge on the mesh graph. Nothing here calls HealthCheckAsync(); SQS is fire-and-forget so it
            // could not answer anyway. See the AwsMesh README — this should stop being necessary.
            OutboundSend.HealthCheck(OutboundTransport.Sqs, "PAYMENTS_QUEUE_URL"));

        // The generated client is a plain class over IBenzeneMessageSender, so it needs registering by
        // hand — the generator does not (yet) emit a DI extension. SCOPED to match IBenzeneMessageSender's
        // own lifetime (AddOutboundRouting registers it scoped); a singleton here would capture a scoped
        // dependency. See work/spec-mesh-tooling-implementation-plan.md's dogfooding findings.
        services.AddScoped<IPaymentsCaptureServiceClient, PaymentsCaptureServiceClient>();
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        IHealthCheck[] healthChecks =
        {
            new OrdersDatabaseHealthCheck(),
            new OrdersQueueHealthCheck(),
        };

        MeshServiceWiring.Configure(app, "orders",
            new[] { typeof(GetOrdersMessageHandler), typeof(CreateOrderMessageHandler) },
            healthChecks);
    }
}

/// <summary>AWS Lambda entry point hosting <see cref="Startup"/>, force-flushing OpenTelemetry per invocation.</summary>
public class Function : TracingLambdaHost<Startup>;
