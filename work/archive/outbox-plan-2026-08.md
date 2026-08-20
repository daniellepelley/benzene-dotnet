> ARCHIVED 2026-08-20: actioned; shipped as `src/Benzene.Outbox` (+ `.DynamoDb`, `.EntityFramework`), dogfooded in `examples/AwsMesh`; the release-posture question is extracted to `../1.0-release-plan.md` §9.

# Transactional Outbox — Implementation Plan

**Status:** **SHIPPED** (verified against source 2026-08-20). Every phase below has been implemented: `src/Benzene.Outbox/`
(`OutboxMiddleware`, `IOutboxStore`, `OutboxDispatcher`/`OutboxDispatcherWorker`), `src/Benzene.Outbox.DynamoDb/` and
`src/Benzene.Outbox.EntityFramework/`, exercised by `test/Benzene.Core.Test/Outbox/` and demonstrated in
`examples/AwsMesh/Orders/Handlers/OutboxHandlers.cs`. The document is kept here, rather than archived,
because the shipped code and package `CLAUDE.md`s cite this path as the design of record - read it as
the rationale behind what exists, not as work outstanding.
**Date:** 2026-08-13
**Audience:** implementation agents. Each phase below is written to be picked up as a
self-contained task by an agent without further product decisions. Do the phases in order unless a
phase's "Depends on" says otherwise.
**Why:** the produce side has no way to make "save state + publish message" atomic. Consume-side
dedup exists (`Benzene.Idempotency*`), the event log exists (`Benzene.EventSourcing*`), but nothing
prevents the classic failure: state saved, publish lost — or published, state rolled back. A live
example of the hole ships in this repo:
`examples/AwsMesh/Orders/Handlers/OrderHandlers.cs` sends `payments:capture` and `order:placed`
best-effort in try/catch after creating the order — if a send fails, the order exists and the
downstream services never hear.

**Decisions already made (do not re-litigate):**
1. Benzene **ships an outbox** as first-class packages. This supersedes
   `docs/cookbooks/transactional-outbox.md`'s recorded stance ("Benzene doesn't ship an outbox —
   … application territory"); that cookbook becomes the guide to the shipped packages (Phase 5).
2. The handler-facing send API **stays `IBenzeneMessageSender.SendAsync`** — the outbox is opt-in
   via registration/route configuration, never a new send API.
3. Package shape mirrors the established store-family pattern (`Benzene.Idempotency` +
   `Benzene.Idempotency.DynamoDb`): core abstraction package **`Benzene.Outbox`**, store packages
   **`Benzene.Outbox.DynamoDb`** (first) and **`Benzene.Outbox.EntityFramework`** (second). All
   other stores (Cosmos, Redis, …) are explicitly deferred.
4. Delivery is **at-least-once**; the outbox and `Benzene.Idempotency` are designed as a pair
   (produce-side durability ⇒ consume-side dedup). No exactly-once claims anywhere.
5. Poison envelopes are **parked** (max attempts → `Parked` status, kept for operator inspection).
   The repo has no dead-letter story and this plan does **not** invent one.
6. **Spec impact: none.** See §3 — the outbox is .NET-internal plumbing; nothing here may change a
   wire shape, mint a `benzene:*` topic, or touch a conformance fixture.

---

## §0. Ground rules for every phase — READ FIRST

**Verify before you edit.** File paths and APIs cited here were verified on 2026-08-13; the repo
moves fast. Before changing a file, read it; if a cited symbol has moved/renamed, follow it — the
*intent* of the step is authoritative.

**Project conventions (from `AGENTS.md` + `work/archive/spec-mesh-tooling-implementation-plan-2026-08.md` §0):**
- New `src/` csproj: copy a sibling (e.g. `src/Benzene.Idempotency/Benzene.Idempotency.csproj`):
  `TargetFramework=net10.0`, `ImplicitUsings`, `Nullable`, `AssemblyName`, `RootNamespace`,
  `GenerateDocumentationFile=true`. Version/metadata come from `src/Directory.Build.props` — do not
  set per-project. AWS SDK packages pin `3.7.301.4`; EF Core pins `10.0.9` (match
  `src/Benzene.HealthChecks.EntityFramework/Benzene.HealthChecks.EntityFramework.csproj`).
- **Solution registration** (`Benzene.sln`): a new project needs (a) a `Project(...)` entry before
  `Global`, (b) the full Debug/Release × AnyCPU/x64/x86 configuration block in
  `ProjectConfigurationPlatforms` (copy an existing project's 12 lines, swap the GUID), (c) a
  `NestedProjects` entry if it belongs in a solution folder. Fresh GUID; never reuse one.
- **Tests** live in `test/Benzene.Core.Test/Benzene.Test.csproj` (xunit + Moq, **no
  FluentAssertions**), organized by folder — this plan's tests go under
  `test/Benzene.Core.Test/Outbox/`. Each new src package gets a `ProjectReference` there.
- Registration extends `IBenzeneServiceContainer` (never `IServiceCollection`); `TryAdd*` for
  defaults a caller may override; injectable clock `Func<DateTimeOffset>` for anything time-based
  (see `DynamoDbIdempotencyStore`'s constructor for the house pattern).
- **Every new package gets a `CLAUDE.md`** (copy the tone/length of
  `src/Benzene.Idempotency/CLAUDE.md`, including an honest "capability boundary" section).
- Middleware-to-later-step handoff uses a **scoped DI holder**, never a context property — read
  `src/Benzene.Abstractions.Middleware/CLAUDE.md`'s "Context purity" section before Phase 1; the
  outbox leans on it twice (the staging buffer, the dispatch marker).
- Docs: Markdown in `docs/`, cookbooks in `docs/cookbooks/`, everything reachable from
  `docs/index.md`. The website build in the `benzene` repo link-checks these.

**Verification discipline (every phase):**
```bash
dotnet build Benzene.sln -v q          # 0 errors (warnings pre-exist; add none)
dotnet test test/Benzene.Core.Test/Benzene.Test.csproj --filter "FullyQualifiedName~Outbox"
```
plus the phase's own acceptance checks. Commit per phase with a conventional message
(`feat(outbox): …`, `docs: …`).

**Outbound-pipeline primer (read once — every phase builds on it):**
- `src/Benzene.Clients/IBenzeneMessageSender.cs` —
  `SendAsync<TRequest,TResponse>(topic, request, headers?)`; registered **scoped** by
  `AddOutboundRouting` (`src/Benzene.Clients/DependencyInjectionExtensions.cs`).
- `src/Benzene.Clients/OutboundRoutingBuilder.cs` — one `IMiddlewarePipeline<OutboundContext>` per
  topic via `.Route(topic, pipeline => …)`.
- `src/Benzene.Clients/OutboundContext.cs` — `Topic`, `Request` (object), `Headers` (mutable,
  copied per send), settable `Response`.
- Transport converters (`OutboundSqsContextConverter` in `src/Benzene.Clients.Aws.Sqs/`, SNS/
  EventBridge siblings) are **terminal** — added via `.Convert(...)`, they never call `next`, they
  serialize `Request` with the route's `ISerializer` and set `Response` to an
  `IBenzeneResult<Void>`. Anything that must run "before the send" is a middleware placed earlier
  in the same route (`UseW3CTraceContext()`, `UseCorrelationId()`, `UseRetry(...)`).
- `examples/AwsMesh/Shared/MeshServiceWiring.cs` shows the real-world wiring: per-topic routes,
  trace/correlation stamping before the transport.

---

## §1. The gap, verified

- A handler that must "write state AND send a message" today does two independent writes. The
  outbound pipeline sends inline; a crash or transport failure between the two loses one side.
  `CreateOrderMessageHandler` (`examples/AwsMesh/Orders/Handlers/OrderHandlers.cs`) documents this
  live: both downstream sends are wrapped in swallow-and-log try/catch.
- `docs/cookbooks/transactional-outbox.md` walks users through hand-rolling exactly the machinery
  this plan productizes (outbox table, same-DbContext publisher, polling relay) — proof of demand
  and a design already aligned with this plan's shape.
- `Benzene.ResponseEvents`' default `IResponseEventPublisher` publishes through
  `IBenzeneMessageSender`, so response-as-event flows **inherit the outbox for free** the moment
  the event topic's route opts in — no integration code needed (state this in Phase 5's docs).

## §2. Design (decided — implement as written)

### 2.1 The outbox is outbound-route **middleware**, not a sender decorator

`UseOutbox()` is an `IMiddleware<OutboundContext>` added to a route's pipeline, placed after the
cross-cutting stamping middleware and before the terminal transport converter:

```csharp
routing.Route("payments:capture", p => p
    .UseW3CTraceContext().UseCorrelationId()
    .UseOutbox()                 // ← capture point
    .UseSqs(queueUrl));
```

Why middleware and not an `IBenzeneMessageSender` decorator:
- **House style.** The clients redesign (`work/archive/benzene-clients-redesign-plan-2026-07.md`) deliberately
  deleted the decorator-chain mechanism in favor of pipeline middleware; re-introducing a decorator
  seam for this one feature would fork the model back.
- **Per-topic opt-in falls out of `Route(...)`** — no new topic-set configuration surface. A
  service outboxes exactly the routes it marks; unmarked routes behave exactly as today.
- **Capture happens after header stamping**, so the persisted envelope carries the business-time
  `traceparent`/`x-correlation-id` — the relay replays the original context instead of its own.
- **Dispatch reuses the same route pipeline** (see 2.4), so transport choice, retry middleware and
  health-check registration are wired once, not twice.
- Placement rule (document it): `UseOutbox()` goes after `UseW3CTraceContext()`/
  `UseCorrelationId()` and **before** `UseParallel(...)` if present — so a fanned-out route is
  captured once and fans out at dispatch time.

**Capture semantics:** when no dispatch marker is present (2.4), the middleware is terminal — it
builds an `OutboxEnvelope` from the context (serializing `Request` with the scope's `ISerializer`),
hands it to the configured write mode (2.3), sets
`context.Response = BenzeneResult.Accepted<Void>()`, and does **not** call `next`. A persistence
failure propagates as an exception — the caller sees the failure, exactly as a transport failure
today; never silent loss.

**Constraint (document loudly):** an outboxed route is fire-and-forget only — callers must use
`SendAsync<TRequest, Void>`. A caller requesting any other `TResponse` gets the existing
`OutboundResponseTypeMismatchException` from `DefaultBenzeneMessageSender` (same behavior as
SQS/SNS routes today, `src/Benzene.Clients/CLAUDE.md`). Request/response topics cannot be outboxed
— a deferred send has no response to give.

### 2.2 The envelope

```csharp
public sealed class OutboxEnvelope
{
    public string Id { get; }                    // Guid "N"; also the default idempotency key (2.6)
    public string Topic { get; }
    public string Payload { get; }               // Request serialized by the route scope's ISerializer
    public string PayloadType { get; }           // assembly-qualified name, for dispatch-time deserialization
    public IReadOnlyDictionary<string, string> Headers { get; }   // post-stamping snapshot
    public DateTimeOffset CreatedAtUtc { get; }
    public int AttemptCount { get; }
    public DateTimeOffset? NextAttemptAtUtc { get; }
    public OutboxStatus Status { get; }          // Pending | Dispatched | Parked
    public string? LastError { get; }
}
```

Honest caveat to document: the payload must round-trip through the registered `ISerializer`
(deserialize to `PayloadType`, re-serialize at dispatch) — the same constraint
`Benzene.EventSourcing` already imposes on stored events. Dispatch runs in the same service, so the
payload type is always loadable; a renamed payload type strands pending envelopes (park path).

### 2.3 Two write modes — be honest about what each guarantees

- **`Immediate` (default): store-and-forward.** Capture writes the envelope straight to
  `IOutboxStore.AddAsync`. Guarantees: the send survives process death and transport outages, is
  retried with backoff, and is never silently swallowed (replaces the try/catch pattern outright).
  Does **not** make the send atomic with the handler's own state write — say so in every doc.
- **`Transactional`: the atomic story.** Capture stages the envelope into a scoped
  `IOutboxStage`; the **application's own commit** persists it together with the state write:
  - **DynamoDb** — the handler commits through the outbox-aware unit of work:
    `IDynamoDbOutboxTransaction.CommitAsync(applicationItems)` issues ONE `TransactWriteItems`
    containing the app's `TransactWriteItem`s plus one `Put` per staged envelope. All-or-nothing.
    Bounded by DynamoDB's 100-item transaction limit (shared with the app's items — document, and
    throw a clear error when exceeded, mirroring `DynamoDbEventStore`'s note).
  - **EF Core** — the outbox entity lives in the **application's `DbContext`**; the stage adds rows
    to the scoped `DbContext`'s change tracker (no `SaveChanges`), and the handler's own
    `SaveChangesAsync` commits state + envelopes in one database transaction. This is exactly the
    shape `docs/cookbooks/transactional-outbox.md` teaches today.
  - An envelope staged but never committed is discarded with the scope — consistent by
    construction (no state written either). The stage logs a warning at scope disposal if
    non-empty and uncommitted.
  - **Cosmos** (deferred, recorded for honesty): `TransactionalBatch` only spans one container
    **and one partition key** — outbox items would have to share the application document's
    partition. Real, but constraint-laden; deferred until someone needs it (see Phase 6).

### 2.4 Dispatch — one engine, reused by every relay host

Core `IOutboxDispatcher` (engine, host-agnostic):
- `RunOnceAsync(ct)` — claim a due batch (`IOutboxStore.ClaimDueAsync(batchSize, lease)`),
  dispatch each, return counts (dispatched/rescheduled/parked). Also deletes dispatched envelopes
  past retention (`DeleteDispatchedBeforeAsync`) — stores with native TTL (DynamoDb) implement
  that as a no-op.
- `DispatchOneAsync(envelopeId, ct)` — claim-and-dispatch a single envelope (the
  stream-triggered path).

Dispatching an envelope: create a scope via `IServiceResolverFactory.CreateScope()` (the
codebase's per-message pattern), set the scoped **`OutboxDispatchScope`** holder (marker + the
stored headers), deserialize `Payload` to `PayloadType`, and call the scope's
`IBenzeneMessageSender.SendAsync<object, Void>(topic, payload, headers)`. The route pipeline runs
as normal; `OutboxMiddleware` sees the marker and **passes through** instead of capturing — after
first re-applying the stored headers onto `context.Headers` (stored values win), so a relay host
with its own ambient `Activity` cannot overwrite the captured business-time trace context.
Outcome: success → `MarkDispatchedAsync`; failure/throw → `RescheduleAsync` with exponential
backoff, or `ParkAsync` once `AttemptCount` reaches `MaxAttempts`.

Claims are **conditional** (lease-based, atomic per store — same discipline as
`IIdempotencyStore.TryClaimAsync`), so a stream-triggered dispatcher and a sweeper racing the same
envelope cannot both send it *except* across a crash-after-send — which is the inherent
at-least-once window. Never claim better than at-least-once.

### 2.5 Relay per host

- **`Benzene.HostedService` / `Benzene.SelfHost`:** core ships `OutboxDispatcherWorker :
  IBenzeneWorker` — a poll loop (`RunOnceAsync` + delay, default 5s) started/stopped by the host.
  Wiring is the existing glue: `Benzene.HostedService`'s adapter for the generic host, or the
  worker registered directly on a self-host. No new hosting package.
- **AWS Lambda (first-class problem, decided):** no background thread exists, so three options
  were evaluated:
  1. **DynamoDB Streams → dispatch handler** *(recommended primary)*. The outbox table has
     streams enabled; the service's own Lambda consumes `{outboxTable}:INSERT` through the
     existing `Benzene.Aws.Lambda.DynamoDb` adapter and calls `DispatchOneAsync` for the inserted
     envelope. Near-real-time, zero idle cost, no new transport code — the adapter's CLAUDE.md
     already names "outbox" as an intended use. Caveats to document: INSERT fires once, so
     *retries* need the sweeper (option 2); the adapter's deliberate stop-at-first-failure batch
     semantics mean a persistently failing dispatch blocks its shard until the event source
     mapping's retry/age limits kick in — configure `maximum_retry_attempts` and rely on the
     sweeper as redrive; streams retain 24h.
  2. **Scheduled sweep** *(required backstop; also the minimal standalone option)*. An EventBridge
     schedule invokes the service Lambda with a message routed to a 3-line app handler (e.g.
     `[Message("outbox:sweep")]` on the direct-invoke surface) that calls `RunOnceAsync`. Handles
     retries, parking, and cleanup. **Deliberately an app-chosen topic — never `benzene:*`**
     (reserved topics are spec surface, §3). A deployment that can tolerate schedule-granularity
     latency can run sweep-only and skip streams entirely.
  3. **Dispatch-on-invoke piggyback** *(rejected as primary)*. Flushing pending envelopes at the
     end of each invocation couples drain latency to traffic (an idle service never drains its
     backlog), extends every invocation's billed duration, and post-response background work is
     impossible under Lambda's freeze model. Recorded as a possible future *optimization*
     ("inline first-attempt after commit"), not a relay — deferred, Phase 6.
  **Recommendation shipped as the documented default: streams dispatch (latency) + a low-frequency
  scheduled sweep (retry/park/cleanup).** Phase 3 dogfoods exactly this pair.
- **Azure Functions / other FaaS:** same shape (Cosmos change feed / timer trigger) — deferred
  with the Cosmos store, Phase 6.

### 2.6 Designed as a pair with `Benzene.Idempotency`

The outbox gives at-least-once ⇒ consumers need dedup. Two concrete couplings:
- At capture, if `Headers` lacks `idempotency-key` (the exact
  `Benzene.Idempotency.IdempotencyDefaults.HeaderName` constant), the middleware stamps the
  envelope `Id` into it (option `StampIdempotencyKey`, default **true**). A consumer running
  `UseIdempotency()` with the default `HeaderOrBodyHashIdempotencyKeyStrategy` then dedups relay
  redeliveries with zero configuration — the two packages click together by default. The string is
  duplicated as a const in `Benzene.Outbox` (do not add a package reference for one string; note
  the mirror in both CLAUDE.mds).
- Docs pair them explicitly everywhere: outbox cookbook → idempotency cookbook and back.

### 2.7 Delivery semantics (state verbatim in docs)

- **At-least-once**, end to end. Duplicates possible on the crash-after-send window and on
  stream+sweeper overlap; consumers dedup (2.6).
- **No ordering guarantee** across envelopes: stream shards order by partition key (envelope id —
  random), the sweeper orders by `CreatedAtUtc` best-effort only, and retries reorder anyway.
  Per-key ordered dispatch is deferred (Phase 6) — do not imply it exists.
- **Retention:** `Dispatched` envelopes are deleted after `RetentionPeriod` (default 7 days) —
  DynamoDb via native TTL on an `expiresAt` attribute set at dispatch time (the
  `DynamoDbIdempotencyStore` pattern), EF via the sweep's cleanup delete. `Parked` envelopes are
  **never auto-deleted** — they are the operator's evidence; deleting or re-pending them is a
  manual store operation for now (ops tooling deferred, Phase 6).
- **Poison:** `MaxAttempts` (default 10, exponential backoff base 30s, cap 1h) → `Parked` with
  `LastError`. Parked is terminal-until-human. **No dead-letter forwarding** — the repo has no
  dead-letter story and this plan does not add one.

## §3. Spec impact: none — and what would violate that

The outbox is .NET-internal plumbing. A relayed message on the wire is produced by the **same
route pipeline, same transport converter, same serializer** as an inline send — byte-identical
payload and headers, plus at most the `idempotency-key` header (an existing .NET-side convention
header inside the open, app-extensible header map; not a spec-enumerated name). Checked against
`AGENTS.md`'s rule ("wire format, status vocabulary, mesh shapes … is a spec change"):
- **Do not** mint any `benzene:*` topic (no `benzene:outbox:sweep`); reserved endpoints are spec
  surface. The sweep handler's topic is app-chosen (§2.5).
- **Do not** touch `test/conformance-fixtures/**` in any phase.
- The eventual mesh observability surface (outbox depth etc., Phase 6) **would** be a mesh-contract
  change — that is exactly why it is deferred behind a separate product decision.
- Nothing else found that risks the boundary: capture returns `BenzeneResultStatus.Accepted`, an
  existing status vocabulary member (`src/Benzene.Results/BenzeneResult.cs`).

---

## Phase 1 — `Benzene.Outbox` (core abstractions, middleware, dispatcher, in-memory store)

**Goal:** the complete host-agnostic outbox: capture middleware, envelope, store/stage seams,
dispatch engine, polling worker, in-memory store — usable end to end in one process.
**Depends on:** nothing. **Effort:** M–L. *Unlocks every later phase.*

Steps:
1. **New project `src/Benzene.Outbox/`** per §0 (csproj copied from `Benzene.Idempotency`; register
   in `Benzene.sln`; `ProjectReference` from `test/Benzene.Core.Test/Benzene.Test.csproj`).
   References: `Benzene.Clients` (OutboundContext, sender, pipeline builder extensions),
   `Benzene.Abstractions` (+`.Middleware`), `Benzene.Results`. No third-party dependencies.
2. **Data model:** `OutboxEnvelope` (§2.2), `OutboxStatus`, `OutboxOptions` (`MaxAttempts`=10,
   backoff base/cap, `RetentionPeriod`=7d, `BatchSize`=25, `ClaimLease`=2min, `PollInterval`=5s,
   `StampIdempotencyKey`=true, `WriteMode` `Immediate|Transactional`).
3. **Seams:** `IOutboxStore` — `AddAsync(envelopes)`, `ClaimDueAsync(batchSize, lease)`
   (atomic/conditional per store — document the contract as hard requirement, mirroring
   `IIdempotencyStore`), `MarkDispatchedAsync(id)`, `RescheduleAsync(id, attemptCount, delay,
   error)`, `ParkAsync(id, error)`, `DeleteDispatchedBeforeAsync(cutoff)`. `IOutboxStage`
   (scoped) — `StageAsync(envelope)`; core ships `BufferedOutboxStage` (in-memory list +
   `DrainStaged()`, warn-on-dispose-if-undrained), registered scoped by `AddOutbox`.
   All methods take `CancellationToken` and forward it (idempotency-package convention).
4. **`OutboxMiddleware : IMiddleware<OutboundContext>`** + `UseOutbox(this
   IMiddlewarePipelineBuilder<OutboundContext>, Action<OutboxOptions>? configure = null)` —
   capture/pass-through semantics per §2.1/§2.4, idempotency-key stamping per §2.6, serializer
   resolved from the scope (`ISerializer`, as `DefaultBenzeneMessageSender` already does).
5. **`OutboxDispatchScope`** — scoped holder (marker + stored headers) per the
   `Benzene.Abstractions.Middleware/CLAUDE.md` pattern; `TryAddScoped` in registration.
6. **`OutboxDispatcher : IOutboxDispatcher`** (§2.4) and **`OutboxDispatcherWorker :
   IBenzeneWorker`** (poll loop; graceful stop finishes the in-flight `RunOnceAsync`, mirroring
   how existing workers drain). Confirm `IBenzeneWorker`'s home
   (`Benzene.Abstractions.Pipelines`) and reference accordingly.
7. **`InMemoryOutboxStore`** — dictionary + lock, honest single-process caveat (copy the framing
   from `InMemoryIdempotencyStore`).
8. **DI:** `AddOutbox(Action<OutboxOptions>?)` (options + stage + dispatch scope + dispatcher),
   `AddInMemoryOutboxStore()`, `AddOutboxDispatcherWorker()`. `TryAdd` where a caller may override.
9. **Tests** (`test/Benzene.Core.Test/Outbox/`): middleware captures and short-circuits with
   `Accepted` (build a real route via `AddOutboundRouting` + `UseOutbox` + a recording terminal
   middleware, the `DefaultBenzeneMessageSenderTest` technique); non-`Void` `TResponse` throws the
   mismatch exception; dispatch marker passes through and stored headers win over ambient stamps;
   idempotency-key stamped only when absent; store failure propagates; in-memory store claim/
   lease/reschedule/park/cleanup semantics; dispatcher run-once success/reschedule/park paths and
   `DispatchOneAsync` claim-refused path; worker starts/stops cleanly.
10. **`src/Benzene.Outbox/CLAUDE.md`** — include the §2.3 mode-honesty and §2.7 semantics
    verbatim-in-spirit, and the `IdempotencyDefaults.HeaderName` mirror note (§2.6).

**Acceptance:** sln builds clean; an in-process demo path works end to end in tests (capture →
worker run → recorded send with original headers); all listed tests green; CLAUDE.md present.

---

## Phase 2 — `Benzene.Outbox.DynamoDb` (store + the atomic unit of work)

**Goal:** the AWS-first store: durable envelopes, atomic app-state+envelope commit, native-TTL
retention, stream-image support for the Lambda relay.
**Depends on:** Phase 1. **Effort:** M.

Steps:
1. **New project `src/Benzene.Outbox.DynamoDb/`** per §0. References `Benzene.Outbox`;
   `AWSSDK.DynamoDBv2` pinned `3.7.301.4` (match `Benzene.Idempotency.DynamoDb`). Consumer
   registers `IAmazonDynamoDB` and provisions the table (house rule — the store never creates it).
2. **Table shape (document in CLAUDE.md + xmldoc):** pk = envelope `Id` (string). Attributes per
   §2.2 (+`expiresAt` for TTL, set only at dispatch). **Sparse GSI** for the sweeper: constant
   `gsiPk = "pending"` + `gsiSk = nextAttemptAtUtc`, both present only while `Pending` (removed on
   dispatch/park → the index stays small). Honest note: a constant-partition GSI serializes sweep
   throughput — acceptable for this feature's throughput class, and the streams path (Phase 3)
   bypasses the GSI entirely.
3. **`DynamoDbOutboxStore : IOutboxStore`:** `ClaimDueAsync` = query the GSI (due ≤ now, limit
   batch), then per item a **conditional `UpdateItem`** setting a `leaseUntil` (win only if
   unleased/lapsed — the atomic claim, same discipline as `DynamoDbIdempotencyStore.TryClaimAsync`
   including the lapsed-but-undeleted TTL read-back honesty). `MarkDispatchedAsync` sets status +
   `expiresAt`, removes the GSI attributes. `DeleteDispatchedBeforeAsync` → no-op returning 0
   (native TTL owns retention; xmldoc says so).
4. **`IDynamoDbOutboxTransaction` / `DynamoDbOutboxTransaction`** (scoped): drains
   `BufferedOutboxStage`, appends one `Put` per envelope to the caller's `TransactWriteItem` list,
   executes ONE `TransactWriteItemsAsync`. Clear errors for: >100 total items; commit with nothing
   staged and no app items. This is the §2.3 unit of work the handler writes through.
5. **`OutboxStreamImage`** — a small POCO matching the item attribute shape, the deserialization
   target for a DynamoDB stream `NewImage` as unmarshalled by `Benzene.Aws.Lambda.DynamoDb` (plain
   JSON) — this package owns the item schema, so the mapping lives here, keeping the Lambda relay
   free of any new package (Phase 3). Just the type + xmldoc; no Lambda dependency.
6. **DI:** `AddDynamoDbOutboxStore(tableName, …)` and `AddDynamoDbOutboxTransaction()` mirroring
   `AddDynamoDbIdempotencyStore`'s shape.
7. **Tests** (`test/Benzene.Core.Test/Outbox/DynamoDb/`, mocked `IAmazonDynamoDB` — the
   `DynamoDbIdempotencyStore`/`DynamoDbEventStore` test technique): claim is conditional and
   refuses a live lease; transaction combines app items + staged envelopes in one call and
   enforces the 100-item bound; dispatched item gets `expiresAt` and drops GSI attributes;
   `OutboxStreamImage` round-trips a store-shaped item JSON.
8. **CLAUDE.md** per §0.

**Acceptance:** sln builds; tests green; CLAUDE.md documents table shape, TTL setup, claim
atomicity requirement, and the 100-item transaction bound.

---

## Phase 3 — Relay hosts dogfooded: retrofit `examples/AwsMesh/Orders`

**Goal:** the dogfood proof. `orders:create` writes a real order item and its two outbound sends
(`payments:capture`, `order:placed`) **atomically** via the DynamoDb outbox; the best-effort
try/catch dies. Relay = DynamoDB Streams dispatch + scheduled sweep (§2.5's recommended pair).
**Depends on:** Phases 1–2. **Effort:** M–L.

Steps:
1. **Terraform** (`examples/AwsMesh/deploy/main.tf`, follow existing resource style): an
   `orders` table, an `orders-outbox` table (TTL on `expiresAt`, GSI per Phase 2, streams enabled
   `NEW_IMAGE`), an `aws_lambda_event_source_mapping` from the outbox stream to the orders Lambda
   (set `maximum_retry_attempts` — §2.5 caveat), a scheduled rule → orders Lambda whose target
   input is the direct-invoke envelope for the sweep topic (verify the exact envelope shape
   against `Benzene.Aws.Lambda.Core.BenzeneMessage` / the existing mesh interrogation before
   writing it), and IAM for both tables + stream read. Env vars: table names.
2. **Startup** (`examples/AwsMesh/Orders/Startup.cs` + `Shared/MeshServiceWiring.cs`): register
   `IAmazonDynamoDB`; `AddOutbox(WriteMode=Transactional)` + `AddDynamoDbOutboxStore` +
   `AddDynamoDbOutboxTransaction`; add `UseOutbox()` to the two outbound routes. `MeshServiceWiring`
   is shared by six services — extend `OutboundSend` (or add an overload) so only Orders opts its
   routes into the outbox; do not force the others through it. Wire `aws.UseDynamoDb(...)` for the
   stream source on the Lambda.
3. **Handler** (`examples/AwsMesh/Orders/Handlers/OrderHandlers.cs`): `CreateOrderMessageHandler`
   drops both try/catch wrappers; sends via the unchanged client/sender (now captured), writes the
   order item, commits once through `IDynamoDbOutboxTransaction.CommitAsync(orderPut)`. Update the
   class's doc comment — it currently *documents* the best-effort hole; make it document the fix.
   (`GetOrdersMessageHandler` stays canned — reading the table back is not this phase's point.)
4. **Relay handlers** (new `examples/AwsMesh/Orders/Handlers/OutboxHandlers.cs`): a
   `[Message("orders-outbox:INSERT")]` handler deserializing the stream body to
   `OutboxStreamImage` and calling `DispatchOneAsync`; a `[Message("outbox:sweep")]` handler
   calling `RunOnceAsync`. Both ~5 lines of body — they ARE the documented Lambda relay pattern.
5. **Consume side of the pair:** payments-api's `payments:capture` ingress is where relay
   duplicates land — wire `UseIdempotency()` + `AddDynamoDbIdempotencyStore` on Payments' SQS
   pipeline (one more small table in terraform), so the example demonstrates the outbox+idempotency
   pair end to end, not just the produce half.
6. **README** (`examples/AwsMesh/README.md`): a section on the flow — atomic commit, stream
   dispatch, sweep redrive, dedup at the consumer; how to observe it (kill the queue permission,
   watch envelopes park). Update `orders`' spec-facing behavior notes if the README documents the
   old best-effort behavior anywhere.
7. Build `Benzene.Examples.sln` clean (that build is the example-surface regression gate in CI).

**Acceptance:** examples solution builds; deployed (or terraform-planned) example has no
best-effort try/catch left in the create-order path; the create flow is: one
`TransactWriteItems` → stream-triggered dispatch → payments receives with original traceparent +
`idempotency-key`; sweep parks a poisoned envelope after `MaxAttempts`.

---

## Phase 4 — `Benzene.Outbox.EntityFramework`

**Goal:** the relational store: same-DbContext atomicity for the ASP.NET/HostedService crowd.
**Depends on:** Phase 1 (not 2/3). **Effort:** M.

Steps:
1. **New project `src/Benzene.Outbox.EntityFramework/`** per §0 (naming matches the existing
   `Benzene.HealthChecks.EntityFramework` sibling — family-first, `EntityFramework` spelled out).
   `Microsoft.EntityFrameworkCore` pinned `10.0.9`; **no provider package** (the app brings its
   provider; do not copy the health-check package's Npgsql reference).
2. **`OutboxRecord`** entity + `ModelBuilder.AddOutboxEntities()` (table `BenzeneOutbox`
   overridable; index on `(Status, NextAttemptAtUtc)` for the sweep query).
3. **`EntityFrameworkOutboxStore<TDbContext> : IOutboxStore`** — resolves the scoped `TDbContext`.
   `ClaimDueAsync` must be atomic under concurrent dispatchers: implement lease-claim via a
   conditioned `ExecuteUpdateAsync` (rows updated == rows claimed) rather than read-then-save;
   document that providers without `ExecuteUpdate` support fall back to optimistic concurrency on
   the lease column. `DeleteDispatchedBeforeAsync` does real deletes (this store has no TTL).
4. **`EntityFrameworkOutboxStage<TDbContext> : IOutboxStage`** — `Transactional` mode: adds
   `OutboxRecord` rows to the scoped `DbContext` change tracker, **never calls `SaveChanges`**;
   the handler's own `SaveChangesAsync` is the commit (§2.3). Immediate mode uses the store's
   `AddAsync` (adds + saves).
5. **DI:** `AddEntityFrameworkOutbox<TDbContext>(Action<OutboxOptions>?)` registering store +
   stage. Relay = the Phase 1 `OutboxDispatcherWorker` (no new code; docs show the
   `Benzene.HostedService` wiring).
6. **Tests** (`test/Benzene.Core.Test/Outbox/EntityFramework/`, EF InMemory or SQLite-in-memory —
   match whatever `test/` already uses for EF; check before adding a test dependency): staged rows
   are unsaved until the app's `SaveChanges`; handler-never-saves discards envelopes; claim is
   exclusive across two store instances on one database; sweep/cleanup paths.
7. **CLAUDE.md** — including the provider-neutrality note and the claim-atomicity requirement.

**Acceptance:** sln builds; tests green; a doc snippet in the package README/CLAUDE.md shows the
full loop: `AddOutboxEntities` + `UseOutbox()` + handler `SaveChangesAsync` + hosted worker.

---

## Phase 5 — Docs pass

**Goal:** the shipped outbox is discoverable and honestly documented; the hand-rolling cookbook
becomes the guide to the real thing. **Depends on:** Phases 1–4 (refresh at the end). **Effort:** S.

Deliverables:
1. **Rewrite `docs/cookbooks/transactional-outbox.md`** around the packages: the two write modes
   (§2.3 honesty verbatim), route wiring, the DynamoDb unit of work, the EF same-DbContext flow,
   the relay-per-host matrix (worker / streams+sweep on Lambda, including the rejected
   piggyback option and why), delivery semantics + parking (§2.7). Delete the superseded
   "Benzene doesn't ship an outbox" framing; keep the `IResponseEventPublisher` section only as
   the one-paragraph note that response-as-event flows inherit the outbox via the topic's route
   (§1) — the hand-rolled publisher/relay walkthrough goes.
2. **`docs/capability-matrix.md`**: add the produce-side row — what the outbox does and does not
   guarantee per mode, and the at-least-once ⇒ idempotency pairing (link both ways with
   `docs/cookbooks/idempotency.md`).
3. **`docs/index.md`**: link the cookbook + a one-line package mention wherever the idempotency/
   event-sourcing packages are listed (match existing style/voice).
4. Cross-link from `docs/cookbooks/response-as-event.md` and the AwsMesh example README.
5. No dead links (the `benzene` repo's website build is the checker).

**Acceptance:** every shipped flag/mode is documented; superseded framing gone; links resolve.

---

## Phase 6 — Deferred (recorded so it isn't invented mid-implementation)

Explicitly **not** in this plan's scope; each needs its own decision later:
- **Observability / mesh surface:** outbox depth and oldest-pending-age as
  `BenzeneDiagnostics` metrics, and any mesh artifact/usage surfacing. Mesh-visible shapes are
  contract changes (§3) — a separate product decision. When picked up: an
  `IOutboxStore`-level stats query + counter emission mirroring `benzene.messages.processed`'s
  tag discipline (`work/archive/spec-mesh-tooling-implementation-plan-2026-08.md` Phase 7a).
- **`Benzene.Outbox.CosmosDb`** — honest same-partition `TransactionalBatch` constraint (§2.3);
  pairs with an Azure Functions relay story (change feed / timer trigger).
- **Per-key ordered dispatch** (FIFO lanes — `BoundedConcurrentDispatcher`'s keySelector is the
  natural primitive if ever needed).
- **Inline first-attempt dispatch after commit** (latency optimization on Lambda; §2.5 option 3).
- **Parked-envelope ops tooling** (re-pend/purge commands; possibly a `benzene` CLI verb).
- **Dead-letter forwarding** — no repo-wide dead-letter story exists; parking is the deliberate
  boundary (decision 5).
- **Symbolic destinations / route catalog interplay:** `work/benzene-outbound-model-plan.md`'s
  `IOutboundRouteCatalog` / `Produces<T>().ToSqs(...)` proposal is unimplemented; `UseOutbox()` is
  ordinary route middleware, so it composes with that plan unchanged. If/when the catalog lands, an
  `Outboxed` flag on `OutboundRoute` would be a natural additive follow-up — note it there, don't
  build it here. (Checked against `work/archive/benzene-clients-redesign-plan-2026-07.md` too: this plan touches no
  surface that redesign froze; the sender interface stays as shipped.)

---

## Suggested agent task slicing

| Task | Phase | Parallel-safe with |
|---|---|---|
| T1 | Phase 1 (core) | — (everything depends on it) |
| T2 | Phase 2 (DynamoDb) | T4 |
| T3 | Phase 3 (example retrofit) | T4, after T2 |
| T4 | Phase 4 (EF Core) | T2, T3 |
| T5 | Phase 5 (docs) | last; refresh against whatever shipped |

Each task: read §0 + §2 + its phase; verify cited files; build + test; commit with a conventional
message; report what was verified vs assumed.

## Open questions (owner decision needed)

1. **Release posture:** do `Benzene.Outbox*` join the 1.0 API-freeze surface
   (`work/archive/1.0-api-freeze-proposal-2026-07.md`) on the next release, or ship a cycle as explicitly
   experimental first? The plan assumes normal packing via the existing sln pipeline either way;
   only the compat-promise label needs the call.

Everything else in this document is decided; implementers should not reopen §2's choices.
