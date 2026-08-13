# Claim-Check Pattern for Large Payloads — Implementation Plan

**Status:** Planned — the owner approved the feature direction 2026-08-13 ("offload the payload to
something like S3, or Blob Storage for Azure; carry a claim reference on the message; hydrate on
receive"). This document turns that direction into implementable phases.
**Date:** 2026-08-13
**Motivation:** transport size limits (SQS/SNS/EventBridge 256 KB, Service Bus standard 256 KB,
Azure Queue Storage 64 KB) force people to hand-roll payload offloading today. Benzene should ship
it as a middleware pair: **offload** on the outbound route pipeline, **hydrate** on the inbound
transport pipeline, with the claim reference travelling as a wire header.
**Audience:** implementation agents. Each phase is a self-contained task; do them in order unless
"Depends on" says otherwise. File paths and APIs were verified against the repo on 2026-08-13 —
re-read every cited file before editing; the *intent* is authoritative, not line numbers.

**Decisions already made (do not re-litigate):**
1. **It is a middleware pair, not a client/transport feature.** Offload runs on the
   `OutboundContext` route pipeline before the terminal transport converter; hydrate runs on the
   inbound transport pipeline before `UseMessageHandlers`. §1 records the reasoning.
2. **The claim reference travels in the header `benzene-claim-check`** — a Tier **C (add-on)**
   header per the spec's tier system (`wire-contracts.md` §2) and the accepted naming principle
   (Benzene-invented names in shared namespaces carry the `benzene-` marker,
   `Benzene/work/benzene-naming-principle.md`).
3. **This is an observable wire contract, so it gets a spec-repo phase** (Phase 3). The spec
   surface is deliberately small but NON-ZERO: one header-table row plus a short subsection
   (reference format + hydration semantics). No conformance fixture — consistent with the other
   Tier C headers (`traceparent`, `x-correlation-id`), which have none.
4. **Reference format:** an opaque **URI-form string** `scheme://location/key` issued by the store
   (e.g. `s3://my-bucket/claim-checks/orders/…/1a2b….json`, `azblob://container/key`). Receivers
   resolve it ONLY through their configured store, and the store MUST refuse a reference outside
   its own configuration (scheme + bucket/container + prefix) — fail loud, never fetch an
   attacker-supplied location.
5. **Package shape mirrors `Benzene.Idempotency` / `.DynamoDb`:** a transport-agnostic core
   (`Benzene.ClaimCheck`) plus per-store packages (`Benzene.ClaimCheck.Aws.S3` first,
   `Benzene.ClaimCheck.Azure.Blob` second). The store packages are **separate from**
   `Benzene.Mesh.Aws.S3`/`Benzene.Mesh.Azure.Blob` — see §2 for why — but copy their plumbing
   conventions (404→null mapping, prefix normalization, "the store does not create the
   bucket/container").
6. **No delete-on-consume. Retention is TTL-based, owned by infrastructure** (S3 lifecycle rules /
   Azure Blob lifecycle management). §3 records the fan-out and redelivery reasoning.
7. **A missing or expired claim on receive fails loud**: `ClaimCheckNotFoundException` naming the
   reference, so the transport's normal failure semantics (nack → redelivery → DLQ) apply. No
   silent skip, no placeholder-processing.
8. **The claim check operates on the serialized wire body** (the string the transport would carry),
   never the typed message — that is what makes it format-agnostic across `Benzene.Xml`,
   `Benzene.MessagePack` (which Base64-armors to a string on string transports), and Avro.
9. **Default offload threshold: 192 KiB** (196,608 UTF-8 bytes of serialized body), configurable
   per route, plus an always-offload switch. Derived from the 256 KB transport family with headroom
   for message attributes/envelope, which count against the same limit. (SQS raised its own max to
   1 MiB in 2025; SNS and EventBridge did not — the smallest common limit still governs the
   default.)
10. **Encryption/at-rest posture: defer to store defaults** (S3 SSE-S3/KMS, Azure Storage SSE).
    Document it; do not build key management.

**Owner decisions needed: none.** Everything above follows from existing governed conventions or
established repo patterns. (One cosmetic default an implementer may pick without asking: the Azure
scheme string — the plan says `azblob`.)

---

## §1. Where the middleware sits, and why (read before Phase 1)

### Outbound — on the `OutboundContext` route pipeline, last before the transport converter

An outbound route (`src/Benzene.Clients/OutboundRoutingBuilder.cs`) is a
`IMiddlewarePipelineBuilder<OutboundContext>` per topic; `OutboundContext` carries the **typed**
request (`object Request`) plus a `Headers` dictionary. Serialization happens inside the
transport's context converter (e.g. `OutboundSqsContextConverter.CreateRequestAsync` calls
`_serializer.Serialize(contextIn.Request)`), and `UseSqs(...)`/`UseSns(...)`/`UseEventBridge(...)`
install that converter via `.Convert(...)`, which is **terminal**
(`ContextConverterMiddleware : ITerminalMiddleware` — nothing added after it runs). So there is no
seam that sees the serialized body *and* is still transport-neutral.

Resolution: the offload middleware sits on the `OutboundContext` pipeline and **serializes the
request itself** with an `ISerializer` (default `Benzene.Clients.JsonSerializer` — the same default
every outbound converter uses). It measures the UTF-8 byte count of that string; under threshold it
calls `next()` untouched (the serialization work is the price of measuring — accept it, don't
guess from the typed object). At/over threshold it:

1. `PutAsync`s the serialized string to the `IClaimCheckStore` and gets back the reference;
2. sets `Headers["benzene-claim-check"] = reference` — the headers channel is exactly where
   `traceparent`/`x-correlation-id` already travel, so every transport that carries those today
   (SQS/SNS message attributes, Service Bus properties, Kafka headers, EventBridge's embedded
   `_benzeneHeaders`) carries the claim reference with zero new transport work;
3. replaces `context.Request` with a tiny placeholder object (one string property carrying the
   reference, for humans reading raw queues; the **header is authoritative** — the spec will say
   the offloaded body's content is unspecified). The converter then serializes the placeholder —
   trivially small — exactly as it serializes anything else.

Wiring shape (matches `examples/AwsMesh/Shared/MeshServiceWiring.cs`'s existing route pattern —
cross-cutting middleware before the terminal converter):

```csharp
routing.Route("payments:capture", pipeline => pipeline
    .UseW3CTraceContext()
    .UseCorrelationId()
    .UseClaimCheck()          // last non-terminal step: sees the final headers/request
    .UseSqs(queueUrl));
```

**Serializer-consistency caveat (state it in docs and XML-docs, it is honest not hypothetical):**
the offload middleware's serializer must match the transport converter's. Both default to
`JsonSerializer`; a route that passes a custom `ISerializer` to its converter must pass the same
one to `UseClaimCheck(o => o.Serializer = …)`, else the stored bytes are not what the converter
would have produced. Hoisting serialization out of the converters so this coupling disappears is a
real future refactor — **out of scope here** (it would touch every outbound converter).

**Ordering:** `UseClaimCheck` goes after header-writing middleware only by convention (any
pre-converter position works — headers written later still reach the converter); what is load-
bearing is that it runs **before** the converter and after anything that mutates `Request`
(nothing does today).

### Inbound — on the transport pipeline, before `UseMessageHandlers`

Inbound transport pipelines (e.g. `src/Benzene.Aws.Lambda.Sqs/`) carry a transport context
(`SqsMessageContext`) holding the raw event; the body stays the raw serialized string until the
request mapper deserializes it inside `UseMessageHandlers`. Transport-agnostic middleware reaches
the message through per-context accessors registered in DI — exactly how
`UseW3CTraceContext<TContext>` resolves `IMessageHeadersGetter<TContext>`
(`src/Benzene.Diagnostics/W3CTraceContextExtensions.cs`) and how `UseIdempotency<TContext>`
resolves headers/body/topic getters (`src/Benzene.Idempotency/Extensions.cs`).

The hydrate middleware is `UseClaimCheck<TContext>()` in the same mold: resolve
`IMessageHeadersGetter<TContext>`; if `benzene-claim-check` is absent, `next()` untouched. If
present: `GetAsync(reference)` from the store (null → throw `ClaimCheckNotFoundException`), then
**replace the raw body before deserialization** via `IMessageBodySetter<TContext>`
(`src/Benzene.Abstractions.Messages/Mappers/IMessageBodySetter.cs`) — an abstraction that exists
today with **zero implementations**; Phase 2 supplies them per transport (the Lambda event POCOs
are mutable, so `context.SqsMessage.Body = body` is the whole implementation). If no setter is
registered for the context type, throw a descriptive error naming the transport and the missing
registration (resolve with `TryGetService`, fail loud) — never silently process the placeholder.

Wiring shape:

```csharp
aws.UseSqs(sqs => Observe(sqs)
    .UseClaimCheck()          // after tracing (hydration is traced), before the handlers
    .UseMessageHandlers(handlers, …));
```

Placement notes: after the observability prelude so the store fetch appears in the trace; before
`UseMessageHandlers` (the deserialization boundary). `UseIdempotency` may run before or after —
before is cheaper (duplicates short-circuit without a store GET) and still correct: a redelivered
offloaded message carries the same placeholder body and the same reference, so its body-hash key
is stable.

**EventBridge subtlety (Phase 2, step 3):** on EventBridge, headers and body share one physical
slot — headers are embedded as the reserved `_benzeneHeaders` object *inside* `detail`
(`EventBridgeMessageHeadersGetter.EmbeddedHeadersKey`), and the body getter returns `detail`'s raw
JSON. The EventBridge `IMessageBodySetter` must therefore **re-embed the original
`_benzeneHeaders` object into the hydrated JSON body** when setting it, mirroring the sender-side
embed rules (only when the payload is a JSON object), so header reads later in the pipeline (e.g.
the version getter at request-mapping time) still see them. SQS/SNS have no such coupling —
attributes and body are separate channels.

### What travels, end to end

```
sender:   Typed request ──serialize──> 210 KB body ──PutAsync──> s3://bucket/…/{guid}
          wire message: body = {"_benzeneClaimCheck":"s3://…"}  header benzene-claim-check = s3://…
receiver: header present ──GetAsync──> 210 KB body ──IMessageBodySetter──> normal deserialization
```

`content-type` and every other header ride unchanged, which is what keeps the mechanism
format-agnostic: the store round-trips the exact wire string, and media-format negotiation on the
receiver behaves as if the payload had never left the message.

---

## §2. Why the store abstraction is new, not the mesh artifact store

`Benzene.Mesh.Aws.S3`/`Benzene.Mesh.Azure.Blob` implement `IMeshArtifactStore`
(`Publish/TryRead` keyed by *relative path*, JSON-only content type, living in
`Benzene.Mesh.Aggregator`). Reusing it would (a) drag the mesh aggregator dependency into every
message-sending service, (b) leave reference generation and reference validation with no home, and
(c) tie two unrelated lifecycles together — mesh artifacts are a durable catalog, claim payloads
are expiring transients. So: a new `IClaimCheckStore` (put returns a store-issued reference; get
resolves one), with the S3/Blob implementations **copying the mesh stores' conventions** — 404 →
null (`AmazonS3Exception.StatusCode == NotFound` / `RequestFailedException.Status == 404`), prefix
normalization, caller-owned bucket/container, `DefaultAzureCredential` convenience overload on the
Azure side.

---

## §3. Lifecycle, retention, and failure honesty

- **Delete-on-consume is wrong** and is not implemented: SNS fans one message out to many
  consumers (the first hydrator would starve the rest), and at-least-once transports redeliver — a
  handler failure after a delete would make the retry permanently unhydratable. Nobody deletes at
  read time.
- **Retention is TTL-based expiry, owned by infrastructure**: an S3 lifecycle rule / Azure Blob
  lifecycle-management policy on the claim-check prefix. The stores do **not** create buckets or
  policies (same posture as the mesh stores and `DynamoDbIdempotencyStore`, which requires the
  consumer to enable TTL on the table); the packages *document* the required rule and Phase 6's
  Terraform shows it working.
- **TTL sizing rule (document verbatim):** the TTL must exceed the longest path from send to last
  possible consumption — queue retention plus DLQ redrive window. SQS retention maxes at 14 days;
  the example uses 14 days and says why.
- **Transactional honesty:** offload and send are two non-atomic operations, offload first. If the
  put succeeds and the send then fails, the blob is orphaned — **the TTL cleanup is the answer**,
  and the docs say so plainly rather than pretending at atomicity. If the put fails, the send
  never happens (the middleware throws — fail loud, matching the repo posture). The reverse order
  (send first) would be worse: a message pointing at a blob that never arrives.
- **Consequence to state in docs:** payloads linger in the store up to the TTL. That widens the
  at-rest footprint of message data; encryption defers to store defaults (SSE), access control to
  bucket/container IAM, and services with data-retention obligations should size the TTL
  accordingly (cross-link `docs/privacy-and-data-handling.md`).

---

## §0. Ground rules for every phase

Identical to `work/spec-mesh-tooling-implementation-plan.md` §0 — read that section; the short
form: new `src/` csproj copies a sibling's shape (`Benzene.Mesh.Azure.Blob.csproj` is the closest
model: `net10.0`, `ImplicitUsings`, `Nullable`, `AssemblyName`, `RootNamespace`,
`GenerateDocumentationFile`, `Description`; version/metadata come from `src/Directory.Build.props`);
**solution registration** in `Benzene.sln` needs the `Project(...)` entry, the full 12-line
configuration block, and a `NestedProjects` entry (fresh GUID, never reused); **tests** live in
`test/Benzene.Core.Test` (xunit + Moq, no FluentAssertions), new packages added as
`ProjectReference`s there; registration extends `IBenzeneServiceContainer` with `TryAdd*` for
overridable defaults; **every new package gets a `CLAUDE.md`** (copy the tone/length of
`src/Benzene.Idempotency/CLAUDE.md` — it is the sibling feature); docs reachable from
`docs/index.md`, and the Benzene repo's website build link-checks them. AWS SDK pins follow the
newest sibling usage (`AWSSDK.S3` is at `3.7.309.4` in `Benzene.Mesh.Aws.S3`); Azure packages
match `Benzene.Mesh.Azure.Blob`'s pins.

Verification, every phase:

```bash
dotnet build Benzene.sln -v q     # 0 errors, no new warnings
dotnet test test/Benzene.Core.Test/Benzene.Test.csproj --filter "FullyQualifiedName~ClaimCheck"
```

Phase 3 runs in the cross-language **Benzene** repo instead; Phase 6 also builds
`Benzene.Examples.sln`. Never edit conformance fixtures in any phase.

---

## Phase 1 — `Benzene.ClaimCheck` core package

**Goal:** the middleware pair, the store contract, and an in-memory store, fully tested with a
fake store — no cloud SDKs.
**Depends on:** nothing. **Effort:** M.

New project `src/Benzene.ClaimCheck/` (+ sln registration, `CLAUDE.md`). References:
`Benzene.Abstractions` (+ `.Middleware`, `.Messages` as split), `Benzene.Clients` (for
`OutboundContext` and `JsonSerializer`), `Benzene.Core.Middleware` (pipeline builder). Contents:

1. **`ClaimCheckHeaders`** — `public const string ClaimCheck = "benzene-claim-check";` the single
   definition of the default name, XML-doc'd against wire-contracts.md §2 the way
   `BenzeneWireNames` documents its names. Make the header name an option (`ClaimCheckOptions.HeaderName`,
   default this const) so the spec's "reserved names are defaults" rule holds.
2. **`IClaimCheckStore`**:
   ```csharp
   Task<string> PutAsync(string body, ClaimCheckPutContext context, CancellationToken ct = default);
   Task<string?> GetAsync(string reference, CancellationToken ct = default);
   ```
   `PutAsync` returns the store-issued reference (decision 4's URI form); `ClaimCheckPutContext`
   carries `Topic` (for key partitioning) — keep it a class so fields can be added additively.
   `GetAsync` returns null for not-found/expired (the middleware turns that into the loud
   failure); it MUST throw, not return null, for a reference outside the store's own
   configuration. XML-doc both contracts explicitly, mirroring `IIdempotencyStore`'s
   normative-remarks style.
3. **`ClaimCheckOptions`** — `ThresholdBytes` (default `192 * 1024`), `AlwaysOffload` (bool,
   default false; per-route "always offload this topic" is just `UseClaimCheck(o => o.AlwaysOffload = true)`
   on that route — routes are per-topic, so no separate topic map is needed), `HeaderName`,
   `Serializer` (`ISerializer?`, null → `JsonSerializer`, with the §1 consistency caveat in its
   XML-doc).
4. **`ClaimCheckOffloadMiddleware : IMiddleware<OutboundContext>`** — the §1 outbound behavior.
   Placeholder type `ClaimCheckPlaceholder` with one property serialized as `_benzeneClaimCheck`
   (underscore-reserved inside a payload, matching `_benzeneHeaders`'s form rationale). Tag the
   current activity when offloading: `benzene.claim-check = "offloaded"`,
   `benzene.claim-check.bytes = <n>` (naming matches `benzene.correlation-id` in
   `ActivityMiddlewareDecorator`; guard on `Activity.Current != null`, absent-not-empty like the
   existing tags).
5. **`ClaimCheckHydrateMiddleware<TContext> : IMiddleware<TContext>`** — the §1 inbound behavior.
   Resolves `IMessageHeadersGetter<TContext>` and `IMessageBodySetter<TContext>` via the resolver
   (setter with `TryGetService` + descriptive `InvalidOperationException` naming the context type
   when absent — the §1 fail-loud rule). Missing blob → `ClaimCheckNotFoundException(reference)`.
   Tags `benzene.claim-check = "hydrated"` + `.bytes`.
6. **`Extensions`** — `UseClaimCheck(this IMiddlewarePipelineBuilder<OutboundContext>, Action<ClaimCheckOptions>? = null)`
   (offload) and `UseClaimCheck<TContext>(this IMiddlewarePipelineBuilder<TContext>, Action<ClaimCheckOptions>? = null)`
   (hydrate), both resolving `IClaimCheckStore` from DI at pipeline-build time exactly as
   `UseIdempotency` does; plus `AddInMemoryClaimCheckStore(TimeSpan? timeToLive = null)`
   (reference scheme `memory://`, honoring expiry so the missing-claim path is testable —
   mirrors `AddInMemoryIdempotencyStore`'s single-instance caveat wording).
7. **Exceptions:** `ClaimCheckNotFoundException`, `ClaimCheckStoreMismatchException` (reference
   outside the configured store).
8. **Tests** (`test/Benzene.Core.Test/ClaimCheck/`): offload — under threshold: no store call, no
   header, request untouched; at/over threshold: stored body equals the serialized request, header
   set, request replaced; `AlwaysOffload` offloads a 10-byte payload; store failure propagates and
   `next()` never runs; custom serializer respected; header name override. Hydrate — no header:
   passthrough, no store call; header: setter called with the stored body; missing blob: throws
   naming the reference; no setter registered: throws naming the context type. Round-trip: offload
   through a real `MiddlewarePipelineBuilder<OutboundContext>` + hydrate against a real
   `SqsMessageContext`-style fake context with in-memory store — body identical end to end.
   (Model the middleware-test style on `IdempotencyMiddlewareTest`.)

**Acceptance:** sln builds; all new tests green; the package has no cloud SDK references; XML docs
build clean (`GenerateDocumentationFile`).

---

## Phase 2 — Inbound body setters for the AWS Lambda transports

**Goal:** `IMessageBodySetter<TContext>` implementations so hydration works on the transports the
dogfood example uses.
**Depends on:** nothing (pure transport-package additions; Phase 1 consumes them). **Effort:** S.

1. `SqsMessageBodySetter : IMessageBodySetter<SqsMessageContext>` in `src/Benzene.Aws.Lambda.Sqs/`
   — `context.SqsMessage.Body = body` (the Lambda event POCO is mutable). Register in
   `DependencyInjectionExtensions.AddSqs` with `TryAddScoped`, matching the getters.
2. `SnsMessageBodySetter : IMessageBodySetter<SnsRecordContext>` in `src/Benzene.Aws.Lambda.Sns/`
   — `context.SnsRecord.Sns.Message = body`; register in `AddSns`.
3. `EventBridgeMessageBodySetter : IMessageBodySetter<EventBridgeContext>` in
   `src/Benzene.Aws.Lambda.EventBridge/` — parse the hydrated body; when the ORIGINAL `detail`
   carried a `_benzeneHeaders` object and the hydrated body is a JSON object, re-embed that object
   before assigning (the §1 EventBridge subtlety; read `EventBridgeMessageHeadersGetter` and the
   outbound embed logic first and mirror them). Register in its DI extension.
4. **Do not** fan out to every transport in this pass. Other transports (Service Bus, Kafka,
   RabbitMQ, the standalone SQS consumer in `Benzene.Aws.Sqs`, Azure Functions triggers) follow
   the identical 5-line pattern; each package's `CLAUDE.md` gains one line saying hydration
   support = "implement + register `IMessageBodySetter<TContext>`". List them in the docs page
   (Phase 7) as "supported when the setter exists", with the fail-loud error as the guide.
5. **Tests:** one small test per setter in the transport's existing test folder
   (`test/Benzene.Core.Test/Aws/...`); the EventBridge one asserts `_benzeneHeaders`
   round-trip survival.

**Acceptance:** `UseClaimCheck()` on an SQS/SNS/EventBridge Lambda pipeline hydrates in a
pipeline-level test; EventBridge preserves embedded headers; unregistered-transport error message
names the missing registration.

---

## Phase 3 — Spec repo: the `benzene-claim-check` add-on convention *(cross-repo)*

**Goal:** the wire contract is documented where other language ports look, because per AGENTS.md an
observable contract is a spec change. Kept minimal: header name + reference format + hydration
semantics. **Repo:** the cross-language **Benzene** repo. **Depends on:** decisions block only —
can run parallel with Phases 1–2. **Effort:** S.

1. **`docs/specification/wire-contracts.md` §2 header table**, new row in table position/style:
   `benzene-claim-check` | **C** | both | one-sentence meaning with a pointer to the new
   subsection.
2. **New short subsection** (after the header table's notes, sibling to the naming discussion):
   *Claim check (add-on).* Content, tersely: written by an optional outbound middleware when a
   payload exceeds a configured size; the value is an **opaque URI-form reference**
   `scheme://location/key` issued by the sender's payload store; the message body of an offloaded
   message is **unspecified** — a consumer MUST NOT interpret it and MUST treat the header as
   authoritative; a consumer with the add-on wired MUST replace the body with the stored content
   verbatim before deserialization (all other headers, including `content-type`, apply to the
   hydrated body); a consumer MUST resolve references only through its own configured store and
   MUST fail the message — never skip it — when the reference cannot be resolved or lies outside
   that store; deletion at read time is forbidden (fan-out); retention is store-side expiry agreed
   between the communicating services. Porting implication, one sentence: Tier C means ports adopt
   on their own schedule; a service that offloads is only interoperable with consumers that have
   wired the add-on and share store access — that is an explicit deployment agreement, exactly
   like any Tier C middleware.
3. **No conformance fixture** (matches the other Tier C headers) and **no changes to
   `transport-bindings.md`** — the header rides the existing per-transport metadata mappings.
4. Run the website generator (`dotnet run --project website/generator -- --out website/dist`) —
   0 broken-link warnings.

**Acceptance:** the row and subsection render; tier table untouched otherwise; website build
clean; benzene-dotnet's Phase 7 docs can cite the spec section by anchor.

---

## Phase 4 — `Benzene.ClaimCheck.Aws.S3`

**Goal:** the production store, AWS-first per repo pattern. **Depends on:** Phase 1.
**Effort:** S–M.

1. New project `src/Benzene.ClaimCheck.Aws.S3/` (+ sln, `CLAUDE.md`): `S3ClaimCheckStore :
   IClaimCheckStore` over `IAmazonS3` (ctor: client, bucket, optional prefix — copy
   `S3MeshArtifactStore`'s prefix normalization).
   - `PutAsync`: key = `{prefix}{topic}/{yyyy/MM/dd}/{guid}` (topic verbatim — S3 keys allow `:`;
     date segment makes the lifecycle rule's effect auditable), `ContentType` from a put-context
     field if later added, else `application/octet-stream`; returns `s3://{bucket}/{key}`.
   - `GetAsync`: parse + validate the reference (scheme `s3`, bucket AND prefix match this store's
     configuration → else `ClaimCheckStoreMismatchException`); 404 → null (copy the mesh store's
     exception filter). Forward `CancellationToken`s to the SDK (the Idempotency convention).
2. `Extensions.AddS3ClaimCheckStore(bucket, prefix = "claim-checks/")` — registers
   `IClaimCheckStore` resolving `IAmazonS3` from DI (the consumer registers the client), exactly
   the `AddDynamoDbIdempotencyStore` shape. XML-doc the infra contract: bucket exists already; a
   lifecycle expiration rule on the prefix is the retention mechanism; TTL must satisfy §3's
   sizing rule; SSE per bucket settings.
3. **Unit tests** (`test/Benzene.Core.Test/ClaimCheck/S3/S3ClaimCheckStoreTest.cs`) with
   `Mock<IAmazonS3>(MockBehavior.Strict)`, modeled on `DynamoDbIdempotencyStoreTest`: put issues
   the expected key shape and returns the reference; get round-trips content; 404 → null; foreign
   bucket/scheme/prefix → mismatch exception; token forwarded.
4. **Integration test** (`test/Benzene.Integration.Test/ClaimCheck/`): extend the LocalStack
   compose file (`Fixtures/Files/Sqs/sqs-docker-compose.yaml`) `SERVICES=sqs` → `sqs,s3`; one test
   in the `DockerEmulatorCollection` doing the full round trip — outbound pipeline with
   `UseClaimCheck().UseSqs(...)` sending an oversized payload, the standalone SQS consumer
   pipeline with `UseClaimCheck()` hydrating it (this also exercises the `Benzene.Aws.Sqs`
   consumer body setter — add it here if Phase 2 step 4 deferred it; it is the same 5 lines).

**Acceptance:** unit + integration tests green (integration proves a >256 KB logical payload
traverses real SQS); package packs; `CLAUDE.md` documents the lifecycle-rule contract.

---

## Phase 5 — `Benzene.ClaimCheck.Azure.Blob`

**Goal:** the Azure store ("or Blob Storage for Azure" — owner's words). **Depends on:** Phase 1
(pattern-follows Phase 4). **Effort:** S.

1. New project `src/Benzene.ClaimCheck.Azure.Blob/` (+ sln, `CLAUDE.md`): `BlobClaimCheckStore :
   IClaimCheckStore` over `BlobContainerClient`; reference `azblob://{container}/{key}`; 404 via
   `RequestFailedException.Status == 404` → null; same key layout and validation as S3.
2. `Extensions.AddBlobClaimCheckStore(...)`: one overload over a caller-supplied
   `BlobContainerClient`, one convenience overload (service `Uri` + container +
   `DefaultAzureCredential`) — mirror `AddMeshAggregatorWithBlob`'s two shapes and its
   `CLAUDE.md`'s managed-identity/RBAC note (Storage Blob Data Contributor). Retention =
   Blob lifecycle management delete rule on the prefix; document per §3.
3. **Tests:** `BlobClaimCheckStoreTest` mocking `BlobContainerClient`/`BlobClient` (Azure SDK
   members are virtual for exactly this). Same case list as S3. No Azurite integration test in
   this pass — the emulator fixtures are heavy and the S3 integration test already proves the
   middleware end-to-end; note this explicitly in the test folder rather than leaving it implied.
4. Wire the pipeline story for one Azure transport in the docs (Service Bus standard 256 KB is the
   motivating limit; Queue Storage's 64 KB makes a lower per-route threshold the documented
   example of `ThresholdBytes` tuning). Body setters for Azure transports follow Phase 2 step 4's
   pattern — add `ServiceBusMessageContext`'s setter here if the docs example claims it works,
   with its registration + test; claim nothing undemonstrated.

**Acceptance:** tests green; package packs; docs claims match shipped setters.

---

## Phase 6 — Dogfood: oversized payload in `examples/AwsMesh`

**Goal:** a real service pair exercises the feature on deployed infrastructure, minimally.
**Depends on:** Phases 1, 2, 4. **Effort:** S–M.

The candidate is the existing **orders → payments** SQS send (`payments:capture`, fire-and-forget
via the generated client — `examples/AwsMesh/Orders/Handlers/OrderHandlers.cs`): already
transport-routed in `MeshServiceWiring.ConfigureServices`, already consumed by Payments' SQS
pipeline in `MeshServiceWiring.Configure`.

1. **Wiring:** `MeshServiceWiring` gains an opt-in (e.g. a bool/env-var on `OutboundSend` or a
   `claimCheckBucket` parameter — pick the smallest change that keeps other services untouched):
   Orders' `payments:capture` route becomes `…UseCorrelationId().UseClaimCheck().UseSqs(target)`;
   the shared inbound SQS pipeline gains `.UseClaimCheck()` when the bucket env var is present.
   Both services register `AddS3ClaimCheckStore(bucket)` + a lazy `IAmazonS3` singleton (copy the
   existing lazy SQS-client pattern).
2. **Payload:** extend the capture-payment flow with an oversized field (e.g. an attached
   "supporting document" blob of ~300 KB in the demo request) so the offload genuinely triggers —
   keep it honest (the README explains it exists to demonstrate the claim check) and keep the
   under-threshold path exercised by the normal small sends.
3. **Terraform** (`examples/AwsMesh/deploy/main.tf`): a dedicated `aws_s3_bucket.claim_checks`
   + `aws_s3_bucket_lifecycle_configuration` expiring objects after **14 days** (comment: SQS max
   retention — §3's sizing rule), public-access-block, IAM: Orders put + get, Payments get, on the
   bucket path; `CLAIM_CHECK_BUCKET` env var on both lambdas. Keep it separate from
   `aws_s3_bucket.artifacts` (mesh catalog ≠ payload transients; different IAM audiences).
4. **README** (`examples/AwsMesh/README.md`): a short section — why the pattern exists (256 KB),
   what to look for (the `benzene.claim-check` tags on the order → payment trace in X-Ray, the
   dated objects in the bucket, the lifecycle rule), and the orphaned-blob honesty note.
5. Contract note: the offloaded payload's *schema* is unchanged — `contracts/payments.spec.json`
   and the generated client are untouched by claim-checking (the client sends the typed request;
   offload happens below it). Say this in the README; it is the point of doing it in middleware.

**Acceptance:** `Benzene.Examples.sln` builds in CI; a deployed run shows an offloaded capture
message hydrated by Payments (trace tags present); small messages still bypass the store; terraform
plan is self-contained.

---

## Phase 7 — Docs

**Goal:** discoverable, honest documentation. **Depends on:** Phases 1–5 (refresh after 6).
**Effort:** S.

1. **`docs/claim-check.md`**: the problem (real limits table: SQS 256 KB default/1 MiB since 2025,
   SNS 256 KB, EventBridge 256 KB, Service Bus 256 KB standard / 100 MB premium, Azure Queue
   Storage 64 KB, Kafka ~1 MB broker default); the middleware pair with both wiring snippets; the
   threshold/AlwaysOffload options and the serializer caveat; store setup for S3 and Blob
   including the lifecycle-rule retention contract and §3's TTL sizing rule verbatim; the
   fail-loud semantics (missing claim → DLQ path); fan-out reasoning for no-delete-on-consume; the
   at-rest/encryption posture; supported transports = "any with `IMessageHeadersGetter` +
   `IMessageBodySetter`" with the shipped list and the one-line recipe for adding a transport.
   Link the spec section (Phase 3) as the cross-language contract. Link from `docs/index.md`
   (Main Themes, near Resilience/Rate Limiting) and from `docs/capability-matrix.md` if it has a
   payload-size row (read it first).
2. **`docs/reference/middleware.md`**: both `UseClaimCheck` entries in table style.
   **`docs/reference/packages.md`**: three package rows.
3. **`CHANGELOG.md`**: one entry per repo convention.

**Acceptance:** every shipped flag/option documented; no dead links (Benzene repo website build
with `--dotnet-docs` pointing here reports 0 warnings).

---

## Explicitly out of scope (recorded so they aren't invented mid-implementation)

- **Compression** — *parked, worth a future note*: gzip-before-threshold would let many payloads
  avoid offloading entirely and pairs naturally with this middleware (a `Content-Encoding`-style
  header, same tier). Park it; do not bolt it on here.
- **Partial hydration / lazy streams** — handing the handler a stream or deferring the store GET
  until the payload is touched. The request-mapping pipeline is string-bodied today; do not fight
  it.
- **Cross-language port implementations** — Phase 3 documents the contract; benzene-go/-typescript/
  -python pick it up on their own schedule (Tier C). Nothing in this plan blocks on them.
- **Interop with AWS's SQS Extended Client Library / Azure's equivalent** — different wire format
  (their reference rides in the body, not a header). Not a goal; note it in docs so nobody expects
  it.
- **Hoisting serialization out of the outbound transport converters** (removes the §1 serializer
  caveat) — a real refactor touching every converter; its own future design.
- **Delete-on-consume / reference-counting cleanup modes** — §3's reasoning stands; TTL only.
- **Store-side encryption/key management beyond store defaults**, and automatic bucket/container/
  lifecycle-rule creation — infra owns infra, consistently with every existing store package.
- **Response-path claim checking** (oversized replies on request/response transports) — the
  request path is where the transport limits bite; revisit if a real need appears.

---

## Suggested agent task slicing

| Task | Phases | Parallel-safe with |
|---|---|---|
| T1 | Phase 1 (core package) | T3 |
| T2 | Phase 2 (body setters) | T3; after T1 for its pipeline-level tests |
| T3 | Phase 3 (spec repo) | T1, T2 — different repo |
| T4 | Phase 4 (S3 store + integration test) | T5, after T1 (integration slice also wants T2) |
| T5 | Phase 5 (Azure Blob store) | T4, after T1 |
| T6 | Phase 6 (AwsMesh dogfood) | after T1, T2, T4 |
| T7 | Phase 7 (docs) | last; refresh after T6 |

Each task: read §0–§3 + its phase; verify cited files before editing; build + test per §0; commit
per phase with a conventional message (`feat(claim-check): …`, `docs: …`, `spec: …` in the Benzene
repo); report what was verified vs assumed.
