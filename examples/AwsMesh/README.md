# AWS Mesh Self-Discovery — end-to-end example

A deployable AWS example that proves the Benzene mesh **self-discovery** story end to end: **six**
Benzene Cloud Services running as Lambdas that call each other over **SQS, SNS and EventBridge**, plus
a **mesh service** (a seventh Lambda) that discovers them by tag, interrogates each, and serves the
Mesh UI — all fronted by API Gateway so you can open the UIs in a browser.

The six services dogfood Benzene's transports by using **each one for what it's actually good at**
(commands vs fan-out events vs routed integration events), so the Mesh UI's topology renders a real,
non-trivial graph. See `work/aws-mesh-multi-transport-plan.md` for the plan and
`work/mesh-self-discovery-design.md` for the discovery design this example exercises.

## Architecture

```
                          API Gateway (one HTTP API per Lambda, public)  +  direct Lambda-invoke (mesh interrogation)

  ┌─────────┐  SQS  payments:capture  ┌──────────┐  SQS  shipping:book  ┌──────────┐
  │ orders  │ ───────────────────────►│ payments │ ───────────────────► │ shipping │
  └────┬────┘                         └────┬─────┘                      └────┬─────┘
       │ SNS  order:placed (fan-out)       │ EventBridge  payment:captured   │ EventBridge  shipping:dispatched
       ▼               ▼                   ▼               ▼                  ▼          ▼          ▼
 ┌───────────┐  ┌───────────────┐   ┌───────────────┐ ┌───────────┐   inventory  notifications  analytics
 │ inventory │  │ notifications │   │ notifications │ │ analytics │
 └───────────┘  └───────────────┘   └───────────────┘ └───────────┘
   reserve         notify              notify            metrics
                                                                          ← 6 Cloud Service Lambdas (tag: benzene)
                          │  mesh (7th Lambda, untagged):
                          │  1. ListFunctions + ListTags  (discover benzene-tagged Lambdas)
                          │  2. Invoke each ({topic:'spec'|'healthcheck'})  (interrogate)
                          ▼
                    ┌───────────┐
                    │    S3     │   registry.json  (discovered config)
                    │  bucket   │   manifest.json / services/*.json / topology.json  (catalog)
                    └───────────┘
                          ▲
                          │  Mesh UI reads the catalog artifacts
```

- The six **service Lambdas** are full Cloud Service Profile (R1–R8) services: `/benzene/invoke`,
  `/benzene/spec`, `/benzene/health`, `/benzene/spec-ui`, plus any domain routes — and they answer
  the mesh's **direct Lambda-Invoke** interrogation (`spec`/`healthcheck` topics) with no HTTP surface
  required. They carry a `benzene` resource tag so discovery finds them.
- The **mesh Lambda** runs, on an EventBridge schedule: discovery (`AwsLambdaDiscoveryProvider` →
  `ListFunctions`+`ListTags`, filtered by the `benzene` tag) → writes `registry.json` to S3 → the
  aggregator interrogates each discovered Lambda by Invoke → writes the catalog artifacts to S3. Its
  HTTP surface serves the **Mesh UI** (reading those artifacts).

## Projects

| Project | What it is | Sends | Consumes |
|---|---|---|---|
| `Orders/` (`…AwsMesh.Orders`) | orders-api Cloud Service Lambda. **Outboxed**: `payments:capture` + `order:placed` are captured by `Benzene.Outbox.DynamoDb` and committed atomically with the order row — see "The outbox" below. **Claim-checked**: an oversized `payments:capture` send is offloaded to S3 via `Benzene.ClaimCheck.Aws.S3` — see "Claim-check: oversized payloads" below. | `payments:capture` (SQS, outboxed, claim-checked), `order:placed` (SNS, outboxed) | `orders-outbox:INSERT` (DynamoDB Streams), `orders:outbox-sweep` (EventBridge schedule) |
| `Payments/` (`…AwsMesh.Payments`) | payments-api Cloud Service Lambda. **Idempotent**: `payments:capture` runs `Benzene.Idempotency.DynamoDb`, deduping the outbox's at-least-once redeliveries — see "The outbox" below. **Claim-check hydrating**: the same ingress resolves any `benzene-claim-check` reference back to the real body — see "Claim-check: oversized payloads" below. | `shipping:book` (SQS), `payment:captured` (EventBridge) | `payments:capture` |
| `Shipping/` (`…AwsMesh.Shipping`) | shipping-api Cloud Service Lambda | `shipping:dispatched` (EventBridge) | `shipping:book` |
| `Inventory/` (`…AwsMesh.Inventory`) | inventory-api Cloud Service Lambda | — | `order:placed` (SNS), `shipping:dispatched` (EventBridge) |
| `Notifications/` (`…AwsMesh.Notifications`) | notifications-api Cloud Service Lambda | — | `order:placed` (SNS), `payment:captured` + `shipping:dispatched` (EventBridge) |
| `Analytics/` (`…AwsMesh.Analytics`) | analytics-api Cloud Service Lambda | — | `payment:captured` + `shipping:dispatched` (EventBridge) |
| `Mesh/` (`…AwsMesh.Mesh`) | the discovery + aggregator + UI Lambda (uses `Benzene.Mesh.Aws.S3`) | — | — |
| `deploy/` | Terraform: 7 Lambdas, IAM, S3, one HTTP API per Lambda, SQS queues, an SNS topic, a custom EventBridge bus + rules, the aggregation schedule, the `orders`/`orders-outbox`/`payments-idempotency` DynamoDB tables, the outbox stream's event-source mapping, the outbox sweep schedule, and the dedicated `claim_checks` S3 bucket + lifecycle rule | | |
| `.github/workflows/mesh-example-aws-deploy.yml` | GitHub Actions: build all 7 Lambdas + `terraform apply` | | |

Only `orders-api` and `payments-api` opt into the outbox/idempotency/claim-check trio — the other four
services (and the mesh) are unaffected; `Shared/MeshServiceWiring` only wires any of them when a service
explicitly asks for it (`OutboundSend(..., outboxed: true, claimChecked: true)` per route,
`enableOutboxDispatchStream`/`enableSqsIdempotency`/`enableClaimCheckHydration` on
`MeshServiceWiring.Configure`).

Each service Lambda is a **self-contained executable** hosting the Benzene pipeline via an
`Amazon.Lambda.RuntimeSupport` bootstrap — because .NET 10 has no managed Lambda runtime, they deploy
on the **`provided.al2023`** custom runtime (self-contained publish).

## OpenTelemetry (traces + metrics)

Every Lambda (the six services and the mesh) wires **full OpenTelemetry**: Benzene's instrumentation
(`AddBenzeneInstrumentation`) for traces and metrics, exported over **OTLP**, plus the pipeline
middleware `UseW3CTraceContext` → `UseBenzeneEnrichment` → `UseBenzeneMetrics` on every transport. The
W3C trace-context propagation is what stitches the **order → payment → shipment** spans (across the SQS
hops) into a single distributed trace — feed it to Grafana Tempo and the mesh's Topology can show
*observed* edges on top of the structural ones.

Two things are different from a typical Generic-Host app, because a bare AWS Lambda host has no `IHost`
(see `Shared/LambdaTelemetry.cs`):

- **The providers are built eagerly.** `services.AddOpenTelemetry()` only *constructs* the
  `TracerProvider`/`MeterProvider` from a hosted service that never runs under a Lambda host — so the
  `"Benzene"` `ActivitySource` would get no listener and **no middleware spans would ever be recorded**.
  `LambdaTelemetry.Configure` builds them with `Sdk.Create*ProviderBuilder().Build()` at startup instead,
  which attaches the listener immediately.
- **Spans are force-flushed per invocation.** `TracingLambdaHost` (the `AwsLambdaHost` subclass every
  `Function` uses) overrides `OnInvocationCompleteAsync` to `ForceFlush` the batched exporters before the
  execution environment freezes, so the current invocation's spans aren't delayed to the next invocation
  or dropped on scale-in.

**X-Ray active tracing** (`tracing_config { mode = "Active" }`) is turned on for every function
automatically — but note it only captures the **AWS-level** segments (the `AWS::Lambda::Function`
segments and their `Overhead` subsegments). Benzene's **per-middleware** spans are OpenTelemetry spans
that leave the process over **OTLP**, a separate pipe that needs a collector to reach X-Ray.

To bridge them, **`var.adot_collector_layer_arn`** (defaulted to the eu-west-1 amd64 ADOT collector
layer) attaches the collector to every function and points `OTEL_EXPORTER_OTLP_ENDPOINT` at its
in-process receiver (`http://localhost:4317`). The layer's *default* config is **metrics-only** (it
drops traces), so the Terraform also sets `OPENTELEMETRY_COLLECTOR_CONFIG_URI=/var/task/collector.yaml`
to select the [`collector.yaml`](collector.yaml) shipped in each Lambda zip, which adds the
`traces → awsxray` pipeline. (No `AWS_LAMBDA_EXEC_WRAPPER` is set: these custom-runtime functions
already emit their own spans, so only the collector half of the layer is used.) `var.otlp_endpoint` is
an escape hatch for pointing at an out-of-process collector instead. With neither set, spans are
recorded but exported nowhere.

### End-to-end cross-service traces (one X-Ray trace per transaction)
The point of the OTel path here is that **a whole transaction — `order:create` → (EventBridge/SNS/SQS) →
the next service's topic → …  — shows up as ONE trace** you can read end to end, in X-Ray and in the Mesh
UI's flow waterfall. Two things make that work, and both are wired in this example:

1. **The trace context is propagated on every outbound send.** `MeshServiceWiring`'s outbound routes run
   `.UseW3CTraceContext().UseCorrelationId()` before the transport, so the current activity's `traceparent`
   (and `x-correlation-id`) ride the message — as an SQS/SNS message attribute, or embedded in the
   EventBridge `Detail`. The receiving service's inbound `UseW3CTraceContext()` (in `Observe()`) then
   **continues the same trace** instead of starting a fresh one. Without the *outbound* half, each service
   began its own trace and you saw disconnected single-service flows.
2. **The root trace id is X-Ray-compatible.** `LambdaTelemetry` adds OpenTelemetry's `AddXRayTraceId()`
   (the `OpenTelemetry.Extensions.AWS` id generator), so the first service mints an X-Ray-format
   (epoch-prefixed) id; downstream services inherit it via the propagated `traceparent`. Every hop then
   lands under **one** X-Ray trace id, which the ADOT collector's `traces → awsxray` pipeline exports as a
   single X-Ray trace. (The default random OTel id maps to an out-of-range X-Ray timestamp, which X-Ray
   drops — which is why cross-service traces didn't line up before.)

**One tracing path on purpose (OTel, not the X-Ray SDK).** This example runs the OTel path *alone*
(`AddDiagnostics` → OTLP → the ADOT collector → X-Ray) and does **not** also wire
`Benzene.Aws.Lambda.XRay`'s `AddXRayTracing()`. That X-Ray-SDK path nests middleware subsegments under the
Lambda's own segment via `_X_AMZN_TRACE_ID`, which only propagates through AWS's `AWSTraceHeader` — and
Benzene doesn't inject that on an outbound send, so it can **only** stitch within a single Lambda
invocation. Running it alongside OTel would add a second, per-hop-rooted representation that muddies the
cross-service view, so we pick the one path that stitches end to end. (`AddXRayTracing()` remains a valid
library option when you want the classic in-segment X-Ray breakdown for a *single* Lambda with no
collector — it's just not what an end-to-end mesh wants.)

### Topic usage → the Mesh UI (metrics, not just traces)
The same `UseBenzeneMetrics()` on every pipeline emits the `benzene.messages.processed` counter tagged
`topic`/`transport`/`result`. The collector's `metrics` pipeline exports it to **CloudWatch** via the
`awsemf` (Embedded Metric Format) exporter (`collector.yaml`) into the `Benzene/Mesh` namespace, dimensions
`[topic, transport, result]`, in the `/benzene/mesh/usage` log group. The counter is exported with **delta**
temporality (`LambdaTelemetry`) so a CloudWatch `Sum` over a window equals the request count.

The mesh Lambda then reads it back: `AddCloudWatchUsage(...)` registers
`Benzene.Mesh.Usage.CloudWatch`'s `IMeshUsageSource`, which the aggregator pulls each run to write
`usage.json` — per-topic request counts over `var.usage_window_hours` (default 24h). The **Mesh UI** renders
those as a Usage column on the estate topic table plus per-topic by-transport / by-status breakdowns and the
window. IAM: the service role gains CloudWatch Logs perms on the usage group (`service_emf`), and the mesh
role gains `cloudwatch:GetMetricData`/`ListMetrics` (in its policy). This is deliberately coarse — request
counts over a window; fine-grained analysis stays in CloudWatch/Grafana. (Per-service attribution and
duration are documented follow-ups — the counter isn't tagged by service, so `usage.json` reports that
dimension as absent, which the UI surfaces honestly rather than guessing.)

#### Generate traffic with the Lambda test tool
There's nothing to show until services actually handle messages. The quickest way to create some is the
**Test** tab on a service Lambda in the console (or `aws lambda invoke`). On a direct invoke the services
accept the **Benzene message envelope** `{ "topic", "headers", "body" }` — note `body` is a *string* holding
the message JSON (escaped quotes), and it flows through the same metered pipeline. Which Lambda handles
which topic: `orders:*` → orders, `payments:*` → payments, `shipping:*` → shipping; the events `order:placed`
→ inventory/notifications, `payment:captured` → notifications/analytics, `shipping:dispatched` →
inventory/notifications/analytics.

**Best starting point — `orders:create` on the `orders` Lambda.** Because the queues/topics/bus are wired,
the handler's downstream sends fire for real, so one invoke fans out `payments:capture` (SQS) →
`shipping:book` (SQS) → `shipping:dispatched` (EventBridge) → the consumers — giving traffic across many
topics and the **sqs/sns/eventbridge** transports, not just the invoke path. Fire it a dozen times:

```json
{ "topic": "orders:create", "headers": {}, "body": "{\"item\":\"Espresso Machine\",\"quantity\":2}" }
```

Per-topic payloads to hit any service directly (these count under the *invoke* transport):

```json
{ "topic": "payments:capture",    "headers": {}, "body": "{\"orderId\":\"ord-1\",\"amount\":20,\"currency\":\"GBP\"}" }
{ "topic": "shipping:book",       "headers": {}, "body": "{\"orderId\":\"ord-1\",\"carrier\":\"DPD\"}" }
{ "topic": "order:placed",        "headers": {}, "body": "{\"orderId\":\"ord-1\",\"item\":\"Espresso Machine\",\"quantity\":2,\"amount\":20,\"currency\":\"GBP\"}" }
{ "topic": "payment:captured",    "headers": {}, "body": "{\"orderId\":\"ord-1\",\"amount\":20,\"currency\":\"GBP\"}" }
{ "topic": "shipping:dispatched", "headers": {}, "body": "{\"orderId\":\"ord-1\",\"shipmentId\":\"shp-1\",\"carrier\":\"DPD\",\"trackingNumber\":\"DPD-123\"}" }
{ "topic": "orders:get-all",      "headers": {}, "body": "" }
```

Or from the CLI (`--cli-binary-format raw-in-base64-out` lets AWS CLI v2 take a raw JSON payload):

```bash
aws lambda invoke --function-name <orders-fn-name> --cli-binary-format raw-in-base64-out \
  --payload '{"topic":"orders:create","headers":{},"body":"{\"item\":\"Espresso Machine\",\"quantity\":2}"}' /dev/stdout
```

Notes: the counter is recorded around the whole pipeline, so **every** invoke produces a datapoint — a
payload that fails validation just lands as `result=failure`. Validation to respect: `orders:create` needs a
non-empty item and quantity 1–1000; `payments:capture` a 3-char currency and amount > 0; `shipping:book` a
carrier in {DPD, RoyalMail, UPS, FedEx}. After firing a batch, give the metric ~1–2 min to reach CloudWatch,
`POST /mesh/refresh` to aggregate now (instead of waiting for the schedule), then check `usage.json` / the
Mesh UI Usage column.

## What each service shows off

Every service is wired through the shared `Shared/MeshServiceWiring` helper, which "goes to town" on
Benzene's features so the example dogfoods them on a real deploy:

- **One set of handlers, five transports.** Each domain handler is reachable over **API Gateway**
  (HTTP), **direct Lambda invoke** (BenzeneMessage), **SQS**, **SNS**, and **EventBridge** — the same
  handler, no per-transport code. Fire any of them from the **Lambda test tool**: each service ships
  saved requests under `.lambda-test-tool/SavedRequests/` (e.g. `orders-create-sqs.json`,
  `orders-create-eventbridge.json`, `orders-create-direct.json`, `orders-create-apigateway.json`).
- **Tracing/logging across every pipeline.** Every transport pipeline is wrapped with
  `UseLogResult` + a **correlation id**, emitting a structured JSON log line per invocation (request,
  response, `processTime`) to stdout → **CloudWatch**.
- **Validation everywhere.** Each domain request has a **FluentValidation** validator applied via
  `router.UseFluentValidation()`, so an invalid payload is rejected identically no matter which
  transport it arrived on.

## Interconnectivity → topology — one transport per job

The six services form a live fulfilment flow, and **each transport is used for what it's good at** —
that's the whole point of the dogfood, and what makes the Mesh UI topology worth looking at:

| Transport | Idiomatic for | In this example |
|---|---|---|
| **SQS** | a point-to-point **command** — one consumer, must arrive, retry/DLQ | `orders → payments` (`payments:capture`), `payments → shipping` (`shipping:book`) |
| **SNS** | a **fan-out event** — one publisher, many subscribers | `orders` publishes `order:placed` → **inventory _and_ notifications** |
| **EventBridge** | **routed integration events** — content rules, one event → many targets | `payments` publishes `payment:captured`, `shipping` publishes `shipping:dispatched` → routed to **notifications / inventory / analytics** |

The flow:

- `orders-api`, on `orders:create`: **sends** `payments:capture` (SQS command) **and** publishes
  `order:placed` (SNS event).
- `payments-api`, on `payments:capture`: **sends** `shipping:book` (SQS command) **and** publishes
  `payment:captured` (EventBridge event).
- `shipping-api`, on `shipping:book`: books the shipment and publishes `shipping:dispatched`
  (EventBridge event).
- `inventory-api` reserves stock on `order:placed` (SNS) and decrements it on `shipping:dispatched`
  (EventBridge) — one service consuming from **two** event transports.
- `notifications-api` notifies the customer on `order:placed` (SNS) + `payment:captured` +
  `shipping:dispatched` (EventBridge).
- `analytics-api` records metrics on `payment:captured` + `shipping:dispatched` (EventBridge).

Every hop goes through the same Benzene `IBenzeneMessageSender` (`AddOutboundRouting` → `UseSqs` /
`UseSns` / `UseEventBridge`), and the receiving side is just the matching Benzene ingress the shared
wiring already registers (`aws.UseSqs` / `aws.UseSns` / `aws.UseEventBridge`) — the **same handler**, no
per-transport code. The choice of transport per send lives entirely in each service's `Startup`
(`OutboundSend.Sqs/Sns/EventBridge(...)`). Terraform provisions the two SQS queues + event-source
mappings, the SNS topic + Lambda subscriptions, and a **custom EventBridge bus** + rules + targets, plus
the send/publish IAM. Most sends are best-effort — a downstream hiccup never fails the upstream call
(and locally, with no target wired, they just log) — **except** orders-api's two sends
(`payments:capture`, `order:placed`), which are **outboxed**: see "The outbox" below for why those two
specifically no longer follow this best-effort posture.

Because each service also **declares** what it sends (in its spec's `events`), the mesh aggregator
derives a **structural topology** — an edge from each sender to each consuming handler — and publishes
`topology.json`, **transport-agnostically** (an SQS command, an SNS event and an EventBridge event all
surface as edges the same way). After a refresh the Mesh UI's **Topology** table shows the whole graph
(`orders → payments`, `orders → inventory`, `orders → notifications`, `payments → shipping`, `payments →
notifications`, `payments → analytics`, `shipping → inventory/notifications/analytics`), source
`structural`, no tracing backend required. Layer on `Benzene.Mesh.Tracing.Tempo` to add *observed* edges
(real req-rate / error / latency) on top.

**See the flow fire:** invoke `orders-api` (any transport — `orders-create-sqs.json`, the API, …), then
watch CloudWatch: `orders` logs "order ... created; committed order row + payments:capture + order:placed
atomically" (the outboxed commit — see "The outbox" below for the stream-dispatch/sweep log lines that
follow it); `inventory` and `notifications` log the `order:placed` fan-out; `payments` logs "payment
captured for ...; sent shipping:book" + "published payment:captured"; `shipping` logs the booking +
"published shipping:dispatched"; `analytics` logs its metrics — all tied together by the propagated
correlation id.

## The outbox: atomic commit, stream dispatch, sweep redrive, dedup at the consumer

`orders-api`'s `orders:create` used to send `payments:capture` and `order:placed` best-effort, each
wrapped in its own swallow-and-log try/catch (`CreateOrderMessageHandler`, before this change) — a
transport hiccup silently lost the send while the order still "succeeded". This example now dogfoods
`Benzene.Outbox` + `Benzene.Outbox.DynamoDb` on that exact hop, the shipped fix for that hole
(`work/outbox-plan.md`; the cross-language spec at `docs/specification/` is unaffected — the outbox is
.NET-internal plumbing, not a wire-level change).

**Produce side (`orders-api`).**
1. `Startup` marks both routes `outboxed: true` and registers `AddOutbox(o => o.WriteMode =
   OutboxWriteMode.Transactional)` + `AddDynamoDbOutboxStore`/`AddDynamoDbOutboxTransaction` against the
   `orders-outbox` table. Only `orders-api` does this — see `Shared/OutboundSend.Outboxed` and
   `Shared/MeshServiceWiring`'s `enableOutboxDispatchStream` — the other five services are untouched.
2. `CreateOrderMessageHandler` sends both messages as usual (`IBenzeneMessageSender.SendAsync<T,
   Void>`); because the routes are outboxed, neither goes out over the wire yet — each is staged on the
   request's scoped buffer. The handler then builds the order row as a DynamoDB `TransactWriteItem` and
   commits it, **in one `TransactWriteItems` call**, together with both staged envelopes via
   `IDynamoDbOutboxTransaction.CommitAsync`. All-or-nothing: either the order and both envelopes persist,
   or none of them do — no more silent partial success.
3. Relay: `orders-outbox`'s DynamoDB stream (`NEW_IMAGE`) triggers `orders-api`'s own Lambda via an
   `aws_lambda_event_source_mapping`; `OutboxStreamDispatchMessageHandler` (topic
   `orders-outbox:INSERT`) dispatches the just-inserted envelope near-real-time. A scheduled EventBridge
   rule (`orders_outbox_sweep_schedule`, default every 5 minutes) invokes the same Lambda on the
   app-chosen topic `orders:outbox-sweep` (never `benzene:*` — reserved topics are spec surface);
   `OutboxSweepMessageHandler` redrives whatever the stream missed, retries with backoff, and parks
   anything past `OutboxOptions.MaxAttempts` (default 10).

**Consume side (`payments-api`).** An outboxed relay is at-least-once, so `payments:capture` can arrive
more than once (a stream dispatch AND a later sweep redrive both attempting the same envelope, or a
crash after send but before the envelope is marked dispatched). `Benzene.Outbox`'s capture middleware
stamps the envelope's own id into the `idempotency-key` header by default (`StampIdempotencyKey`);
`payments-api`'s SQS ingress runs `Benzene.Idempotency.DynamoDb`'s `UseIdempotency()`
(`enableSqsIdempotency: true`, its own `payments-idempotency` table), which dedups on that header with
zero extra configuration — the two packages are designed to click together.

**How to observe it:**
- **Normal path** — fire `orders:create` (see "Generate traffic" above) and watch `orders-api`'s
  CloudWatch logs: the commit log line, then (within a second or two) `OutboxStreamDispatchMessageHandler`'s
  "outbox stream dispatch ... Dispatched" line. `payments-api` should show exactly one capture per order
  even if you fire the same envelope's redelivery by hand.
- **Failure/redrive path** — revoke `orders-api`'s `sqs:SendMessage` on the payments queue (or point
  `PAYMENTS_QUEUE_URL` at a queue it can't reach) and fire `orders:create` again: the stream dispatch
  fails and reschedules with backoff; `terraform apply` it back, or wait for the next
  `orders:outbox-sweep` run, and watch the envelope go `Pending` → `Dispatched`. Leave the permission
  broken and the envelope reschedules with exponential backoff until `MaxAttempts` is reached, at which
  point the sweep parks it (`Parked`, kept for operator inspection — see `work/outbox-plan.md` §2.7);
  `OutboxSweepMessageHandler`'s log line reports the dispatched/rescheduled/parked/retired tally each run.
- Inspect the `orders-outbox` table directly (`terraform output orders_outbox_table_name`) to see
  envelope status/attemptCount/lastError first-hand.

**Honest limits, stated the way `work/outbox-plan.md` states them:** delivery is **at-least-once**, never
exactly-once — that's exactly why the consume side dedups. There's no ordering guarantee across
envelopes. `Immediate` mode (the default when a route omits `Transactional`) is store-and-forward, not
atomic with a state write — `orders-api` deliberately opts into `Transactional` because it has a state
write (the order row) to be atomic with; a service with no state write of its own would use the default.

## Claim-check: oversized payloads

Real transports cap message size — SQS/SNS/EventBridge at 256 KB (SQS raised its own max to 1 MiB in
2025; SNS and EventBridge did not, so the smallest common limit still governs), Service Bus standard at
256 KB, Azure Queue Storage at 64 KB. `Benzene.ClaimCheck` ships the pattern as a middleware pair —
offload on the outbound route, hydrate on the inbound transport pipeline — rather than making it a
transport or client-generation concern. This example dogfoods it on the same hop the outbox dogfoods:
`orders-api → payments-api`'s `payments:capture` (`work/claim-check-plan.md` Phase 6).

**Send side (`orders-api`).** `Startup` marks the `payments:capture` route `claimChecked: true`
(`OutboundSend.ClaimChecked`) and registers `AddS3ClaimCheckStore(bucket)` against the dedicated
`claim_checks` S3 bucket (`CLAIM_CHECK_BUCKET` env var). `Shared/MeshServiceWiring` wires
`UseClaimCheck()` on that route — **after** `UseOutbox()`, deliberately: capture's terminal pass needs
the *real* typed request to serialize into the durable envelope, so offload must not run until a send is
actually about to hit the wire (a non-outboxed route's normal send, or the outbox relay dispatcher's
pass-through re-send of a captured envelope's real deserialized payload). See `OutboundSend.ClaimChecked`'s
remarks for the full reasoning. `UseClaimCheck()` measures the serialized `payments:capture` body; under
`ClaimCheckOptions.DefaultThresholdBytes` (192 KiB) it's a no-op — the ordinary small send goes out
inline exactly as before. At or over threshold it `PutAsync`s the body to S3, stamps the
`benzene-claim-check` header with the store-issued `s3://…` reference, and replaces the outbound request
with a tiny placeholder — so the actual SQS message stays trivially small regardless of how large the
real payload was.

**Receive side (`payments-api`).** `Startup` registers the same `AddS3ClaimCheckStore(bucket)` (same
bucket, same `CLAIM_CHECK_BUCKET` env var), and `MeshServiceWiring.Configure` gets
`enableClaimCheckHydration: true`, which adds `UseClaimCheck<SqsMessageContext>()` to the SQS ingress —
after `UseIdempotency()` (a redelivered offloaded message still carries the same placeholder body and
the same reference, so its idempotency-key body hash is stable; deduping first avoids a store fetch for
a duplicate the handler will never see) and before `UseMessageHandlers` (the deserialization boundary).
A message with no `benzene-claim-check` header passes through untouched, without touching the store —
the common case stays free. A message that carries the header resolves it via `GetAsync`, replaces the
raw body with the real one, and only then reaches `CapturePaymentMessageHandler` — the handler and its
`CapturePaymentValidator` never know an offload happened.

**Triggering it for real.** `CapturePayment.OrderId`/`Amount`/`Currency` alone never gets near 192 KiB, so
`Orders/Handlers/OrderHandlers.cs`'s `CreateOrder` request carries an optional demo-only
`SupportingDocument` field (e.g. a large "attached receipt" blob). When present,
`Shared/ClaimCheckDemoPayload.Embed` folds it into `CapturePayment.OrderId` for the send (see the
"Contract note" below for why it rides an existing field rather than a new one), and
`Shared/ClaimCheckDemoPayload.Strip` takes it back off in `CapturePaymentMessageHandler` before
`payments-api` does anything with the order id or forwards it downstream — `shipping:book` and
`payment:captured` stay small on purpose, since neither of those routes is claim-checked and would
otherwise risk tripping SQS/EventBridge's own transport limit themselves. Fire an oversized order:

```bash
orders_api=$(terraform -chdir=deploy output -json service_spec_ui_urls | jq -r .orders | sed 's#/benzene/spec-ui$##')
doc=$(head -c 250000 /dev/zero | tr '\0' 'A')
curl -X POST "$orders_api/orders" -H 'content-type: application/json' \
  -d "{\"item\":\"Espresso Machine\",\"quantity\":1,\"supportingDocument\":\"$doc\"}"
```

250,000 bytes of filler is a deliberate choice, not just "big enough": `payments:capture` is also
**outboxed** (see "The outbox" above), and capture writes the full, real `CapturePayment` — including
the attachment — into a single DynamoDB item as part of the atomic `TransactWriteItems`. DynamoDB caps
an item at 400 KB, so the demo payload has to clear `ClaimCheckOptions.DefaultThresholdBytes`
(196,608 bytes) comfortably while staying well clear of that unrelated, tighter ceiling too — 250 KB
does both with room either side. (A base64-encoded binary attachment would have been the more realistic
"document" shape, but base64's ~4/3 size inflation pushes a 300 KB source file uncomfortably close to
the 400 KB DynamoDB limit on this particular route; plain filler sidesteps that for the demo.) A service
without an outboxed hop in front of its claim-checked route has no such ceiling to mind.

Ordinary `orders:create` calls (no `supportingDocument`) keep exercising the normal under-threshold
bypass path — most of this example's traffic never touches the claim-check store at all, which is the
point: the middleware only does work when a payload actually needs it.

**What to look for when it runs:**
- **Trace tags.** `ClaimCheckOffloadMiddleware`/`ClaimCheckHydrateMiddleware` tag the current Activity
  `benzene.claim-check = "offloaded"`/`"hydrated"` plus `benzene.claim-check.bytes` — with
  `AddDiagnostics()`'s per-middleware spans (see "OpenTelemetry" above) these show up as their own
  spans on the `orders-api → payments-api` X-Ray trace, right next to the correlation-id/trace-context
  middleware's own spans.
- **The bucket.** `terraform output claim_check_bucket`, then list objects under `claim-checks/` — one
  dated `claim-checks/payments:capture/yyyy/MM/dd/{guid}` object per offload
  (`S3ClaimCheckStore`'s key shape; the topic name travels into the key verbatim).
- **The lifecycle rule.** `aws_s3_bucket_lifecycle_configuration.claim_checks` expires objects under that
  prefix after **14 days** — sized to exceed SQS's own maximum retention (14 days) plus any DLQ redrive
  window, per `work/claim-check-plan.md` §3's sizing rule, so the rule can never expire an object while a
  redelivery could still need it.

**Honest limits.** There is **no delete-on-consume** — a redelivered offloaded message must still be able
to hydrate, and SNS-style fan-out means the first consumer to read a claim-checked payload is never
guaranteed to be the only one, so nobody deletes at read time. Offload and send are two non-atomic steps,
offload first: if the S3 `PutObject` succeeds but the subsequent SQS `SendMessage` then fails, the
uploaded object is **orphaned** — nobody ever consumes it, and the TTL-based lifecycle rule above is the
only cleanup, not a two-phase commit. That means payloads linger in the bucket for up to 14 days whether
or not they were ever read; encryption defers to the bucket's own default settings (SSE), and access
control is exactly the `service_claim_check` IAM policy `deploy/main.tf` grants.

**Contract note.** The offloaded payload's *schema* is unchanged by any of this — `Orders/contracts/payments.spec.json`
and the generated `CapturePayment` client type (see "orders → payments uses a *generated* client" below)
are completely untouched by claim-checking, because offload happens in outbound *middleware*, below the
typed client call, not inside it. That is the actual point of shipping claim-check as a middleware pair
rather than a client-generation feature: any existing route can adopt it with zero contract or codegen
changes. This dogfood takes that literally — rather than growing `CapturePayment` with a new field just
to manufacture an oversized demo payload (which *would* have touched the contract, only for demo
plumbing, not because claim-check needed it to), the oversized `SupportingDocument` rides inside the
existing `OrderId` string field (see `Shared/ClaimCheckDemoPayload`'s remarks). A real service adding a
genuinely large field to a contract remains free to do so — claim-check would offload it exactly the same
way with no further wiring.

## Deploy it (via GitHub Actions — no local tooling)

Tooling: **Terraform, run by GitHub Actions**, fronted by **API Gateway HTTP APIs** (one per Lambda,
so each service's Spec UI and the Mesh UI serve their relative assets from their own API root).

1. **Add two repo secrets** (Settings → Secrets and variables → Actions):
   - `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` — an IAM principal allowed to manage Lambda, IAM,
     S3, API Gateway, and EventBridge.
2. **Run the workflow**: Actions → **Deploy AWS Mesh Example** → *Run workflow* (pick a region).
   It builds all seven Lambdas (self-contained `provided.al2023`), then `terraform apply`s the stack.
3. **Grab the URLs** from the workflow's final `terraform output` step:
   - `mesh_ui_url` — the Mesh UI.
   - `service_spec_ui_urls` — each service's Spec UI.
   - `mesh_refresh_url` — POST to force a discovery+aggregation pass now.

### Deploy locally instead (if you do have Terraform)

State is kept in a per-account S3 bucket (`benzene-mesh-tfstate-<account-id>`) so repeated runs are
incremental rather than colliding — configured at `init` time, so nothing account-specific is
committed. Create the bucket once, then `init` against it:

```bash
# Build + zip each Lambda (self-contained, provided.al2023) into examples/AwsMesh/artifacts/, then:
cd examples/AwsMesh/deploy
ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
aws s3api create-bucket --bucket "benzene-mesh-tfstate-$ACCOUNT" --region eu-west-1 \
  --create-bucket-configuration LocationConstraint=eu-west-1   # omit --create-bucket-configuration in us-east-1
terraform init \
  -backend-config="bucket=benzene-mesh-tfstate-$ACCOUNT" \
  -backend-config="key=aws-mesh/terraform.tfstate" \
  -backend-config="region=eu-west-1"
# If resources already exist from an earlier run without persisted state, adopt them first:
REGION=eu-west-1 PROJECT=benzene-mesh ./adopt-existing.sh
terraform apply -var region=eu-west-1
```

If an earlier run failed *midway*, the account can end up with duplicate/partial resources (API
Gateway allows duplicate names), which makes adoption ambiguous. Recover with a one-time clean slate —
delete every app resource (never the state bucket), then recreate:

```bash
REGION=eu-west-1 PROJECT=benzene-mesh ./cleanup-all.sh
terraform apply -var region=eu-west-1
```

In the GitHub Actions workflow this is the **`recreate`** checkbox on *Run workflow* — tick it once to
recover, then leave it off for normal incremental deploys.

## See it working (both ends)

1. **Open a service Spec UI** (`service_spec_ui_urls.orders`, etc.) — proof the services are up
   and Cloud Service Profile-conformant, each with its own domain contract and health.
2. **Trigger a mesh pass**: `curl -XPOST "$mesh_refresh_url"` (or wait for the schedule — every minute
   by default, `var.aggregate_schedule`). It returns `{ "discovered": 6 }` once it has found the six
   `benzene`-tagged Lambdas. The schedule keeps the catalog + usage feed fresh on its own; the Mesh UI
   explorer loads artifacts once per page load, so reload the page to pick up a newer pass.
3. **Open the Mesh UI** (`mesh_ui_url`) — the catalog of the six services the mesh **discovered by
   itself** (no `mesh.json`), each interrogated by direct Lambda-Invoke, with health and dependencies.
   Below the service list, the **Topics** table is the cross-service catalog (every topic across the
   platform → which service owns it, its HTTP mapping, domain vs utility), with a **show utilities**
   toggle that hides the reserved Benzene endpoints by default.
4. **Open a service's Spec UI** and note the **Benzene utilities** panel — the reserved
   `spec`/`health`/`mesh` endpoints are collapsed out of the service's domain topics.

That's the end-to-end test: services on one end, the self-discovering mesh on the other.

The `POST $mesh_refresh_url` endpoint returns **201 Created** (a pass creates/refreshes the catalog
artifacts) with `{ "discovered": N }`.

## How discovery is scoped

- The six **service** Lambdas carry a `benzene` **resource tag**; the **mesh** Lambda does not — so
  the mesh discovers the services but never itself. Change the tag key via the `discovery_tag_key`
  Terraform variable (and the mesh's filter) to match your own tagging.
- The mesh's IAM role gets exactly `lambda:ListFunctions` + `lambda:ListTags` (discover),
  `lambda:InvokeFunction` scoped to the six services (interrogate), and `s3:*Object`/`ListBucket`
  on the artifact bucket. Read + describe-invoke only.

## Cost: this demo is not free while it merely exists

A standing deploy costs money without anyone using it. The scheduled aggregation invokes the mesh
Lambda, which fans spec + healthcheck calls out to all six services, and **every one of those is
X-Ray-traced and CloudWatch-metered**. The X-Ray free tier has two separate dimensions and the demo
can exhaust either:

| Free-tier dimension | Limit / month | What consumes it here |
| --- | --- | --- |
| Traces **recorded** | 100,000 | Every sampled span the services + mesh emit |
| Traces **retrieved or scanned** (`Global-XRay-TracesAccessed`) | 1,000,000 | The Mesh UI's live queries — `GetTraceSummaries` **scans every trace in the picked window on each poll** |

The second one is the surprising one: the bill grows with *how much you look*, not just how much
traffic there is. Three knobs, all defaulted for a cheap standing demo:

- **`trace_sample_rate`** (default `0.2`) — a parent-based ratio sampler, so a transaction is sampled
  or dropped as a whole and the mesh never shows half a flow. Cuts **both** dimensions at once: fewer
  traces recorded also means fewer traces for every query to scan. Set to `1` to record everything.
- **`aggregate_schedule`** (default `rate(15 minutes)`) — the standing traffic floor. At
  `rate(1 minute)` the idle demo alone produces roughly 20k Lambda invocations and 35k traces a day.
- **The Mesh UI's own polling** — 15s for the live plane, 5 min for the 24h issue inbox, **paused
  entirely while the browser tab is hidden**, and the inbox asks for counts only (`includeFlows:
  false`) so it never triggers a day-wide trace scan. No configuration needed; just be aware that a
  dashboard left open on-screen is the single biggest consumer of the *retrieved* dimension.

If you only need the demo occasionally, **tear it down between sessions** (below) — that takes the
cost to zero, and a redeploy is one workflow run.

## Teardown

**Via GitHub Actions (recommended):** run the **Destroy AWS Mesh Example** workflow
(`.github/workflows/mesh-example-aws-destroy.yml`) — the counterpart of the deploy workflow. It uses
the same remote S3 state, so it destroys exactly what the deploy created. Pick the region you deployed
to, and optionally tick **Also delete the Terraform state bucket** for a
full cleanup. Beyond `terraform destroy` it also empties the artifacts bucket first (S3 refuses to
delete a non-empty bucket) and deletes the `/aws/lambda/benzene-mesh-*` log groups Lambda creates
implicitly — neither is a Terraform resource, and both would otherwise linger and keep billing.

**Locally:**

```bash
# Terraform evaluates the whole config to plan a destroy, and the S3 code objects call filemd5() on
# the Lambda zips — so the artifacts have to exist even though a destroy uploads nothing. If you no
# longer have them (a fresh clone, or you cleaned the build), stand in empty placeholders first:
mkdir -p examples/AwsMesh/artifacts
for n in orders payments shipping inventory notifications analytics mesh; do
  : > "examples/AwsMesh/artifacts/$n.zip"
done

cd examples/AwsMesh/deploy && terraform destroy
```

Note the local path leaves the implicit CloudWatch log groups behind; delete them with
`aws logs delete-log-group --log-group-name /aws/lambda/benzene-mesh-<service>` if you want a clean
account.

## Cold-start tuning

.NET on Lambda has a real cold-start cost, and it is **mostly not Benzene** — it's JIT compilation
and reflection-driven code generation (System.Text.Json metadata, DI graph build, handler/validator
reflection) that only runs once per fresh execution environment. Optimising it well is what lets an
X-Ray trace isolate Benzene's own overhead from the .NET/AWS floor. What this example already does,
and the levers beyond it, in rough order of value-for-effort:

**Already applied here:**
- **ReadyToRun** — the publish step (`.github/workflows/mesh-example-aws-deploy.yml`) uses
  `-p:PublishReadyToRun=true`, precompiling IL to native so most framework/app code doesn't JIT at
  startup. This is the standard first move and it's on.
- **`InvariantGlobalization=true`** — every service `.csproj` sets it (also required because
  `provided.al2023` ships no libicu). Skips ICU load at init.
- **Shared static Lambda event serializer** — `AwsLambdaMiddlewareRouter` caches the
  `DefaultLambdaJsonSerializer` statically so the (large) AWS event type's STJ metadata is built once
  per process, not per invocation (see `Benzene.Aws.Lambda.Core`).
- **Framework warm-up (opt-in)** — `AddBenzeneWarmUp()` pre-builds each handler's request **and
  response** STJ metadata and each FluentValidation rule set during Lambda INIT, invisibly (no
  synthetic message, no logs/metrics/traces). Enable it in the service startup to move those
  first-message JIT gaps into INIT. See `Benzene.Core.MessageHandlers` → *Cold-start warm-up*.
- **Memory = 1024 MB** (services; the mesh was already 1024). Lambda scales vCPU with memory and
  cold start is CPU-bound, so this roughly halves init/JIT wall time vs 512 MB. Dial to ~1769 MB for
  a full vCPU (shortest cold start) or back to 512 to minimise cost — one line in `deploy/main.tf`.

**Remaining levers (not applied — each has a real trade-off):**
- **Source-generated JSON** *(event types done)* — the largest unwarmed cost was STJ's reflection-based
  metadata build for the AWS event types. **Every event-source adapter now uses a source-generated
  context** (API Gateway v1/v2 + custom authorizer, SQS, SNS, S3, EventBridge, DynamoDB, Kinesis, Kafka,
  and the BenzeneMessage direct-invoke path), so the cold event→Benzene conversion no longer pays the
  event-type metadata build. Still a follow-up: the same treatment for the message **payload** types
  (app-authored or Benzene-generated context wired into the media format).
- **arm64 (Graviton)** — usually better price/performance and competitive cold start. Requires
  flipping `lambda_architecture` to `arm64`, the CI `RID` to `linux-arm64`, **and** the ADOT collector
  layer ARN (see `variables.tf`) to the matching arm64 build — a coordinated change, so it's opt-in.
- **ADOT collector overhead** — the trace's ~26 ms `extensionOverhead` (and some INIT weight) is the
  telemetry extension, not Benzene. If cold latency matters more than full-fidelity tracing, sample
  traces or drop the collector layer.
- **Provisioned concurrency** — the guaranteed-warm escape hatch: pre-initialised environments, no
  cold start on the covered concurrency, at standing cost. The blunt-instrument option when a specific
  path must never pay init.

**Deliberately *not* done — these would break this app:**
- **Trimming (`PublishTrimmed`)** — Benzene discovers handlers, resolves DI, and builds
  FluentValidation rules by reflection, and the default serializer is reflection-based STJ. Trimming
  strips types those paths need at runtime → failures that don't show at build time. Don't enable it
  without full trim annotations and source-gen serialization first.
- **Native AOT** — the biggest cold-start win in principle, but incompatible as-is for the same
  reflection/DI/reflection-STJ reasons; it would require source-generating serialization and reworking
  reflection-based discovery. A project, not a setting.
- **SnapStart** — not available on the `provided.al2023` **custom** runtime (SnapStart covers managed
  runtimes only), and .NET 10 has no managed Lambda runtime — so it's off the table for this deployment.

## Known first-deploy iteration points

I can build and compile all of this, but the live AWS behaviour is only verifiable on a real deploy.
The most likely things to tweak on the first run (all localized):
- **Custom-runtime packaging** — the `bootstrap` wrapper + self-contained publish RID/arch
  (`lambda_architecture` must match the CI `RID`). Note the `provided.al2023` runtime ships **no
  libicu**, so all seven Lambda projects publish with `<InvariantGlobalization>true</InvariantGlobalization>`
  — without it the apphost aborts at init with "Couldn't find a valid ICU package installed".
- **API Gateway payload format** — pinned to `1.0` to match `Benzene.Aws.Lambda.ApiGateway`; if a UI
  route 500s, this is the first thing to check.
- **EventBridge → topic routing** — both the `mesh:aggregate` schedule and the inter-service
  integration events (`payment:captured`, `shipping:dispatched`) rely on the Benzene EventBridge adapter
  reading `detail-type` as the topic. The custom-bus rules match on that same `detail-type`; if a
  consumer never fires, confirm the publisher's `DetailType` and the rule's `event_pattern` agree (POST
  `mesh_refresh_url` triggers a pass independently of any of this).
- **SNS fan-out routing** — `order:placed` carries the Benzene topic in the `topic` **message
  attribute**; the SNS→Lambda subscription delivers it to inventory-api and notifications-api, whose
  `aws.UseSns` ingress routes on that attribute. If a subscriber doesn't route, check the attribute is
  present on the published message.
- **Outbox stream/sweep IAM** — `orders-outbox`'s event-source mapping polls with `orders-api`'s own
  execution role (not a separate one); if the stream dispatch handler never fires, check
  `service_dynamodb`'s `dynamodb:DescribeStream`/`GetRecords`/`GetShardIterator`/`ListStreams` grant on
  the table's `stream_arn` before anything else. This example has not been deploy-verified end to end
  against real AWS — the flow above is verified by compiling and by the unit tests under
  `test/Benzene.Core.Test/Outbox/`, not by an actual `terraform apply` + live traffic run.

## Build locally

```bash
dotnet tool restore   # orders-api generates its payments client with the published `benzene` CLI
for p in Orders Payments Shipping Inventory Notifications Analytics Mesh; do
  dotnet build "examples/AwsMesh/$p/Benzene.Examples.AwsMesh.$p.csproj"
done
```

## orders → payments uses a *generated* client

payments-api emits its own contract on every build (`Benzene.Descriptor` — see
[docs/contract-artifacts.md](../../docs/contract-artifacts.md)). orders-api commits a copy of it at
`Orders/contracts/payments.spec.json` — the way a consumer team commits a copy of a producer's
contract — and its csproj turns that into a typed client at build time:

```xml
<PackageReference Include="Benzene.CodeGen.Build" Version="0.0.2-alpha.6" PrivateAssets="all" />
<BenzeneServiceContract Include="contracts\payments.spec.json" Mode="topic-client"
                        ServiceName="Payments" Topics="payments:capture"
                        Namespace="Benzene.Examples.AwsMesh.Orders.Clients" />
```

The generated `CapturePayment` **request type** is what `CreateOrderMessageHandler` builds and sends —
the topic id and the request shape come from payments-api's contract, so the hand-written
`OutboundPaymentCapture` mirror DTO this used to require is gone. `Topics="payments:capture"` scopes the
client to the one topic orders-api actually calls, so none of payments-api's other surface is coupled in.

The handler does NOT call the generated client's `CapturePaymentsAsync(...)` method, though — it sends
via `IBenzeneMessageSender.SendAsync<CapturePayment, Void>("payments:capture", …)` directly. That's the
outbox talking (see "The outbox" above): `payments:capture` is now an **outboxed** route, and an
outboxed/SQS route is fire-and-forget only (`TResponse` must be `Void` — a captured-not-yet-sent
message has no response to give). `CapturePaymentsAsync` asks for a typed `PaymentDto` response, which
no such route can ever produce (this was latent before the outbox too — SQS itself is
send-acknowledgement-only, so that call was always going to hit `OutboundResponseTypeMismatchException`
the moment a queue was actually wired; the best-effort try/catch silently swallowed it). The client and
its DI registration (`AddPaymentsClients()`) are still generated and still wired, dogfooding the
from-source codegen path this project otherwise exists to exercise — just not called from this one
call site any more.

This example deliberately uses the **published** CLI from NuGet (pinned in
`.config/dotnet-tools.json`) rather than building it from source, so it exercises what a real consumer
gets. `examples/CodeGen/…Contracts.Consumer` runs the same targets from source, so working-tree
regressions are still caught before a release.

### Rough edges this surfaced

Adopting the generated client turned up four things worth fixing in the tooling, recorded as
requirements in
[`work/spec-mesh-tooling-implementation-plan.md`](../../work/spec-mesh-tooling-implementation-plan.md).
The first three are now fixed; the fourth is a deliberately parked known limitation.

1. ~~**`benzene:healthcheck` was required unconditionally.**~~ **Fixed.** Every generated client used
   to list it in `RequiredTopics`, so the outbound-routing start-up check (`Enforce` by default) failed
   the host of *any* service adopting *any* generated client until it registered a route for a topic it
   never meant to call — here, over fire-and-forget SQS that could never answer a health probe anyway.
   Generated clients no longer include Benzene's reserved `benzene:*` endpoints at all: they cover
   domain topics only. The `OutboundSend.HealthCheck(...)` workaround is gone.
2. ~~**The generated code's dependencies weren't declared.**~~ **Fixed by the same change.** The only
   undeclared dependency was `Benzene.Clients.HealthChecks` (`IHasHealthCheck`/
   `ClientHealthCheckProcessor`), pulled in by the emitted health check; with that gone the generated
   code needs nothing beyond what a consumer already references, and this project's
   `ProjectReference` to it has been removed.
3. ~~**No DI registration is generated.**~~ **Fixed.** The generator now emits one beside the client:
   `AddPaymentsClients()` (and a per-topic `AddPaymentsCaptureServiceClient()`), an extension on
   `IBenzeneServiceContainer` — Benzene's own container abstraction, so it works whatever container is
   underneath rather than assuming Microsoft's `IServiceCollection` — registering the client `Scoped` to
   match `IBenzeneMessageSender`'s lifetime. `Startup` now calls it instead of hand-registering.
4. **`decimal` does not survive the round trip.** payments-api's `CapturePayment.Amount` is `decimal`;
   JSON Schema records it as `"number"` and the generator emits `double`, so the call site casts. Fine
   for this demo, wrong for money. **Parked** as a known limitation: the schema is the governing
   contract, and some types are known not to travel well in JSON — see finding 7d in the plan.

Neither the remaining limitation nor anything above blocks the build — the client is generated and
compiled on every `dotnet build`.
