# Claim Check (oversized payloads)

`Benzene.ClaimCheck` lets an outbound message that's too big for its transport ride anyway,
without changing the transport, the contract, or the handler. It ships as a middleware **pair** —
**offload** on the outbound route pipeline, **hydrate** on the inbound transport pipeline — not a
client feature and not a change to how you write handlers.

## The problem

Every async transport caps message size, and the cap is smaller than people expect:

| Transport | Limit |
|---|---|
| AWS SQS | 256 KB default (AWS raised this to 1 MiB in 2025 for standard/FIFO queues) |
| AWS SNS | 256 KB |
| AWS EventBridge | 256 KB per event |
| Azure Service Bus (Standard) | 256 KB |
| Azure Service Bus (Premium) | 100 MB |
| Azure Queue Storage | 64 KB |
| Kafka | ~1 MB (broker `message.max.bytes` default; many clusters raise it) |

Hitting one of these mid-project usually means hand-rolling an offload-to-blob-storage dance —
put the payload somewhere durable, put a reference on the message, teach every consumer to resolve
it back. `Benzene.ClaimCheck` ships that dance once, as ordinary middleware, so any existing route
can adopt it without touching its contract.

> Because the smallest common limit across the SQS/SNS/EventBridge family governs, the package's
> default offload threshold is sized off it — see [Options](#options) below.

## How it works

**Offload** (`UseClaimCheck()`) runs on the *outbound* `OutboundContext` route pipeline, as the
last non-terminal step before the transport converter (`UseSqs(...)`, `UseSns(...)`, etc). It
serializes `context.Request` and measures its UTF-8 byte count. Under the threshold, it's a no-op —
`next()` runs and the message goes out exactly as it would without the package. At or over the
threshold, it puts the serialized body into an `IClaimCheckStore`, stamps the store-issued
reference onto the `benzene-claim-check` header, and replaces `context.Request` with a tiny
placeholder — so the actual message that hits the wire stays small regardless of how large the
real payload was.

**Hydrate** (`UseClaimCheck<TContext>()`) runs on the *inbound* transport pipeline, after the
observability prelude (so the store fetch appears in the trace) and before `UseMessageHandlers()`
(the deserialization boundary). A message with no `benzene-claim-check` header passes through
untouched, without ever touching the store — the common, non-offloaded case stays free. A message
that carries the header is resolved via the store and the raw body is replaced with the real
content *before* deserialization, so the handler never knows an offload happened.

```csharp
// Outbound: last non-terminal step of the route, before the transport converter.
services.UsingBenzene(x => x.AddOutboundRouting(routing => routing
    .Route("payments:capture", pipeline => pipeline
        .UseCorrelationId()
        .UseClaimCheck()          // offload
        .UseSqs(queueUrl))));

// Inbound: after the observability prelude, before UseMessageHandlers.
app.UseSqs(sqs => sqs
    .UseW3CTraceContext()
    .UseClaimCheck()              // hydrate — TContext inferred as SqsMessageContext
    .UseMessageHandlers());
```

Register a store once, on both the sending and receiving service (see
[Choosing a store](#choosing-a-store)):

```csharp
services.AddInMemoryClaimCheckStore(TimeSpan.FromHours(24));   // single instance / tests / local dev
// or, in production:
services.AddS3ClaimCheckStore(bucket);                          // Benzene.ClaimCheck.Aws.S3
services.AddBlobClaimCheckStore(container);                     // Benzene.ClaimCheck.Azure.Blob
```

**The claim check operates on the serialized wire body** — the string a transport converter would
actually send — never the typed message. That's what keeps it format-agnostic across
`Benzene.Xml`, `Benzene.MessagePack`, and Avro: the store round-trips the exact wire string, and
whatever media-format negotiation the receiver does behaves as if the payload had never left the
message.

## Options

`UseClaimCheck(o => ...)` / `UseClaimCheck<TContext>(o => ...)` both take an optional
`ClaimCheckOptions` configurator:

| Option | Default | Meaning |
|---|---|---|
| `ThresholdBytes` | `192 * 1024` (192 KiB / 196,608 bytes) | The serialized-body size, in UTF-8 bytes, at or above which an outbound message is offloaded instead of sent inline. Derived from the smallest common limit in the 256 KB transport family (SQS/SNS/EventBridge), with headroom for message attributes/envelope, which count against the same limit. |
| `AlwaysOffload` | `false` | Offload every message on this route regardless of size. Routes are already per-topic (`routing.Route("payments:capture", ...)`), so "always offload this topic" is just `UseClaimCheck(o => o.AlwaysOffload = true)` on that route — no separate topic map. |
| `HeaderName` | `ClaimCheckHeaders.ClaimCheck` (`"benzene-claim-check"`) | The header the reference travels on. Overriding it is a deployment agreement: the sending route and the receiving pipeline must agree on the same name, or the receiver never sees the reference and hands the unusable placeholder body to the handler. |
| `Serializer` | `null` → `Benzene.Clients.JsonSerializer` | The serializer used to measure and store the outbound body. |

Because the default threshold (192 KiB) already clears the 256 KB family with headroom, most SQS-,
SNS-, and EventBridge-fronted routes need no tuning at all. Azure Queue Storage's 64 KB cap is the
exception — a route sending on Queue Storage needs a tighter, transport-specific override:

```csharp
routing.Route("orders:archive", pipeline => pipeline
    .UseClaimCheck(o => o.ThresholdBytes = 48 * 1024)   // headroom under Queue Storage's 64 KB cap
    .UseQueueStorage(queueName));
```

### Serializer-consistency caveat

The offload middleware's serializer must match the transport converter's serializer for that same
route. Both default to `Benzene.Clients.JsonSerializer`; a route that passes a custom `ISerializer`
to its converter (e.g. `UseSqs(url, mySerializer)`) must pass the same instance to
`UseClaimCheck(o => o.Serializer = mySerializer)`, or the bytes measured and stored will not be
what the converter would actually have produced for the un-offloaded message. This is a real,
load-bearing coupling — it's documented rather than hidden because hoisting serialization out of
every outbound converter (which would remove the coupling entirely) is a real future refactor, out
of scope for this middleware today.

## Choosing a store

Persistence is pluggable via `IClaimCheckStore`, mirroring [Idempotency](cookbooks/idempotency.md)'s
shape:

- **`Benzene.ClaimCheck`** ships `InMemoryClaimCheckStore` (`AddInMemoryClaimCheckStore(ttl?)`) —
  single-process, for one host, tests, or local development. State lives in the process only: in a
  multi-instance deployment each instance keeps its own map, so a payload offloaded by one instance
  is invisible to another.
- **`Benzene.ClaimCheck.Aws.S3`** ships `S3ClaimCheckStore` over `IAmazonS3`
  (`AddS3ClaimCheckStore(bucket, prefix = "claim-checks/")`) — the production AWS store. Keys are
  `{prefix}{topic}/{yyyy/MM/dd}/{guid}`; the reference is `s3://{bucket}/{key}`.
- **`Benzene.ClaimCheck.Azure.Blob`** ships `BlobClaimCheckStore` over a `BlobContainerClient`
  (`AddBlobClaimCheckStore(container, prefix = "claim-checks/")`, or a convenience overload that
  builds the container client from a blob service `Uri` + container name using
  `DefaultAzureCredential`) — the production Azure store. Keys use the same layout as S3; the
  reference is `azblob://{container}/{key}`.

Both cloud stores require the bucket/container to already exist — **they never create it**, the
same posture as every other Benzene infrastructure store (`Benzene.Mesh.Aws.S3`,
`Benzene.Idempotency.DynamoDb`).

### Retention is a lifecycle rule, not the middleware

There is **no delete-on-consume**. SNS-style fan-out can deliver one offloaded message to several
independent consumers, and at-least-once transports redeliver — deleting a payload at read time
would either starve a sibling consumer or make a retry permanently unhydratable. Retention is
therefore **entirely infrastructure's responsibility**, via an S3 bucket lifecycle rule or an Azure
Blob lifecycle-management delete rule scoped to the store's prefix:

```hcl
resource "aws_s3_bucket_lifecycle_configuration" "claim_checks" {
  bucket = aws_s3_bucket.claim_checks.id
  rule {
    id     = "expire-claim-checks"
    status = "Enabled"
    filter { prefix = "claim-checks/" }
    expiration { days = 14 }   # SQS's own retention maxes at 14 days — see the sizing rule below
  }
}
```

**TTL sizing rule (state this verbatim wherever the rule is configured): the TTL must exceed the
longest possible path from send to last possible consumption — queue retention plus any DLQ redrive
window.** Undersizing it means a slow redelivery can find its claim already deleted, which surfaces
as `ClaimCheckNotFoundException` on the receiving side — a fail-loud failure, not silent data loss,
but still one you can avoid by sizing the rule correctly. SQS's own maximum retention is 14 days,
which is why `examples/AwsMesh`'s lifecycle rule uses 14 days.

Neither store package creates the lifecycle rule for you — infra owns infra, the same posture as
every other Benzene store package.

### At-rest posture

Encryption defers to the store's own defaults (S3 SSE-S3/SSE-KMS, Azure Storage SSE) — this
package does not build key management. Access control is whatever IAM policy / RBAC role governs
the bucket or container. Because retention is TTL-based rather than delete-on-consume, offloaded
payloads linger in the store for up to the configured TTL whether or not they were ever read —
services with data-retention obligations should size the TTL accordingly. See
[Privacy & Data Handling](privacy-and-data-handling.md).

## Failure semantics

- **A missing or expired claim on receive fails loud.** `IClaimCheckStore.GetAsync` returns `null`
  for not-found/expired; the hydrate middleware turns that into `ClaimCheckNotFoundException`
  naming the reference, so the transport's own failure semantics (nack → redelivery → eventually a
  DLQ) apply exactly as they would for any other unprocessable message. There is no special-cased
  swallowing and no placeholder-processing.
- **A reference outside a store's own configuration is a security boundary, not a not-found case.**
  A store MUST throw `ClaimCheckStoreMismatchException` for a reference with the wrong scheme, or
  for a cloud store, a foreign bucket/container/prefix — never attempt the fetch. A store must
  never resolve an attacker-supplied or otherwise foreign location.
- **Offload-then-send is not atomic.** The offload middleware puts to the store *before* calling
  `next()`. If the put fails, the middleware throws and the send never happens — fail loud, nothing
  half-done. If the put succeeds and the subsequent send then fails, the stored payload is
  **orphaned** — nobody will ever consume it, and TTL expiry is the only cleanup, not a two-phase
  commit. The reverse order (send first) would be worse: a message pointing at a blob that never
  arrives.
- **No setter registered for the transport also fails loud, not silently.** If a message carries a
  claim-check reference but no `IMessageBodySetter<TContext>` is registered for that context type,
  the hydrate middleware throws an `InvalidOperationException` naming the context type and telling
  you to register a setter or remove `UseClaimCheck<TContext>()` from the pipeline — never leaves
  the unresolved placeholder for the handler to choke on.

## Observability

Both middleware tag the current `Activity` when they act — `benzene.claim-check` set to
`"offloaded"` or `"hydrated"`, plus `benzene.claim-check.bytes` — the same naming convention as
`benzene.correlation-id`. With `AddDiagnostics()`'s per-middleware spans (see
[Monitoring & Diagnostics](monitoring.md)), these show up as their own spans right next to the
correlation-id/trace-context middleware's own spans, so a trace makes an offload/hydrate pair
visible end to end across a hop.

## Supported transports

Hydration works on **any context that has both an `IMessageHeadersGetter<TContext>` (to read the
`benzene-claim-check` header) and an `IMessageBodySetter<TContext>` (to replace the raw body before
deserialization) registered**. Every transport already registers the headers getter (it's how
`UseW3CTraceContext<TContext>` and `UseIdempotency<TContext>` read headers too); the body setter is
the newer, narrower piece — `IMessageBodySetter<TContext>` exists as an abstraction
(`Benzene.Abstractions.Messages`) that each transport package opts into individually.

Shipped today:

| Transport | Context | Package |
|---|---|---|
| AWS Lambda SQS | `SqsMessageContext` | `Benzene.Aws.Lambda.Sqs` |
| AWS Lambda SNS | `SnsRecordContext` | `Benzene.Aws.Lambda.Sns` |
| AWS Lambda EventBridge | `EventBridgeContext` | `Benzene.Aws.Lambda.EventBridge` (also re-embeds the reserved `_benzeneHeaders` object into the hydrated body, mirroring the sender-side embed rule, so header reads later in the pipeline still see them) |
| Standalone SQS consumer | the `Benzene.Aws.Sqs` consumer context | `Benzene.Aws.Sqs` |

Offload has no such restriction — it only touches `OutboundContext`, which every outbound route
already has — so any outbound client transport can offload today regardless of whether its
*receiving* side has hydration wired yet.

**Adding a new transport is a small, additive change**: implement + register
`IMessageBodySetter<TContext>` for that transport's context type (the mutable Lambda event POCOs
above are a one-line `context.SomeMessage.Body = body` implementation, registered with
`TryAddScoped`). Several transport packages already document this as their next step in their own
`CLAUDE.md` under "Claim-check hydration" — Kafka, RabbitMQ, Azure Queue Storage, Azure Event Grid,
and the Azure self-hosted Service Bus/Kafka workers among them. **Azure Service Bus is the one
transport where it isn't a trivial one-liner**: the Azure SDK's
`ServiceBusReceivedMessage.Body` has no public setter (it's produced by the SDK from the wire
message and is read-only by design), so hydrating a Service Bus message needs the context itself to
grow a body-override slot its body getter consults ahead of `Message.Body` — a real, if small,
design change, not yet done. Until a setter is registered for a transport, wiring
`UseClaimCheck<TContext>()` on it throws the descriptive `InvalidOperationException` above the
moment a message actually needs hydrating — never a silent pass-through of an unusable placeholder.

## Cross-language wire contract

`benzene-claim-check` is a **Tier C (add-on)** header in the language-neutral Benzene
specification — see
[wire-contracts.md §2.1, "Claim check (add-on)"](https://benzene.app/docs/specification/wire-contracts.html#21-claim-check-add-on).
Tier C means each language port adopts it on its own schedule: a service that offloads is only
interoperable with consumers that have wired the add-on and share access to the same store — an
explicit deployment agreement, exactly like any other Tier C middleware (`traceparent`,
`x-correlation-id`).

## See also

- [`examples/AwsMesh`](../examples/AwsMesh/README.md#claim-check-oversized-payloads) — a dogfooded,
  deployable example: an oversized `payments:capture` send from `orders-api` to `payments-api`,
  offloaded through `Benzene.ClaimCheck.Aws.S3`, with the Terraform lifecycle rule and trace tags to
  look for.
- [Idempotency](cookbooks/idempotency.md) — the sibling pattern this package's store shape mirrors;
  pairs naturally on the receiving side of an at-least-once, claim-checked route.
- [Privacy & Data Handling](privacy-and-data-handling.md) — sizing retention against data-handling
  obligations.
- [Middleware Reference](reference/middleware.md#useclaimcheck--useclaimchecktcontext) —
  `UseClaimCheck()` / `UseClaimCheck<TContext>()` in the full middleware catalogue.
- [Package Reference](reference/packages.md#reliability--consistency) — the three shipped packages.
