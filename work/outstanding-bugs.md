# Outstanding bugs — reconciled against current source (2026-07-21, verification pass)

> **How to read this.** Every item from the prior triage was **re-verified against current `main`
> source** (four parallel review passes, cross-checked with git history). The large majority are now
> **RESOLVED** — either by the #29/#30 review series, the overnight-fixes series, the fresh
> security/concurrency hunt, this reconciliation pass, or the API-shape track
> (`work/archive/api-shape-proposal-1.0-2026-07.md`, items 2a/2b/4a shipped). What genuinely remains is almost entirely
> **maintainer decisions** (behaviour/API/policy calls) plus **perf hygiene** — there are effectively
> **no clean-cut correctness bugs left unfixed**. Items are cited with `file:line` where useful.
>
> **Re-check note (2026-07-21, later).** Since the four-pass verification, `main` advanced and closed
> three more items that were briefly listed open: **Avro deserialize OOM** (`BoundedBinaryDecoder`),
> **SQS/DynamoDB → `IHasMessageResult`** convergence, and **transport-tag constants**. This file has
> been updated to match. Two earlier characterisations here were corrected against source: the Kinesis
> "partition checkpoint model" (inherent to Kinesis's single-resume-point contract, not a bug — now
> RESOLVED/doc) and the Avro OOM ("library-limited" was wrong — a bounded decoder fixed it).
>
> **Re-check note (2026-08-20).** Four `[DECISION]` items have since been decided and implemented, and
> are moved to the resolved half below: the split-brain `RaiseOnFailureStatus` defaults, the
> `AddMessageHandlers` finder lock-in, `BenzeneResultExtensions.IsSuccess()`, and (as a removal rather
> than a fix) the `BenzeneHttpWorker` entry. The excluded "missing features" list was also trimmed of
> two things that shipped. Everything still listed under **Open** was re-confirmed open on that date.

Legend: **[DECISION]** real issue, fix is a behaviour/API/policy call (needs a maintainer's decision
first). **[PERF]** performance hygiene, not a correctness bug. **[RESOLVED]** verified fixed in source.

> **Tracked findings, 2026-08-25 (review rounds 5–6).** A separate batch of 27 verified findings
> (evidence-backed: live repros, stress tests, compiler-driven probes) is tracked on the shared task
> board (tasks #1–#27) and is **not duplicated into this file** while open. Their fix designs — with
> decisions, rationale, and rejected alternatives — are ruled in
> **[`bug-fix-designs-2026-08.md`](bug-fix-designs-2026-08.md)**; do not re-decide or re-review those
> areas without reading that ruling first. As each work package lands, its items are added to the
> Resolved half below with a pointer to the ruling's section.

---

## Resolved since the prior triage (verified in current source)

These were previously listed as open. They are now confirmed fixed — do **not** re-action them.

### Tier 1 correctness (all done)
- **`Utils.GetTypes` `ReflectionTypeLoadException`** — all 3 copies now `catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>()... }`.
- **`OutboundSnsContextConverter` drift** — now shares `DataTypeFor`/`GuardAttributeLimit`/`ApplyFifoProperties` with `SnsContextConverter` (FIFO + numeric + 10-attr cap).
- **`ActivityProcessTimer` span-not-failed** — fixed in the `UseTimer` wrapper (`Diagnostics/Timers/Extensions.cs`): `AddException` + `SetStatus(Error)` on throw.
- **`HttpRequest` null contract** — `Method`/`Path`/`Headers` now have initializers.
- **gRPC `[EnumeratorCancellation]`** — present on `ReadAll`/`Convert`; OK-unary-no-payload correctly maps to `OK` (only a genuinely null result → `Unknown`).
- **`AwsLambdaBenzeneTestHost` X-Ray segment leak** — `BeginSegment`/`EndSegment` now in try/finally.
- **`SqsMessageTopicMapper` null `MessageAttributes`** — guarded (`SqsConsumerMessageTopicGetter`).
- **`BenzeneHttpWorker` accept loop** — **moot: the subsystem was removed.** `Benzene.SelfHost.Http`
  (the `HttpListener` host this worker belonged to) was deprecated and then deleted in favour of Kestrel
  via `Benzene.AspNet.Core` — see `docs/deprecations.md`. There is no `BenzeneHttpWorker` to fix or
  regress. (It had received the catch-all + `finally` before the removal.)
- **Dead reflection code + stray `Debug.WriteLine`** — `MessageClientSdkBuilder` reflection removed; `Debug.WriteLine` removed from `MessageHandler`/`ReflectionMessageHandlersFinder`/`HandlerPipelineBuilder` (this pass); `AsRawHttpRequest` now emits CRLF.

### Tier 2 (done)
- **DI factory disposal leak (`MicrosoftServiceResolverFactory.Dispose`)** — now gated by `_ownsServiceProvider`; disposes providers it built, incl. `DisposeAsync`.
- **`ValidateOutboundRouting` global-field fragility** — now attribute-gated on `[OutboundRoutingContract]`.
- **Kinesis "successful batch never checkpoints reprocesses forever"** — `KinesisStreamOptions.AutoCheckpointOnSuccess = true` default + `CheckpointAll()`.
- **S3 & EventBridge Lambda had no `RaiseOnFailureStatus` opt-in** — both now have `Options.RaiseOnFailureStatus` throwing a `*MessageProcessingException`.
- **Self-host SQS consumer `WholeBatch` default deletes failures** — default flipped to `PerMessage` (only successes deleted).
- **Service Bus worker `AutoComplete` default completes failures** — default flipped to `Explicit` (null OR failure → abandon). (Stale CLAUDE.md "(default)" line corrected this pass.)
- **AWS batch clients let a whole-request throw escape** — `Sqs`/`Sns`/`EventBridge` batch clients now catch the send throw → per-entry failures.
- **AWS batch clients per-entry conversion failure aborts the batch** — AWS clients wrap `CreateRequestAsync` per entry.
- **Azure batch clients per-entry conversion failure aborts the batch** — `ServiceBus`/`EventHub`/`EventGrid` batch clients now wrap `CreateRequestAsync` per entry too (this pass).
- **`MeshAggregator.BuildTopicEntry` false mismatch + space-collision dedup** — request/response guarded independently; dedup keyed on a `(Client, Server)` tuple.
- **`CloudServiceDescriptorSource._descriptor` non-volatile** — now `volatile`.
- **`mesh-ui.html` unvalidated href scheme** — `safeHttpUrl()` allow-lists http/https.
- **Outbound Kafka `GetBytes(null)`** — `header.Value ?? string.Empty`.
- **Kafka body getter `.ToString()` on `byte[]`** — now UTF-8 decodes byte payloads.
- **Avro deserialize OOM** — `BoundedBinaryDecoder` guards the length prefix before allocation (`482af8ad`).
- **SQS/DynamoDB adapter convergence** — onto `IHasMessageResult` (`92f4c459`) + `TransportNames` tags (`ee342f7e`).
- **Overlapping result abstractions** — legacy `IMessageResult` deleted, settlement routed through `IBenzeneResult` (`6424cde9`).
- **Kinesis "partition checkpoint model"** — inherent to Kinesis's single-resume-point contract (design doc §2); checkpointer already correct, shard-order guidance added to CLAUDE.md (`822cabf4`).

### Decided and implemented since (2026-08-20)
- **Split-brain `RaiseOnFailureStatus` defaults** — **DECIDED: align on `true`.** Every transport that
  has the flag now defaults it `true` (SNS, S3, EventBridge, Azure Functions Kafka/QueueStorage/
  ServiceBus/EventGrid/EventHub, the Event Hub worker, Google Pub/Sub) — see
  `work/archive/settlement-contract-1.0-2026-07.md` for the flip, and `BenzeneKafkaConfig.RaiseOnFailureStatus` for the
  self-hosted Kafka worker, which had no such flag at all until the
  `work/settlement-default-alignment-proposal.md` item A1 fix. A returned failure result is no longer
  silently settled anywhere by default. *(Tier B of that proposal — the `!= true` vs `== false`
  null/unrouted policy — is a separate, still-open decision; see below.)*
- **`AddMessageHandlers` finder lock-in** — **FIXED.** `IMessageHandlersFinder` is now registered once,
  built **lazily** over the **deduped union** of every registered `MessageHandlerCandidateTypes`, so a
  no-arg-then-typed call sequence discovers both and overlapping scans don't double-register (the
  dedup-semantics decision the earlier revert was blocked on).
  (`Benzene.Core.MessageHandlers/DI/Extensions.cs`, `RegisterHandlerFinderInfrastructure`.)
- **`BenzeneResultExtensions.IsSuccess()` true only for `Ok`** — **FIXED.** It now delegates to
  `BenzeneResultStatus.IsSuccess(status)`, so it covers all six success statuses and agrees with
  `IBenzeneResult.IsSuccessful`; `IsOk()` remains the narrower "exactly `Ok`" check.
  (`Benzene.Results/BenzeneResultExtensions.cs`.)

### Security/concurrency (fresh-hunt series, done)
Native AMQP batch leak; XML entity-expansion DoS; MessagePack `TrustedData` DoS; Redis faulted-connection
lock-in; retry `Task.Delay` overflow; RabbitMq failed-startup lane leak; mesh path traversal; discovery
SSRF/URL-restructuring; codegen NRE + int64 truncation + non-incremental generator; CORS
wildcard+credentials and full Fetch-spec preflight compliance; spec-output caching.

### Tracked findings round 5–6, WP-7 — cross-cutting hygiene (done)
Decisions, rationale, and rejected alternatives for all five are ruled in
[`bug-fix-designs-2026-08.md`](bug-fix-designs-2026-08.md) §"WP-7 — Cross-cutting hygiene: cancellation,
eviction, Saga per-run state".
- **[RESOLVED] #2 — `IHealthCheck.ExecuteAsync()` had no `CancellationToken`.** Interface now requires
  one; every implementer (20+) forwards it into its own I/O. See WP-7(a).
- **[RESOLVED] #26 — `SqsHealthCheck` ran its own internal timeout guard, duplicating the processor's.**
  Guard deleted; it now relies purely on the processor's uniform timeout wrap, matching
  `SnsHealthCheck`/`EventBridgeHealthCheck`'s shape. See WP-7(b).
- **[RESOLVED] #1 — claim-check middleware never passed the ambient cancellation token into the store.**
  `ClaimCheckHydrateMiddleware`/`ClaimCheckOffloadMiddleware` now resolve it via
  `ICancellationTokenAccessor` and pass it into `IClaimCheckStore.Get/PutAsync`. See WP-7(c).
- **[RESOLVED] #18 — `InMemoryClaimCheckStore` never actually reclaimed an expired entry.** `GetAsync`
  now evicts an entry it finds expired; `PutAsync` also sweeps all expired entries, at most once per
  minute, with no background thread. See WP-7(d).
- **[RESOLVED] #15 — `SagaStep<T>`/`Stage` stored per-execution outcome as instance fields, so
  concurrent `RunAsync()` calls on one built `Saga` could corrupt each other (round-5: 6/300 corrupted
  runs).** Outcome now lives in a run-scoped `SagaStepOutcome` created fresh per run; a built `Saga` is
  documented immutable and concurrency-safe. See WP-7(e).

### Tracked findings round 5–6, WP-9 — schema compatibility: union-aware walkers (done)
Decisions, matching rule, and the breaking-direction table are ruled in
[`bug-fix-designs-2026-08.md`](bug-fix-designs-2026-08.md) §"WP-9 — Schema compatibility: union-aware
walkers".
- **[RESOLVED] #25 — `SchemaCompatibilityComparer`/`JsonSchemaComparer` never inspected `oneOf`/
  `anyOf`/`allOf`, so removing an entire discriminated-union variant (`oneOf:[Dog,Cat]` →
  `oneOf:[Dog]`) was reported as zero changes.** Both walkers (deliberately-identical twins) now walk
  `oneOf`/`anyOf`/`allOf` pairwise, matching members by discriminator mapping value, then `$ref` target
  name, then position, and report `UnionVariantAdded`/`UnionVariantRemoved`/`UnionVariantChanged`
  (the last recursing into the matched pair). An `items` present on only one side is now a `TypeChanged`
  instead of being silently skipped. See WP-9.

---

## Open — maintainer decisions (the real remaining backlog)

None of these is a clean self-contained bug; each changes behaviour, a public API, or a policy.

### Settlement / at-least-once semantics
- **[RESOLVED / doc] Kinesis "partition checkpoint model"** — *previously listed as an unsafe
  up-to-checkpoint hazard needing a new model; that was an over-statement.* Kinesis's
  `ReportBatchItemFailures` contract is inherently a **single shard-order resume point** — AWS reads
  only the first reported sequence number and retries every record from there to the end; there is no
  per-record/per-partition skip (design doc §2). So a "retain partition A, retry partition B" model is
  **impossible by construction**, not a missing feature. The checkpointer implements the only correct
  model: a single monotonic shard-order watermark (`21f7333` prevents rewind) + `AutoCheckpointOnSuccess`
  (closes never-checkpoint-reprocess-forever). The one residual was **documentation** — that a
  `PartitionBy` handler must checkpoint the shard-order frontier, not each partition's latest record —
  now added to the package CLAUDE.md. Nothing further to implement.
- **[DECISION] Kinesis & DynamoDB streams swallow the pipeline exception** — both return a batch
  response and rely on the ESM having `ReportBatchItemFailures`, which Benzene can't see. Consider a
  thrown-exception fallback or a startup warning. (`KinesisStreamApplication.cs:101`, `DynamoDbApplication.cs:57`.)
- **[DECISION] RabbitMQ null-result → ack** — documented/tested deliberate, diverges from
  ServiceBus/DynamoDb (null → redeliver). Cross-transport-consistency call only.

### DI / mesh
- **[DECISION] `MeshSelfReportMiddleware` fire-and-forget on Lambda** — `_ = PublishBestEffortAsync()`
  after `await next()`; the runtime freezes on return so the report often never completes on the very
  on-demand host it targets. The package documents opportunistic-only as deliberate; a Lambda-reliable
  path (flush-before-return / scheduled) is a design change.

### Contracts / validation / serialization
- **[DECISION] `SchemaCompatibilityComparer` gaps** — `CompareSchemas` ignores `.Enum`, `.Nullable`,
  and facets (`MaxLength`/`Pattern`/`Minimum`…), so enum-value removal, nullable flips, and facet
  tightening pass the backward-compat gate. Closing it needs new `SchemaChangeKind` values + a
  per-direction breaking-vs-warning classification (policy). (`Compatibility/SchemaCompatibilityComparer.cs:106-177`.)
- **[RESOLVED] Avro unbounded deserialize allocation (OOM)** — fixed by `BoundedBinaryDecoder`
  (`482af8ad`): it guards the `bytes`/`string` length prefix **before** the `new byte[length]`
  allocation, bounded by the decoded input size and tightened by `AvroOptions.MaxDeserializeBytes`.
  (My earlier "library-limited, wire-cap only partial" note was wrong — the bounded-decoder approach
  closes it properly.) **[DECISION, post-1.0] Avro `Dictionary`/map round-trip** still unsupported
  (`KeyValuePair` is read-only → empty record) — a bidirectional map-schema feature, per
  `work/archive/api-shape-proposal-1.0-2026-07.md` item 4b.
- **[RESOLVED] Overlapping result abstractions** — SQS/DynamoDB first converged onto
  `IHasMessageResult` (`92f4c459`, the `bool?` fork gone), then the **legacy `IMessageResult` was
  deleted outright and settlement rerouted through `IBenzeneResult`** (`6424cde9`, touching
  `IHasMessageResult` + every transport context). The three-way overlap is gone; the library now
  represents a message outcome one way. (proposal items 1b + 2b)
- **[DECISION] Cache null-payload negative-caching & version unknown-version passthrough** — a null
  deserialized value is a cache miss and a null payload is still written back (`CacheEntry.cs:64-83`);
  an unknown requested version silently falls back to the max version (`VersionSelector.cs:21-29`).
  Both are documented per-policy behaviours.

### Health / convergence / lower-impact
- **[DECISION] `DynamoDbHealthCheck` ignores `TableStatus`** — verdict is HTTP-200 only; `TableStatus`
  is now surfaced in the result data but doesn't fail a `DELETING`/`INACCESSIBLE_…` table. Which
  statuses fail is the policy call. (`DynamoDbHealthCheck.cs:36-40`.)
- **[DECISION] `CachingHealthCheckProcessor` cache key is the sorted Type-set** — two probes (liveness
  vs readiness) with the same type-set but different instances collide for the TTL. (`CachingHealthCheckProcessor.cs:49`.)
- **[RESOLVED] SQS/DynamoDb two-generation adapter + magic-string transport tags** — both converged
  onto `IHasMessageResult` (`92f4c459`, the `bool?` fork gone) and the tags now use
  `TransportNames.Sqs`/`.DynamoDb` (`ee342f7e`). (proposal items 2a + 2b)
- **[DECISION] CR/LF response-header injection (defence-in-depth)** — API-Gateway/self-host/AspNet
  response adapters pass header values through without stripping CR/LF. Not a confirmed live vector
  (values are Benzene-/handler-sourced today); whether to strip centrally is the call.

### Latent / API-freeze
- **[DECISION] `MiddlewareRouter` value-type request** — `request == null` on an unconstrained
  `TRequest` is always false for value types; the fix (`where TRequest : class`) is a source-breaking
  public-API constraint held for the 1.0 freeze. (No value-type router exists in-repo.)
- **[DECISION] Cosmos `MapChangeType` unknown op → `Replace`** — safe against today's SDK (only
  Create/Delete/Replace exist); a fix means a throw in the change-feed hot path or a new
  `CosmosChangeType.Unknown` enum value.
- **[DECISION] `SnsMessageBodyGetter` un-guarded `SnsRecord.Sns`** — adding `?.` would return null and
  weaken `GetBody`'s non-null contract; not production-reachable (AWS always populates `Sns`).

---

## Open — performance hygiene (not correctness bugs)

- **[PERF] `ActivityMiddlewareDecorator` re-resolves `FindHandler` per middleware** — paid only when an
  OTel listener is attached (guarded by the `activity is null` fast-path). (`ActivityMiddlewareDecorator.cs:76`.)
- **[PERF] Per-send *converter* allocation in the single-message egress clients** — the *serializer* is
  now a shared static in all 7; the converter is still `new`'d per send.
- **[PERF] Azure workers resolve the logger via a per-error DI scope** — `BenzeneEventHubWorker.cs:112,156`.
- **[PERF] No `ConfigureAwait(false)` in core** — core await paths rely on a SynchronizationContext-free host.

---

## Excluded (unchanged from prior triage)
- **Missing features / roadmap** (not bugs): SQS-FIFO-consume, Service Bus
  transactions/deferral/filters, Kafka EOS/schema-registry, Kinesis tumbling windows, BlobStorage
  `Stream` binding, Queue-Storage size guard, SNS Extended-Client, etc. — see the roadmap docs.
  *(gRPC streaming and Service Bus sessions were on this list and have since shipped —
  `Benzene.Grpc` routes all four RPC shapes; `BenzeneServiceBusConfig.SessionsEnabled` drives the
  session processor.)*
- **Verified FALSE**: "gRPC client discards caller deadline/cancellation"; "outbound SQS/SNS return
  `Ok` not `Accepted`".
