using Benzene.Abstractions.Hosting;
using Benzene.Examples.AwsMesh.Orders.Clients;
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
            OutboundSend.Sns("order:placed", typeof(OutboundOrderPlaced), "ORDER_PLACED_TOPIC_ARN"));

        // The GENERATED DI registration, not a hand-written one: `benzene build -output topic-client`
        // now emits AddPaymentsClients() (plus a per-topic AddPaymentsCaptureServiceClient()) beside the
        // client itself, registering it SCOPED — the lifetime IBenzeneMessageSender is registered with,
        // so the client can never become a captive dependency. It extends IBenzeneServiceContainer,
        // Benzene's own container abstraction, so it works whatever container is underneath rather than
        // assuming Microsoft's. See work/spec-mesh-tooling-implementation-plan.md's finding 7c.
        services.UsingBenzene(x => x.AddPaymentsClients());
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
