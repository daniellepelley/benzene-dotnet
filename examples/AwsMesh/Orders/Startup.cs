using Amazon.DynamoDBv2;
using Amazon.S3;
using Benzene.Abstractions.Hosting;
using Benzene.ClaimCheck.Aws.S3;
using Benzene.Examples.AwsMesh.Orders.Clients;
using Benzene.Examples.AwsMesh.Orders.Clients.PaymentsCapture;
using Benzene.Examples.AwsMesh.Orders.Handlers;
using Benzene.Examples.AwsMesh.Orders.HealthChecks;
using Benzene.Examples.AwsMesh.Orders.Model;
using Benzene.Examples.AwsMesh.Shared;
using Benzene.HealthChecks.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.Outbox;
using Benzene.Outbox.DynamoDb;
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

    /// <summary>
    /// The DynamoDB table names the outbox reads/writes — provisioned by <c>deploy/main.tf</c>
    /// (<c>orders</c>, <c>orders-outbox</c>), handed in as env vars so this project never hard-codes an
    /// account/region-specific name.
    /// </summary>
    private const string OrdersOutboxTableNameEnvVar = "ORDERS_OUTBOX_TABLE_NAME";

    /// <summary>
    /// The S3 bucket <c>Benzene.ClaimCheck.Aws.S3</c> offloads oversized <c>payments:capture</c> sends
    /// to — provisioned by <c>deploy/main.tf</c> (<c>aws_s3_bucket.claim_checks</c>, a DEDICATED bucket,
    /// separate from <c>aws_s3_bucket.artifacts</c> — see its comment for why). See README "Claim-check:
    /// oversized payloads" and <c>work/archive/claim-check-plan-2026-08.md</c> Phase 6.
    /// </summary>
    private const string ClaimCheckBucketEnvVar = "CLAIM_CHECK_BUCKET";

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        MeshServiceWiring.ConfigureServices(services, "orders", typeof(Startup).Assembly,
            // orders-api → payments-api: on create, send payments:capture to the payments SQS queue (a
            // point-to-point command — one consumer, must arrive). The payload type is the GENERATED
            // contract type, so the edge the mesh draws is declared with payments-api's own request shape
            // rather than a hand-copied mirror of it. Outboxed=true: this is the outbox dogfood — see
            // Handlers/OrderHandlers.cs and work/archive/outbox-plan-2026-08.md's Phase 3. ClaimChecked=true: this is
            // ALSO the claim-check dogfood (work/archive/claim-check-plan-2026-08.md Phase 6) — a caller that attaches a
            // large SupportingDocument (see Handlers/OrderHandlers.cs) pushes this send's serialized body
            // over Benzene.ClaimCheck's default 192 KiB threshold, and UseClaimCheck() (wired below
            // OutboundSend.ClaimChecked in MeshServiceWiring, AFTER UseOutbox()) offloads it to the
            // dedicated claim-checks S3 bucket instead of inlining it on the SQS message.
            OutboundSend.Sqs("payments:capture", typeof(CapturePayment), "PAYMENTS_QUEUE_URL", outboxed: true, claimChecked: true),
            // orders-api → inventory-api + notifications-api: publish order:placed to SNS, which fans it
            // out to every subscriber (a domain event, not a command). Also outboxed, atomically with the
            // send above and the order row (see CreateOrderMessageHandler). Not claim-checked: it never
            // carries the oversized demo field (see Handlers/OrderHandlers.cs).
            OutboundSend.Sns("order:placed", typeof(OutboundOrderPlaced), "ORDER_PLACED_TOPIC_ARN", outboxed: true));

        // The GENERATED DI registration, not a hand-written one: `benzene build -output topic-client`
        // now emits AddPaymentsClients() (plus a per-topic AddPaymentsCaptureServiceClient()) beside the
        // client itself, registering it SCOPED — the lifetime IBenzeneMessageSender is registered with,
        // so the client can never become a captive dependency. It extends IBenzeneServiceContainer,
        // Benzene's own container abstraction, so it works whatever container is underneath rather than
        // assuming Microsoft's. See work/archive/spec-mesh-tooling-implementation-plan-2026-08.md's finding 7c.
        // (Not used by CreateOrderMessageHandler any more — an outboxed/SQS route is fire-and-forget
        // only, and this generated client's CapturePaymentsAsync asks for a typed PaymentDto response,
        // which no such route can ever produce. Left registered/generated as the from-source codegen
        // dogfood this project otherwise exists to exercise; see the csproj's <BenzeneServiceContract>.)
        services.UsingBenzene(x => x.AddPaymentsClients());

        // orders-api is the ONE service in this example that opts into the outbox (via Outboxed: true
        // above) — Transactional so the order row and both captured envelopes commit in a single
        // DynamoDB TransactWriteItems (CreateOrderMessageHandler). Registering AddOutbox/
        // AddDynamoDbOutboxStore/AddDynamoDbOutboxTransaction here, rather than inside the shared
        // MeshServiceWiring, keeps the other five services' DI untouched.
        services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
        var outboxTableName = Environment.GetEnvironmentVariable(OrdersOutboxTableNameEnvVar) ?? "orders-outbox";
        services.UsingBenzene(x => x
            .AddOutbox(o => o.WriteMode = OutboxWriteMode.Transactional)
            .AddDynamoDbOutboxStore(outboxTableName)
            .AddDynamoDbOutboxTransaction(outboxTableName));

        // orders-api is the SENDING side of the claim-check dogfood (via ClaimChecked: true above) —
        // registers a lazy IAmazonS3 client (same pattern as the IAmazonDynamoDB client above) and the
        // S3-backed IClaimCheckStore. Registered here, not inside the shared MeshServiceWiring, for the
        // same reason the outbox registrations above are: it keeps the other five services' DI untouched.
        var claimCheckBucket = Environment.GetEnvironmentVariable(ClaimCheckBucketEnvVar) ?? "benzene-mesh-claim-checks";
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
        services.UsingBenzene(x => x.AddS3ClaimCheckStore(claimCheckBucket));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        IHealthCheck[] healthChecks =
        {
            new OrdersDatabaseHealthCheck(),
            new OrdersQueueHealthCheck(),
        };

        MeshServiceWiring.Configure(app, "orders",
            new[]
            {
                typeof(GetOrdersMessageHandler),
                typeof(CreateOrderMessageHandler),
                // The Lambda relay pair (work/archive/outbox-plan-2026-08.md §2.5): streams dispatch (near-real-time) +
                // scheduled sweep (retry/park/cleanup backstop). See Handlers/OutboxHandlers.cs.
                typeof(OutboxStreamDispatchMessageHandler),
                typeof(OutboxSweepMessageHandler),
            },
            healthChecks,
            enableOutboxDispatchStream: true);
    }
}

/// <summary>AWS Lambda entry point hosting <see cref="Startup"/>, force-flushing OpenTelemetry per invocation.</summary>
public class Function : TracingLambdaHost<Startup>;
