using Amazon.DynamoDBv2;
using Amazon.S3;
using Benzene.Abstractions.Hosting;
using Benzene.ClaimCheck.Aws.S3;
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
/// header by default, matching this store's default key strategy with zero extra configuration). Also
/// the <b>hydrate side</b> of the claim-check pair (<c>work/claim-check-plan.md</c> Phase 6): the same
/// ingress runs <c>UseClaimCheck&lt;SqsMessageContext&gt;()</c> (<c>enableClaimCheckHydration</c>),
/// resolving any <c>benzene-claim-check</c> reference orders-api's oversized sends carry back to the
/// real body before the handler runs.
/// </summary>
public class Startup : BenzeneStartUp
{
    /// <summary>Provisioned by <c>deploy/main.tf</c>; see <c>Benzene.Idempotency.DynamoDb/CLAUDE.md</c> for the table shape.</summary>
    private const string PaymentsIdempotencyTableNameEnvVar = "PAYMENTS_IDEMPOTENCY_TABLE_NAME";

    /// <summary>
    /// The S3 bucket <c>Benzene.ClaimCheck.Aws.S3</c> hydrates offloaded <c>payments:capture</c>
    /// receives from — the same bucket orders-api's <c>ClaimCheckBucketEnvVar</c> offloads to (see its
    /// Startup for why it's a dedicated bucket). See README "Claim-check: oversized payloads" and
    /// <c>work/claim-check-plan.md</c> Phase 6.
    /// </summary>
    private const string ClaimCheckBucketEnvVar = "CLAIM_CHECK_BUCKET";


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

        // payments-api is also the RECEIVING side of the claim-check dogfood (orders-api sends with
        // ClaimChecked: true) — registers a lazy IAmazonS3 client (same pattern as the IAmazonDynamoDB
        // client above) and the S3-backed IClaimCheckStore, pointed at the same bucket orders-api writes
        // to. Registered here, not inside the shared MeshServiceWiring, for the same reason the
        // idempotency registration above is: it keeps the other five services' DI untouched.
        var claimCheckBucket = Environment.GetEnvironmentVariable(ClaimCheckBucketEnvVar) ?? "benzene-mesh-claim-checks";
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
        services.UsingBenzene(x => x.AddS3ClaimCheckStore(claimCheckBucket));
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
            enableSqsIdempotency: true,
            enableClaimCheckHydration: true);
    }
}

/// <summary>AWS Lambda entry point hosting <see cref="Startup"/>.</summary>
public class Function : TracingLambdaHost<Startup>;
