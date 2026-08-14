using Amazon.DynamoDBv2;
using Benzene.Abstractions.Hosting;
using Benzene.Examples.AwsMesh.Payments.Handlers;
using Benzene.Examples.AwsMesh.Payments.HealthChecks;
using Benzene.Examples.AwsMesh.Payments.Model;
using Benzene.Examples.AwsMesh.Shared;
using Benzene.HealthChecks.Core;
using Benzene.Idempotency.DynamoDb;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.AwsMesh.Payments;

/// <summary>
/// The payments-api Cloud Service, hosted as an AWS Lambda. Via the shared wiring it exposes the full
/// Cloud Service Profile over HTTP, answers the mesh's direct-invoke interrogation, and routes its
/// domain handlers over SQS, SNS and EventBridge too — every pipeline logged and every payload
/// validated. Also the <b>consume side</b> of the outbox+idempotency pair
/// (<c>work/outbox-plan.md</c> §2.6): the <c>payments:capture</c> SQS ingress runs
/// <c>UseIdempotency()</c> (<c>enableSqsIdempotency</c>), deduping the redeliveries an at-least-once
/// outbox relay can produce (orders-api's outbox stamps its envelope id into the <c>idempotency-key</c>
/// header by default, matching this store's default key strategy with zero extra configuration).
/// </summary>
public class Startup : BenzeneStartUp
{
    /// <summary>Provisioned by <c>deploy/main.tf</c>; see <c>Benzene.Idempotency.DynamoDb/CLAUDE.md</c> for the table shape.</summary>
    private const string PaymentsIdempotencyTableNameEnvVar = "PAYMENTS_IDEMPOTENCY_TABLE_NAME";

    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        MeshServiceWiring.ConfigureServices(services, "payments", typeof(Startup).Assembly,
            // payments-api → shipping-api: on capture, send shipping:book to the shipping SQS queue
            // (a point-to-point command — one consumer, must arrive).
            OutboundSend.Sqs("shipping:book", typeof(OutboundShipmentBook), "SHIPPING_QUEUE_URL"),
            // payments-api → notifications-api + analytics-api: publish payment:captured to EventBridge,
            // routed to interested consumers by rule (an integration event).
            OutboundSend.EventBridge("payment:captured", typeof(OutboundPaymentCaptured), "EVENT_BUS_NAME"));

        // payments-api is the ONE service in this example that consumes outboxed traffic (orders-api's
        // payments:capture), so it's the one that pairs Benzene.Idempotency with it (see
        // MeshServiceWiring's enableSqsIdempotency for where UseIdempotency() actually gets mounted).
        services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
        var idempotencyTableName = Environment.GetEnvironmentVariable(PaymentsIdempotencyTableNameEnvVar) ?? "payments-idempotency";
        services.UsingBenzene(x => x.AddDynamoDbIdempotencyStore(idempotencyTableName));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        IHealthCheck[] healthChecks =
        {
            new PaymentsDatabaseHealthCheck(),
            new PaymentsGatewayHealthCheck(),
        };

        MeshServiceWiring.Configure(app, "payments",
            new[] { typeof(GetPaymentsMessageHandler), typeof(CapturePaymentMessageHandler) },
            healthChecks,
            enableSqsIdempotency: true);
    }
}

/// <summary>AWS Lambda entry point hosting <see cref="Startup"/>.</summary>
public class Function : TracingLambdaHost<Startup>;
