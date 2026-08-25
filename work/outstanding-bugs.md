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

> **Tracked findings, 2026-08-25 (review rounds 5–6) — all fixed.** A batch of 27 evidence-backed
> findings (live repros, stress tests, compiler-driven probes) from the round-5/round-6 review passes
> is now fully resolved — all nine work packages below landed and pushed to `main`. Their design
> decisions, rationale, and rejected alternatives remain ruled in
> **[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md)** (now archived); consult it
> before touching any of this code again so a decision made here doesn't get silently re-litigated.

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

### Tracked findings round 5–6, WP-4 — gRPC null-response crash (done)
Decision, rationale, and why server-streaming/duplex are deliberately untouched are ruled in
[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-4 — gRPC null-response crash (unary +
client-streaming)".
- **[RESOLVED] #8 — a fire-and-forget unary handler (no response payload) crashed instead of
  succeeding.** `ProtobufJsonGrpcMessageAdapter.ConvertResponse<TResponse>` threw `BenzeneException`
  on a `null` payload, which `GrpcMethodHandler.HandleAsync`'s unary call site surfaced as an opaque
  `RpcException(Unknown)` instead of the mapped `accepted → OK`. A `null` payload now converts to an
  empty `TResponse` message instance. See WP-4.
- **[RESOLVED] #23 — the same crash on the client-streaming call site.** One adapter fix
  (`ConvertResponse`) closes both #8 and #23, since `GrpcMethodHandler.ClientStreamingAsync` calls the
  same method. See WP-4.

### Tracked findings round 5–6, WP-1 — mesh host auth (done)
Decisions, rationale, and rejected alternatives for all seven are ruled in
[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-1 — Mesh host: auth satisfiability
matrix, OIDC hardening, logout, dispatch wiring".
- **[RESOLVED] #3 — `AllowedEmailDomains` was satisfiable under `auth.mode: "none"`, which establishes
  no identity to filter at all.** `MeshAuthGate.Validate` now rejects it at startup. See WP-1(a).
- **[RESOLVED] #6 — `dispatchRole` was satisfiable under `proxy` with no `groupsHeader`, which carries
  no role claims at all.** Rejected at startup, naming both keys. See WP-1(a).
- **[RESOLVED] #19 — `dispatch.enabled: true` under `auth.mode: "none"` booted cleanly and then
  permanently 403'd every `mesh:dispatch` request** (mode `"none"` sets no identity, and
  `MeshDispatchGate`'s identity check is fail-closed), with nothing telling the operator why. Rejected
  at startup instead. See WP-1(a).
- **[RESOLVED] #20 — a non-https `auth.oidc.authority` reached the OIDC handler unvalidated and
  crashed with an unhandled 500 the first time discovery metadata was fetched, mid-request.** New
  `auth.oidc.requireHttpsMetadata` (default `true`) rejects it at startup instead; set `false`
  explicitly for a local, TLS-less authority only. See WP-1(b).
- **[RESOLVED] #4 — no way to sign out of an oidc-mode session.** `POST /mesh/auth/logout` (CSRF
  header required, GET rejected) signs out the cookie and answers `{"redirect": ...}` from the IdP's
  discovered `end_session_endpoint` when available; the mesh UI's Sign-out control now POSTs and
  navigates on the response instead of a plain `<a href>` GET link. See WP-1(c).
- **[RESOLVED] #5 — `dispatchUrl` was never passed to `UseMeshUi(...)`, so the Test Console's send
  button stayed invisible even with dispatch wired.** `Startup.cs` now passes it whenever
  `dispatch.enabled`. See WP-1(d).
- **[RESOLVED] #27 — `dispatchRole` was satisfiable under `basic`, a single hand-configured account
  with no roles.** Rejected at startup; **no `MESH_BASIC_ROLES` knob was added** (rejected
  alternative — `basic` stays deliberately minimal). See WP-1(a).

### Tracked findings round 5–6, WP-7 — cross-cutting hygiene (done)
Decisions, rationale, and rejected alternatives for all five are ruled in
[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-7 — Cross-cutting hygiene: cancellation,
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

### Tracked findings round 5–6, WP-5 — Azure: source-generator diagnostics; Service Bus settle ordering (done)
Decisions, rationale, and the explicitly rejected alternative are ruled in
[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-5 — Azure: source-generator
diagnostics; Service Bus settle ordering".
- **[RESOLVED] #9 — `AzureFunctionTriggerGenerator` let two triggers of different transports collide
  on the same `[Function(name)]` name literal, silently emitting ambiguous/invalid output.** Every
  transport's triggers now merge into one array before emission, so the check is cross-transport (not
  per-transport as it was); a collision reports `BENZ0001` (error) at each colliding declaration and
  emits none of them. The Function name is deliberately **not** auto-renamed the way the generated
  class name already is — rejected alternative, see the ruling — because it's externally meaningful
  (bindings, host.json, scale rules, portal identity), so silently renaming it would just move the
  failure to deployment. See WP-5a.
- **[RESOLVED] #11 — the generator had no diagnostics path at all**, so a reader that hit a problem
  could only silently skip the declaration (see #9 above and CosmosDb below). Added a
  `DiagnosticDescriptors` table (`BENZ0001`, `BENZ0002`) as the one place future generator complaints
  join, and a `TriggerInfo.ForDiagnostic` path that lets a transport reader hand a diagnostic through
  the same incremental pipeline that carries real triggers to `Execute`, which reports it. See WP-5a.
- **[RESOLVED] #10 — `ServiceBusApplication.OnPipelineSucceededAsync` set `state.Acked = true` before
  calling `CompleteMessageAsync`/`AbandonMessageAsync`, not after.** If the settle call itself threw,
  the base class's exception-recovery fallback-abandon (gated on `!state.Acked`) saw `Acked` already
  true and skipped — exactly when it was needed. `Acked` is now set only once the settle call returns
  successfully. See WP-5b.

### Tracked findings round 5–6, WP-3 — claim fencing for Outbox and Idempotency stores (done)
Decisions, rationale, and the breaking-change ruling (pre-1.0, no compatibility overloads) are ruled in
[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-3 — Claim fencing for Outbox and
Idempotency stores".
- **[RESOLVED] #16 — `IIdempotencyStore.CompleteAsync`/`ReleaseAsync` took no claim token, so a
  stale/slow holder's late settle (arriving after its claim legitimately lapsed and was reclaimed by
  another worker) could silently clobber the new holder's outcome (round-5 deterministic repro).**
  `ClaimResult` now carries a `ClaimToken` (non-null exactly when `Claimed`); `CompleteAsync`/
  `ReleaseAsync` require it and return `Task<bool>` — `false` means no live claim matched and nothing
  was written. `InMemoryIdempotencyStore` checks the token under its existing lock;
  `DynamoDbIdempotencyStore` conditions the settle `PutItem`/`DeleteItem` on a `claimToken` attribute
  match. `IdempotencyMiddleware` logs a warning (not an error) on a `false` settle. See WP-3.
- **[RESOLVED] #17 — `IOutboxStore.MarkDispatchedAsync`/`RescheduleAsync`/`ParkAsync` took no lease
  token, so a live-but-slow claimant whose lease naturally lapsed and was reclaimed by another worker
  could have its late settle silently clobber (or, via a stale `RescheduleAsync`, resurrect) the new
  holder's state — the round-6 stress-test double-dispatch (`sendCount == 2`) with the store unable to
  tell the difference.** `OutboxEnvelope` now carries a `LeaseToken`, stamped/rotated on every
  successful `ClaimDueAsync`/`ClaimAsync`; the three settle methods require it and return `Task<bool>`.
  All three stores (`InMemoryOutboxStore`, `DynamoDbOutboxStore`, `EntityFrameworkOutboxStore`) fence
  their settle writes on it; `OutboxDispatcher` passes the claimed envelope's token through and logs a
  warning on a `false` settle. Fencing closes the state-clobber/resurrection hole and makes a lost
  lease visible — it does **not** and cannot recall a message a stale claimant already handed to the
  transport before its lease lapsed; crash-after-send remains an inherent at-least-once window. See
  WP-3.

### Tracked findings round 5–6, WP-9 — schema compatibility: union-aware walkers (done)
Decisions, matching rule, and the breaking-direction table are ruled in
[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-9 — Schema compatibility: union-aware
walkers".
- **[RESOLVED] #25 — `SchemaCompatibilityComparer`/`JsonSchemaComparer` never inspected `oneOf`/
  `anyOf`/`allOf`, so removing an entire discriminated-union variant (`oneOf:[Dog,Cat]` →
  `oneOf:[Dog]`) was reported as zero changes.** Both walkers (deliberately-identical twins) now walk
  `oneOf`/`anyOf`/`allOf` pairwise, matching members by discriminator mapping value, then `$ref` target
  name, then position, and report `UnionVariantAdded`/`UnionVariantRemoved`/`UnionVariantChanged`
  (the last recursing into the matched pair). An `items` present on only one side is now a `TypeChanged`
  instead of being silently skipped. See WP-9.

### Tracked findings round 5–6, WP-2 — Mesh collector robustness, deterministic schema, example posture (done)
Decisions, rationale, and rejected alternatives for all three are ruled in
[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-2 — Mesh collector robustness,
deterministic schema, example posture".
- **[RESOLVED] #22 — `MeshTimeRangeResolver.ParseDuration` threw `OverflowException` on a count too
  large for `TimeSpan` (e.g. `now-100000000d`), surfacing as an unhandled 500 on `mesh:query:*`.**
  `ParseDuration` now treats an overflowing count exactly like an unparseable one — absent, never
  thrown — extending `ParseBound`'s existing contract. Recorded as principle P5 ("query-side inputs
  degrade to absent, never throw, mirroring the ingest side's 'no feed fails ingestion' rule") in
  `src/Benzene.Mesh.Collector/CLAUDE.md`. See WP-2(a).
- **[RESOLVED] #7 — `MeshSchemaGenerator`'s `required` array followed CLR reflection order, which is
  unspecified across runtimes, making descriptor hashes non-reproducible.** `required` is now sorted
  `StringComparer.Ordinal`, the same ordering `properties` already used. Accepted, one-time
  consequence: descriptor hashes shift for any service whose reflection order wasn't already
  alphabetical (pre-1.0, determinism is the contract). See WP-2(b).
- **[RESOLVED] #21 — `examples/AzureMesh`'s `POST /mesh/refresh` had no guard at all (unlike
  AwsMesh), and its README carried no unauthenticated-posture disclaimer for the mesh host itself.**
  `UseMeshRefreshGuard` is now wired in front of the endpoint (CSRF header + manifest-age throttle,
  same package AwsMesh already uses); the README's new "Security posture" section states plainly that
  the mesh host itself — not just the polled services — is publicly reachable and unauthenticated,
  demo-only posture, matching the K8sMesh/GoogleCloudMesh/AzureFunctionsMesh siblings (P7). Full OIDC
  for this example stays out of scope. See WP-2(c).

### Tracked findings round 5–6, WP-6 — AWS clients: Lambda invocation semantics; Step Functions idempotent starts (done)
Decisions, rationale, and the rejected alternative for #13 are ruled in
[`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-6 — AWS clients: Lambda invocation
semantics; Step Functions idempotent starts".
- **[RESOLVED] #12 — `UseAwsLambda<T>()`'s `LambdaContextConverter<T>` (the `<T, Void>` fire-and-forget
  shape) silently invoked `RequestResponse` (synchronous) instead of `Event` (async), and
  `MapResponseAsync` unconditionally returned `Accepted` regardless of the actual invoke outcome.**
  `CreateRequestAsync` now sets `InvocationType.Event`; `MapResponseAsync` classifies the response
  (Event: 2xx `StatusCode` → `Accepted`, else failure; request/response: non-null `FunctionError` →
  failure, never `Accepted`). See WP-6(a).
- **[RESOLVED] #13 — `StepFunctionsClient.StartExecutionAsync` treated every
  `ExecutionAlreadyExistsException` as a successful idempotent retry, with no check that the existing
  execution's input actually matched this call's — a name collision with a DIFFERENT input was a silent
  false-positive `Accepted`.** Now calls `DescribeExecution` and compares the existing execution's
  `Input` to this call's serialized input: a match is `Accepted`; a mismatch is a `Conflict` failure
  (the caller's payload was not started). See WP-6(b).
- **[RESOLVED] #14 — `SanitizeExecutionName` could map two distinct original names onto the SAME
  Step Functions execution name (via character-replacement collisions or identical-after-truncation
  names), silently defeating (#13)'s idempotency check.** A sanitized-or-truncated name now gets a
  deterministic 8-hex-character `SHA-256(original name)` suffix, collision-resistant across distinct
  originals; an already-clean name is unchanged. See WP-6(c).

### Tracked findings round 5–6, WP-8 — RabbitMQ `mandatory` made real (done)
Ruled in [`bug-fix-designs-2026-08.md`](archive/bug-fix-designs-2026-08.md) §"WP-8 — RabbitMQ `mandatory` made
real".
- **[RESOLVED] #24 — `RabbitMqClientMiddleware`'s `mandatory: true` was documented ("an unroutable
  message is returned by the broker rather than silently dropped") but never implemented: the
  middleware called `BasicPublishAsync(..., mandatory, ...)` and unconditionally set
  `context.Published = true` right after, never subscribing to `IChannel.BasicReturnAsync` - an
  unroutable message with `mandatory: true` still silently reported `Accepted`, identical to
  `mandatory: false`.** Implemented for real (P6 - no inert options), not removed: new
  `RabbitMqMandatoryPublishCoordinator` (one instance per `IChannel`, shared across every publish -
  RabbitMQ.Client's `BasicReturnAsync`/`BasicAcksAsync`/`BasicNacksAsync` are channel-scoped events, and
  `RabbitMqClientMiddleware` is constructed fresh per publish, so subscribing from the middleware itself
  would pile on a handler per message) stamps a `MessageId` if the caller didn't set one, correlates a
  `Basic.Return` back to its publish by that `MessageId`, and correlates `Basic.Ack`/`Basic.Nack` by
  delivery tag (captured race-free by pairing `GetNextPublishSequenceNumberAsync()` with the publish
  call under a per-channel gate - RabbitMQ.Client 7.0.0 does not hand a publish its own assigned tag
  back). A returned message now resolves `Published = false` (a failed send), not the old unconditional
  `true`. Wiring requires (and fails fast on, at setup - not first publish) a channel with publisher
  confirmations enabled, verified via `GetNextPublishSequenceNumberAsync()` (the only public-API-visible
  proxy for that setting in this client version). See WP-8.

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
