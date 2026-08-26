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

> **Tracked findings, 2026-08-26 (review rounds 7–10) — all fixed.** A batch of evidence-backed findings
> (tasks #30–#95) from the later review rounds (which re-reviewed the round-5/6 fix code and swept
> previously-unscrutinized areas: core/DI, validation, resilience, observability, CLI/codegen,
> serialization, the Autofac adapter, the mesh backend adapters, the schema registry, the testing infra,
> hosting, abstractions) is now fully resolved — all 16 work packages landed and pushed to `main` (16
> merge commits plus one follow-up commit fixing baseline regressions surfaced only by the full
> post-merge test run). Their design decisions, rationale, and rejected alternatives remain ruled in
> **[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md)** (now
> archived, stamped with the landing commits); consult it before touching any of this code again so a
> decision made here doesn't get silently re-litigated.

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

### Tracked findings round 7–10, WP-B — DynamoDB idempotency phantom win + fencing consistency (done)
Decisions, rationale, and the rejected "won-but-unverified" alternative are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-B — DynamoDB
idempotency phantom win + fencing consistency".
- **[RESOLVED] #31 — `DynamoDbIdempotencyStore.TryClaimAsync`'s conflict path returned
  `ClaimResult.Won(claimToken)` on an empty read-back without ever writing anything — a `Won` with no
  durable row, defeating dedup and making the later fenced `CompleteAsync` always no-op (confirmed via
  an executed test: `PutItemAsync` call count 1, yet `Claimed=True` with a token never persisted).**
  `TryClaimAsync` never synthesizes a `Won` from an empty read: when the follow-up `GetItem` after a
  `ConditionalCheckFailedException` finds the record absent (a race with a concurrent `ReleaseAsync`),
  it bounded-retries the conditional `PutItem` against the now-observed-absent state (`MaxClaimAttempts`
  = 3), returning `Won` only after an actual successful write. If every attempt still races the same way,
  it throws `IdempotencyClaimContentionException` rather than fabricate an outcome — the invariant *every
  `Won` corresponds to a durable write* now holds unconditionally. See WP-B.
- **[RESOLVED] #51 — `InMemoryIdempotencyStore.IsLiveClaim` ANDed an `entry.ExpiresAt > now` check the
  DynamoDb/Outbox fences deliberately omit, so a holder that merely outraced its own TTL (with nobody
  having reclaimed the key) got a misleading "reclaimed by another worker" `false` and its outcome was
  discarded.** Dropped the `ExpiresAt > now` conjunct — `IsLiveClaim` is now token match alone, matching
  every sibling fencing implementation (`DynamoDbIdempotencyStore`, both Outbox stores). See WP-B.

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
  **[CORRECTED 2026-08-26, WP-E/#41]:** the "matching the K8sMesh/GoogleCloudMesh/AzureFunctionsMesh
  siblings" claim above was false when written — none of those three actually had `UseMeshRefreshGuard`
  wired at the time. All three are now fixed to match (see the WP-E entry below); the claim is true as
  of that fix, not as of this one.

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
- **[RESOLVED] #38 — `AzureFunctionTriggerGenerator.Execute` built the `BENZ0001` diagnostic from a
  `TriggerInfo.Location`, which `TriggerInfo`'s equality deliberately excludes (for incremental cache
  hits) - so the incremental engine was free to keep serving an old cached `TriggerInfo`, stale
  `Location` and all, whenever a freshly-recomputed instance compared equal to it. If that old
  `Location`'s `SyntaxTree` was no longer part of the current `Compilation`, `Diagnostic.Create`/
  `ReportDiagnostic` threw `ArgumentException` during suppression-checking and crashed the whole
  incremental build on an ordinary, unrelated edit - reproduced both via two independently-constructed
  `CSharpCompilation`s sharing one `GeneratorDriver`, and via a genuine single-tree incremental edit
  (`SyntaxTree.WithChangedText` + `Compilation.ReplaceSyntaxTree`).** Two fixes, both needed (verified
  live - see the implementation note in `work/archive/bug-fix-designs-round7-10-2026-08.md`, WP-C, for why a
  try/catch around `ReportDiagnostic` was tried first and does NOT work: the throw happens inside
  Roslyn's own `GeneratorDriver.RunGeneratorsCore`, after every generator's callback has already
  returned): (1) **the actual crash fix** - `AttributeReading.AttributeLocation` now returns an
  *external* `Location` (file path + span, no `SourceTree`) instead of the tree-bound
  `SyntaxNode.GetLocation()` result, so there is no tree reference left to go stale across an
  incremental boundary in the first place; and (2) **restored per-transport `RegisterSourceOutput`**
  (undoing WP-5's merge of all 9 transports into one shared output, with a content-aware
  `IEqualityComparer` on each transport's `Collect()` - `ImmutableArray<T>`'s own equality compares by
  reference, not content) so an edit to one transport can't force re-emission of another's classes - the
  incrementality regression the same review flagged - while the `BENZ0001` collision check stays a
  single global view (`Combine`d into every transport's output) so a name shared *across* transports is
  still caught. See `work/archive/bug-fix-designs-round7-10-2026-08.md`, WP-C.
- **[RESOLVED] #32 — the `BENZ0001` name-collision `GroupBy` ran *after* filtering out triggers that
  carry their own `PendingDiagnostic` (e.g. `BENZ0002` for a CosmosDb trigger missing `DocumentType`),
  so a collision where one side was broken reported only that side's own diagnostic and silently
  shipped the other trigger under the shared name with no `BENZ0001` at all.** The collision check now
  runs over the *full* declared set, including an entry that carries its own diagnostic
  (`TriggerInfo.ForDiagnostic` always records the attempted `FunctionNameLiteral`, even though nothing
  will be emitted for it, precisely so this check can still see it) - both diagnostics now fire
  together when applicable. See WP-C.
- **[RESOLVED] #39 — only CosmosDb's `Read()` validated a required field (`BENZ0002` for missing
  `DocumentType`); the other five transports with a required binding value silently emitted an
  empty/invalid binding argument (e.g. `ServiceBusTrigger("", "")`) instead of failing the build.**
  Extended the same pattern to `DiagnosticDescriptors`: `BENZ0003` (ServiceBus - neither `QueueName` nor
  `TopicName` set), `BENZ0004` (EventHub - missing `EventHubName`), `BENZ0005` (Kafka - missing
  `Topic`), `BENZ0006` (Queue Storage - missing `QueueName`), `BENZ0007` (Blob Storage - missing
  `Path`). See WP-C.
- **[RESOLVED] #40 — `AttributeReading.NamedString` couldn't distinguish an *absent* `Name` (which
  correctly defaults) from an *explicitly-set* `""`/whitespace-only `Name`, across all 9 transports -
  the latter silently produced `[Function("")]`.** Added `AttributeReading.ValidateName`, called by
  every transport before its own required-field checks, which reports the new `BENZ0008` diagnostic for
  the explicit-empty case and leaves the absent case defaulting as before. See WP-C.
- **[RESOLVED] #42 — `ServiceBus.Read`'s `queue.Length > 0 ? ... : ...` silently preferred `QueueName`
  when *both* `QueueName` and `TopicName`/`SubscriptionName` were set on one attribute, discarding the
  topic with no diagnostic at all.** Added `BENZ0009` (warning, non-blocking) reported alongside the
  still-generated trigger, which keeps the same queue-wins precedence - only the silent discard is
  fixed, not the precedence itself. See WP-C.

### Tracked findings round 7–10, WP-U — Examples build regression (done)
Ruled in [`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-U — Examples
build regression (URGENT, land first)".
- **[RESOLVED] #68 — `Benzene.Examples.sln` failed to build (CS0535, ~18 files): WP-7a's `IHealthCheck`
  interface change to `ExecuteAsync(CancellationToken)` was swept across `src/` but not `examples/`.**
  All 18 example `IHealthCheck` implementers (`AwsMesh`, `AzureFunctionsMesh`, `GoogleCloudMesh`,
  `K8sMesh`, `Mesh` examples) updated to the new signature. See WP-U.

### Tracked findings round 7–10, WP-N — Resilience & correlation (done)
Decisions, rationale, and rejected alternatives are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-N — Resilience &
correlation".
- **[RESOLVED] #61 — nested `UseTimeout`: an OUTER deadline firing while inside an INNER wrap escaped
  as a raw `OperationCanceledException`/`TaskCanceledException`, not the `TimeoutException`
  `docs/resilience.md` promises.** The catch filter only recognized cancellation from *this* layer's own
  `cts.Token`, but in nested composition the exception's `.CancellationToken` is always the innermost
  live `CancellationTokenSource`'s token regardless of which layer's timer actually fired. Filter now
  gates on `!original.IsCancellationRequested` alone (the true host/ambient token this layer saw on
  entry never having fired) rather than an exact-token match. `TimeoutMiddleware.cs`.
- **[RESOLVED] #62 — `IdempotencyMiddleware` never threaded the ambient `CancellationToken` into
  `IIdempotencyStore.TryClaimAsync`/`CompleteAsync`/`ReleaseAsync`** (defaulted to
  `CancellationToken.None` at every call site) - the same gap WP-7(c) fixed for the sibling
  `ClaimCheckHydrateMiddleware`/`ClaimCheckOffloadMiddleware`, missed for this middleware (P8). Now
  resolves `ICancellationTokenAccessor` in the constructor (optional, DI-supplied via `TryGetService`)
  and reads `.CancellationToken` at the point of use for all three store calls.
  `IdempotencyMiddleware.cs`, `Extensions.cs`.
- **[RESOLVED] #63 — Polly's own unset `ShouldHandle` default already excludes
  `OperationCanceledException` from tripping a circuit breaker, but this repo's own retry-oriented
  test/doc pattern (`new PredicateBuilder().Handle<Exception>()`) reintroduces the bug if copy-pasted
  onto a breaker's `ShouldHandle`.** Added `Benzene.Resilience.Polly`'s
  `CancellationSafePredicateBuilderExtensions.ExcludingCancellation<TResult>()` (mirrors
  `RetryMiddleware`'s own documented default, `ex is not OperationCanceledException`) plus an explicit
  callout in `docs/cookbooks/polly-resilience.md`'s cancellation-caveat section warning that widening
  `ShouldHandle` beyond Polly's default can silently drop cancellation-safety.
- **[RESOLVED] #64 — an inbound `x-correlation-id` header value flowed completely unsanitized (no
  length cap, no control-character check) into log scopes (`ILogger.BeginScope`) and outbound headers —
  a caller-controlled CRLF-injection/log-forging and unbounded-length vector.** `CorrelationId.Set`
  (the interface's reference implementation) now caps length at 128 chars and rejects any value
  containing a control character (`\r`/`\n` included), silently keeping the existing (self-generated,
  by default) id on rejection rather than accepting the forged value - `ICorrelationId`'s "always has a
  value" contract holds either way. `CorrelationId.cs`, `ICorrelationId.cs`,
  `InboundCorrelationIdMiddleware.cs`. **Closes the previously-open `[DECISION] CR/LF response-header
  injection (defence-in-depth)` item**, moved below from "Open — maintainer decisions": that item held
  the risk unconfirmed because the affected values were "Benzene-/handler-sourced today"; this
  correlation-id path is directly caller-controlled, settling it.
- **[RESOLVED, was `[DECISION]`] CR/LF response-header injection (defence-in-depth)** — API-Gateway/
  self-host/AspNet response adapters still pass header values through without stripping CR/LF in
  general, but #64 above is the confirming instance: an inbound header value (correlation id) is
  directly caller-controlled and previously flowed unsanitized into a log scope and outbound headers.
  Sanitized at the point it enters the process (`CorrelationId.Set`) per #64. The broader adapter-level
  stripping this item originally scoped remains a defence-in-depth enhancement, not required now that
  the one confirmed live vector is closed at its source.

### Tracked findings round 7–10, WP-O — Observability & privacy-doc accuracy (done)
Decisions and rationale are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-O — Observability &
privacy-doc accuracy".
- **[RESOLVED] #54 — `UseW3CTraceContext`'s manually-started `"W3CTraceContext.Root"` span
  (`ActivityKind.Server`) was wrapped in a bare `using (activity) { await next(); }` with no try/catch,
  so it was never marked `Error` on a thrown exception** - the highest-visibility span, the one OTel
  backends key error-rate metrics off, unlike the sibling handler span
  (`ActivityMiddlewareDecorator`) and `UseTimer` (`Diagnostics/Timers/Extensions.cs`), both already
  correctly fixed for this bug class. Now wraps `next()` in try/catch, calling `AddException(ex)` +
  `SetStatus(ActivityStatusCode.Error, ex.Message)` before rethrowing, matching that pattern.
  `W3CTraceContextExtensions.cs`. (A related, lower-confidence, unconfirmed gap - `Tag()` running
  before the try/catch in `ActivityMiddlewareDecorator` - is noted with a comment at the call site
  rather than restructured; see the ruling.)
- **[RESOLVED] #55 — `docs/privacy-and-data-handling.md` falsely claimed Benzene "emits no framework
  log lines unless you explicitly add `UseLogResult(...)`/`UseLogContext(...)` middleware"**, but
  `MessageRouter` logs unconditionally (missing-topic/no-handler-found/type-mismatch warnings, and an
  unsuccessful-result warning that interpolates handler-authored `result.Errors` text) with zero
  middleware wired. Corrected the "What Benzene captures automatically" section to describe the
  router's baseline warnings, cross-referencing `docs/diagnosing-failures.md`'s "What you get with
  nothing wired" table (which already documented this correctly), and flagged that the
  unsuccessful-result line can carry handler-authored error text.

### Tracked findings round 7–10, WP-M — Validation: JsonSchema parity (done)
Ruled in [`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-M —
Validation: JsonSchema parity".
- **[RESOLVED / doc] #60 — `Benzene.JsonSchema`'s `DefaultJsonSchemaProvider` silently ignores
  `System.ComponentModel.DataAnnotations` attributes** (`[Required]`/`[Range]`/`[MinLength]`, ...) - the
  generator only understands `Json.Schema.Generation`'s own, differently-namespaced attribute set, so a
  DTO validated correctly by `Benzene.DataAnnotations`/`Benzene.FluentValidation` gets a type-shape-only
  check under `Benzene.JsonSchema`, with no warning. Documented the gap plainly in
  `src/Benzene.JsonSchema/docs/README.md` (a new "Gap" section) and in `docs/capability-matrix.md`'s
  Validation row, pointing to `SuppliedJsonSchemaCatalog` (hand-authored schema) and
  `Json.Schema.Generation`'s own attributes as the ways to get real constraint coverage from this
  package. (Ruling records this as the doc-only fix; a fuller fix projecting DataAnnotations attributes
  into schema keywords during generation remains open as future work, not tracked as a bug.)

### Tracked findings round 7–10, WP-D — Mesh host & UI robustness (done)
Ruled in [`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-D — Mesh
host & UI robustness".
- **[RESOLVED] #34 — `MeshTimeRangeResolver.ParseBound`'s `now ± span` threw `ArgumentOutOfRangeException`
  (`DateTimeOffset` range) for a relative count that is perfectly valid as a `TimeSpan` but pushes the
  result outside `DateTimeOffset`'s own representable window (e.g. `now-5000000d`) - a different overflow
  path from the already-fixed `ParseDuration`/`TimeSpan` overflow (#22), crashing `mesh:query:fleet`/
  `correlation` unconditionally on this input.** Live-verified (the crash reproduced, then stopped
  reproducing after the fix). `now ± span` is now wrapped in the same "absent, never throw" contract
  (P5): a caught `ArgumentOutOfRangeException` degrades to a null bound, exactly like an unparseable or
  `TimeSpan`-overflowing one. See WP-D.
- **[RESOLVED] #35 (security, P9) — `MeshDispatchGuardMiddleware`'s 128 KiB payload cap was enforced by
  reading the `Content-Length` header alone, which returns 0 ("absent") for a chunked
  `Transfer-Encoding` request - so an oversized chunked body on the bare-Kestrel `Benzene.Mesh.Host`
  sailed straight past the guard's own threat model (a compromised session) into the dispatch handler.**
  Live-verified (413 with `Content-Length` set over the cap vs. an unrefused chunked bypass before the
  fix). The check now measures the request's ACTUAL buffered body size (`HttpRequestBodyBuffer`, already
  populated ahead of every custom middleware by `BenzeneExtensions.UseHttp`'s
  `BufferRequestBodyMiddleware`) rather than trusting the header, falling back to the header only on a
  transport that never buffers (e.g. AWS API Gateway, where the whole body already arrives
  pre-materialized and `Content-Length` is trustworthy). Kestrel's own `MaxRequestBodySize` is now also
  set in `deploy/Mesh/Benzene.Mesh.Host/Program.cs`, tracking `MeshDispatchGuardOptions.
  DefaultMaxRequestBytes`, as defence-in-depth against the buffering itself being unbounded. See WP-D.
- **[RESOLVED] #36 — the mesh UI's sign-out `fetch` (`fetch(...).then(s=>s.json()).catch(()=>({}))`) had
  no `response.ok`/status check, so a failed logout (network error, an unexpected 500) looked identical
  to success and silently fell through to a page reload.** `src/Benzene.Mesh.Ui/mesh-ui.html`'s sign-out
  button now checks `response.ok` before treating the result as success (matching the pattern the
  refresh action `d0` in the same file already uses), and renders an inline error note
  (`.bz-refresh-note[data-tone=bad]`, the same convention the refresh control's own note uses) instead of
  reloading on a failure. See WP-D.
- **[RESOLVED] #72 (worth-fixing) — `MeshArtifactMiddleware.HandleAsync` called `_store.TryReadAsync`
  with no try/catch, but all three cloud artifact stores (S3/Blob/GCS) deliberately re-throw on a
  non-404 failure (a documented, tested contract) - a transient hiccup crashed this middleware, the mesh
  UI's primary read path, as a raw unhandled 500.** Live-verified. The read is now wrapped in try/catch;
  a store failure answers a clean `503` with a generic, no-detail-leakage JSON body
  (`{"error":"unavailable"}`, matching the convention `MeshRefreshGuardMiddleware.DenyAsync` already
  uses in this package) and logs server-side. See WP-D.
- **[RESOLVED] #37 (minor, P6) — `auth.dispatchRole` was accepted while `dispatch.enabled` stayed
  false, silently inert: the role check only ever runs against `DispatchPath`, which isn't a reachable
  endpoint at all when dispatch is disabled.** `MeshAuthGate.Validate` now rejects the combination at
  startup, naming both keys - the same fail-fast treatment this method already gives every other
  inert/unsatisfiable auth-config combination. See WP-D.

### Tracked findings round 7–10, WP-E — Mesh example parity + resolved-note correction (done)
Ruled in [`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-E — Mesh
example parity + resolved-note correction".
- **[RESOLVED] #41 — `examples/AzureFunctionsMesh`'s `POST /mesh/refresh` had no guard at all
  (unauthenticated ARM discovery + a Blob write on an anonymous POST), CONTRADICTING this file's own
  #21 resolved-note, which claimed AzureFunctionsMesh already "matched the guarded posture" alongside
  K8sMesh/GoogleCloudMesh. Re-verifying the other two named siblings found the note was wrong about
  THEM too: `examples/K8sMesh/Mesh/Startup.cs` and `examples/GoogleCloudMesh/Mesh/Startup.cs` also had
  no `UseMeshRefreshGuard` wired at all - so the #21 note's claim was false for all three examples it
  named, not just AzureFunctionsMesh.** `UseMeshRefreshGuard` (CSRF header + manifest-age throttle,
  `Topic = "mesh:refresh"`) is now wired into all three, mirroring `AzureMesh`/`AwsMesh`'s existing
  wiring; each README gained the same "Security posture" disclosure section AzureMesh's README already
  had. **#21's resolved-note is corrected above** (see its `[CORRECTED 2026-08-26, WP-E/#41]` line):
  its "K8sMesh/GoogleCloudMesh/AzureFunctionsMesh already match the guarded posture" claim was false at
  the time it was written for all three named examples - none of the three had `UseMeshRefreshGuard`
  wired until this fix. See WP-E, and `archive/bug-fix-designs-round7-10-2026-08.md`'s WP-E section for
  why the scope grew from "verify two examples" to "fix three".
- **[RESOLVED] #73 — `examples/AwsMesh/Mesh/MeshAggregateHandler` is triggered BOTH by an EventBridge
  schedule AND an on-demand `POST /mesh/refresh` HTTP endpoint on the same class, calling
  `_aggregator.RunOnceAsync(registry)`/`_store.PublishAsync("registry.json", ...)` directly with no
  gate - and because the two triggers run in SEPARATE Lambda execution environments (unlike the other
  four mesh examples, which run as one long-lived process), no in-process semaphore could serialize
  them.** `reserved_concurrent_executions = 1` is now set on the mesh Lambda in
  `examples/AwsMesh/deploy/main.tf` as a cheap platform-level serializer (AWS Lambda queues/rejects a
  concurrent invocation past the reservation rather than running two at once), paired with a documented
  residual-risk comment on the resource (what this does and does not protect against - an S3
  conditional-write/lease is named as the fuller fix if true single-flight is ever needed). See WP-E.

### Tracked findings round 7–10, WP-P — Core version-blindness (done)
Ruled in [`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-P — Core
version-blindness (shared root cause)".
- **[RESOLVED] #69 — `DefaultJsonSchemaProvider`/`SuppliedJsonSchemaProvider`'s `Get()` resolved the
  topic via a bare version-less `GetTopic(context)`, never consulting `IMessageVersionGetter<TContext>`
  — so for a topic with 2+ registered handler versions, request validation ran against whichever
  version `VersionSelector`'s unversioned max-by-ordinal fallback happened to land on, not the version
  the request actually declared, which could reject a genuinely valid request (a v1 payload validated
  against v2's schema).** Fixed at the shared root cause, not per call site: both providers now call
  `IMessageTopicGetter<TContext>.GetVersionedTopic(context, IMessageVersionGetter<TContext>?)` before
  `FindHandler`. See WP-P.
- **[RESOLVED] #70 — `ActivityMiddlewareDecorator`/`EnrichmentExtensions`/`XRayMiddlewareDecorator` had
  the identical bug: they tagged `benzene.handler`/annotated `benzene_handler` with the wrong
  (never-run) handler and `benzene.version`/`benzene_version` blank on every multi-version topic,
  undermining mesh trace-backed flow reconstruction.** Same fix as #69, applied to all three
  consumers. See WP-P.
- **Centralized, not five separate fixes.** `MessageRouter<TContext>` was already the one call site that
  got this right (combining `IMessageGetter<TContext>.GetTopic` with `IMessageVersionGetter<TContext>`
  before `FindHandler`); rather than teaching each of the other four consumers to duplicate that logic,
  the combination is now one shared extension method, `GetVersionedTopic`, on
  `IMessageTopicGetter<TContext>` (`Benzene.Abstractions.MessageHandlers.Mappers` — the interface
  `IMessageGetter<TContext>` already extends, so every existing call site keeps working unchanged).
  `MessageRouter` itself was refactored onto the same helper, so there is exactly one implementation of
  "resolve the topic the request actually declares" for every current *and future* by-topic consumer to
  call. `DefaultJsonSchemaProvider`/`SuppliedJsonSchemaProvider` take an optional (nullable, `= null`)
  constructor `IMessageVersionGetter<TContext>` for back-compat with existing direct constructions; the
  DI-resolved instance (`AddJsonSchema`/`AddSuppliedJsonSchemas`) always gets one, since every transport
  that registers message-handler dispatch already registers `IMessageVersionGetter<TContext>` (a hard
  dependency of `MessageRouter` itself). The diagnostics/XRay decorators resolve it via the existing
  `TryGetService` pattern (degrading to unversioned resolution, today's behaviour, if genuinely
  unregistered) rather than a hard constructor dependency, matching how they already resolve
  `IMessageGetter<TContext>`.

### Tracked findings round 7–10, WP-K — Health-check cancellation classification (done)
Decision, rationale, and the three-check implementation divergence (`GrpcHealthCheck`/`RabbitMqHealthCheck`
guard at the call site; `DynamoDbHealthCheck` gets `TcpHealthCheck`'s own explicit catch/rethrow; every
other affected check is fixed by the shared class alone) are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-K — Health-check
cancellation classification (P8 completion of WP-7)".
- **[RESOLVED] #50 — WP-7a's own requirement that every `IHealthCheck` forward its `CancellationToken`
  made a cancellation-swallowing bug newly reachable in ~10 backend checks** (`Sns`/`Sqs`/`EventBridge`/
  `ServiceBus`/`EventHub`/`QueueStorage`/`Kafka`/`RabbitMq`/`Grpc`/`HttpBenzeneMessage`/`DynamoDb`), each
  catching the generic `Exception` (including `OperationCanceledException`) and misclassifying a real
  timeout/shutdown as an ordinary transient dependency failure (`{"Error": "TaskCanceledException"}`) -
  indistinguishable from a genuinely dead dependency. `HealthCheckError.Classify` now re-throws an
  `OperationCanceledException` instead of classifying it, so `ExceptionHandlingHealthCheck` (which every
  check runs under via `HealthCheckProcessor`) reports the distinct `"Cancelled"` outcome instead - the
  same behaviour `TcpHealthCheck`'s own catch/rethrow already had. Live-verified against the real
  processor (real `SqsHealthCheck`/`ServiceBusHealthCheck`/`RabbitMqHealthCheck`/`DynamoDbHealthCheck`
  driven through `HealthCheckProcessor` with a timeout shorter than a hung SDK call). See WP-K.

### Tracked findings round 7–10, WP-F — Mesh fleet/usage backend fetch-isolation & bounds (done)
Decisions, rationale, and rejected alternatives are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-F — Mesh fleet/usage
backend fetch-isolation & bounds".
- **[RESOLVED] #74 — `CompositeMeshFleetReadModel.TraceAsync`/`CorrelationAsync` forwarded to the trace
  source with no try/catch, unlike the sibling `RecentFlowsAsync`/`TopicsFromUsageAsync` in the same
  class, which degrade-to-empty/null.** Both now catch and degrade to `null` (a single trace/correlation
  lookup reads as "not found" rather than throwing out of the composite), matching the class's own
  documented fetch-isolation rule. See WP-F.
- **[RESOLVED] #75 — `TempoServiceGraphTopologyBuilder.BuildAsync`'s 5 sequential PromQL calls had no
  fetch isolation; one Prometheus hiccup took down the whole `mesh:topology` handler.** Each query is
  now wrapped individually (`RunQueryAsync`): a failing call degrades just that edge-dimension to
  absent, the rest of the topology still builds. See WP-F.
- **[RESOLVED, verify live limit before shipping] #76 — `XRayTraceSource` never chunked a
  `GetTraceSummaries` window against a per-call time-range bound; the default `CorrelationLookback`
  (24h) went out as one call.** Windows are now chunked into contiguous ≤6h sub-queries
  (`FetchTraceSummariesAsync`/`ChunkWindow`), mirroring the id-axis chunking `BatchGetTraces` already
  had. The 6h bound is a conservative structural default (the review couldn't reach live AWS docs/an
  account to confirm `GetTraceSummaries`' exact per-call time-range limit) — verify against live
  docs/account before relying on the exact threshold; the chunking itself is correct regardless (an
  unnecessary chunk costs one extra API call, not a bug). See WP-F.
- **[RESOLVED, verify live page ordering before shipping] #77 — `GetRecentFlowsAsync`'s early-stop
  pagination heuristic (`summaries.Count >= limit * 4`) assumed `GetTraceSummaries` pages come back
  newest-first (unconfirmed); if it doesn't, the client-side top-N could bias toward stale traces under
  high volume.** Replaced with paging to window exhaustion or a generous hard cap (`limit * 20`),
  whichever comes first — order-agnostic by construction — with a logged warning (`ILogger?`, optional)
  when the cap is hit before the window was exhausted, rather than a silent truncation. The review
  couldn't reach a live X-Ray account to confirm actual page ordering; the fix is safe regardless of
  what that ordering turns out to be. See WP-F.
- **[RESOLVED] #78 — `LogsQueryUsageQuery` interpolated `options.MetricName`/dimension names directly
  into KQL with no escaping (config-time values only, so lower urgency, but fixed for defence-in-depth
  per the ruling).** New `EscapeKqlStringLiteral` escapes backslashes/quotes and rejects a configured
  value containing a line break (`ArgumentException`) before it's interpolated. See WP-F.
- **[RESOLVED] #79 — `JaegerTraceSource.SearchAcrossServicesAsync` fanned out one sequential HTTP GET
  per discovered service when `Services` was unset.** Parallelized via `Benzene.Core.Middleware`'s
  `BoundedFanOut`, capped by new `JaegerTraceSourceOptions.SearchConcurrency` (default 8). See WP-F.

### Tracked findings round 7–10, WP-L — Avro DoS + evolution, XML BOM, Newtonsoft divergence (done)
Decision, rationale, and the exact mechanism (why the depth guard has to live in `BoundedBinaryDecoder`,
not `AvroDatumConverter`) are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-L — Serialization:
Avro DoS + evolution, XML BOM, Newtonsoft divergence".
- **[RESOLVED] #56 — Avro serialize/deserialize recursed unboundedly on a self-referencing/deeply-nested
  schema, an *uncatchable* CLR stack overflow crashing the whole process from a <100 KB body (confirmed
  by two independent agents at ~15,000-16,000 nesting levels).** `BoundedBinaryDecoder` now also counts
  nested-read entry points (`ReadUnionIndex`, plus `ReadArrayStart`/`ReadMapStart` for an explicit
  schema's non-nullable nested collections - a superset of the ruling's literal "union-index" mechanism,
  to also cover the explicit-schema path) across a whole deserialize call and throws a catchable
  `AvroPayloadTooDeepException` once `AvroOptions.MaxDepth` (default 500) is exceeded, well below the
  real crash threshold. The serialize side (`AvroDatumConverter.ToDatum`/`ToUnionDatum`/`ToRecord`/
  `ToArray`) got the equivalent guard, threading an exact (not approximated) depth count through its own
  recursion. See WP-L.
- **[RESOLVED] #57 — Avro has zero schema-evolution support**, despite `Benzene.Avro/CLAUDE.md` having
  marketed the package on Avro-the-format's schema-evolution reputation; a removed middle field silently
  read the *next* field's bytes into the wrong property (no exception), a reordered field threw an
  opaque `IndexOutOfRangeException`. Landed as two commits per the ruling's doc-first instruction: (1)
  `CLAUDE.md` now states plainly that there is no schema-evolution support and why; (2)
  `AvroSerializer`'s deserialize path now detects both failure modes for the reflection/registered-schema
  path and throws a clear `AvroSchemaMismatchException` - a field-order mismatch that desyncs the byte
  stream is caught and rewrapped, and unconsumed trailing bytes after a successful read (a field-count
  mismatch) are checked explicitly. This is detection, not resolution - see WP-L and the corrected
  `CLAUDE.md` for what is and is not guaranteed. See WP-L.
- **[RESOLVED] #58 — `XmlSerializer.Deserialize(Type, string)` rejected a valid UTF-8-BOM-prefixed body**
  that ASP.NET Core's own `StreamReader`-based body-reading path accepts. A leading U+FEFF is now
  stripped before constructing the `XmlReader`, fixing every transport at once. See WP-L.
- **[RESOLVED] #59 — Newtonsoft and the default `System.Text.Json`-based serializer diverged on
  `NaN`/`Infinity` doubles** (Newtonsoft silently encoded them as strings; STJ threw `ArgumentException`
  unhandled). The default STJ serializer now sets `JsonNumberHandling.AllowNamedFloatingPointLiterals`;
  verified empirically (not assumed from the flag's name), this makes STJ emit the same quoted-string
  wire form (`"NaN"` etc.) Newtonsoft has always used, so the two engines now agree on the wire rather
  than one crashing. Documented in `Benzene.NewtonsoftJson/CLAUDE.md`. See WP-L.

### Tracked findings round 7–10, WP-A — RabbitMQ mandatory-publish coordinator hardening (done)
Ruled in [`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-A — RabbitMQ
mandatory-publish coordinator hardening" (WP-8's own new code — the riskiest code that round shipped, and
the first dedicated review of it found three issues).
- **[RESOLVED] #30 — the final `await tcs.Task.WaitAsync(cancellationToken)` in
  `RabbitMqMandatoryPublishCoordinator.PublishMandatoryAsync` sat outside the try/catch that called
  `Forget(tag, messageId)` on a failed publish, so a caller token firing while genuinely awaiting the
  broker's ack/nack/return (i.e. after the publish itself had already succeeded) leaked the pending-publish
  entry in `_byTag`/`_byMessageId` forever — confirmed via an executed leak probe.** The final wait is now
  wrapped in its own try/catch: cancellation forgets the entry before the `OperationCanceledException`
  propagates. Regression: `RabbitMqMandatoryPublishTest.PublishMandatoryAsync_CancelledWhileAwaitingBrokerOutcome_ForgetsThePendingPublish`.
- **[RESOLVED] #45 — nothing bounded how long a caller could wait for the broker's publish confirm; a
  stalled/unresponsive broker (confirms enabled but never firing) hung the caller forever.** Added an
  optional publish-confirm timeout (`TimeSpan?`, defaulting to
  `RabbitMqMandatoryPublishCoordinator.DefaultPublishConfirmTimeout` = 30s) threaded through
  `PublishMandatoryAsync` → `RabbitMqClientMiddleware` → `Extensions.UseRabbitMqClient` →
  `RabbitMqBenzeneMessageClient`. Composed with #30's fix as one `WaitAsync` on a token linked from the
  caller's token and the timeout (mirrors `Benzene.Resilience.TimeoutMiddleware`'s timer-vs-host-token
  distinction) — a timeout also forgets the pending-publish entry, surfacing as `TimeoutException` rather
  than a bare cancellation. Regression:
  `RabbitMqMandatoryPublishTest.PublishMandatoryAsync_BrokerNeverConfirms_TimesOutAndForgetsThePendingPublish`.
- **[RESOLVED] #33 — `_byMessageId[messageId] = pending` used indexer-overwrite, so a second mandatory
  publish sharing an already-in-flight `MessageId` silently stole the first publish's correlation entry,
  risking a later `Basic.Return` being misattributed to the wrong publish.** Changed to `TryAdd`; a
  duplicate now throws `InvalidOperationException` at publish time, before the message reaches the wire.
  Unreachable through the shipped `RabbitMqClientMiddleware` surface today (it always stamps a fresh GUID),
  but the coordinator's own contract invited it for a caller-supplied `MessageId`. Regression:
  `RabbitMqMandatoryPublishTest.PublishMandatoryAsync_DuplicateMessageIdAlreadyInFlight_ThrowsClearly`.

### Tracked findings round 7–10, WP-G — AWS client / health-check consistency (done)
Decisions and rationale ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-G — AWS client /
health-check consistency (P8)".
- **[RESOLVED] #43 — `AwsLambdaBenzeneMessageClient`'s fire-and-forget (`InvocationType.Event`) path
  unconditionally returned `Accepted` regardless of the invoke's actual `InvokeResponse.StatusCode` —
  the same bug WP-6(a)/#12 fixed in the sibling `UseAwsLambda<T>()`/`LambdaContextConverter<T>` pipeline,
  left unswept in this one.** `AwsLambdaClient.SendMessageAsync` now throws
  `AwsLambdaEventInvokeFailedException` when an `Event` invoke's `InvokeResponse.StatusCode` is not 2xx,
  symmetric with its existing `FunctionError` → `AwsLambdaFunctionErrorException` throw for the
  request/response branch; it flows through `AwsLambdaBenzeneMessageClient`'s existing catch and is
  reported as `ServiceUnavailable`, never a false `Accepted`. See WP-G.
- **[RESOLVED] #44 — `AwsLambdaHealthCheck`/`StepFunctionsHealthCheck` still ran an internal
  `Task.WhenAny`+`Task.Delay(10000)` timeout guard, the same shape WP-7(b)/#26 deliberately removed from
  `SqsHealthCheck` once ambient-token forwarding made the processor's own timeout wrap genuinely
  effective — left unswept on these two.** Both already forwarded the real ambient token into their SDK
  calls (`GetFunctionConfigurationAsync`/`DescribeStateMachineAsync`/`StartExecutionAsync`), so the
  guard was redundant; deleted on both, now relying purely on the processor's uniform timeout wrap,
  matching `SqsHealthCheck`'s/`SnsHealthCheck`'s/`EventBridgeHealthCheck`'s current shape. (The Active-mode
  Lambda ping via `AwsLambdaBenzeneMessageClient` still cannot forward a token into its own SDK call —
  that client has no `CancellationToken` overload — unchanged from before and out of this fix's scope,
  same caveat WP-7(b) already carried.) See WP-G.

### Tracked findings round 7–10, WP-I — gRPC null-response diagnostic symmetry (done)
Ruled in [`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-I — gRPC
null-response diagnostic symmetry".
- **[RESOLVED] #48 — `ProtobufJsonGrpcMessageAdapter.ConvertResponse`'s null-payload branch called
  `Activator.CreateInstance<TResponse>()` directly with no check, unlike the non-null branch (which
  calls `GetDescriptor` and throws a clear `BenzeneException` naming the offending type when
  `TResponse` isn't a real protobuf message) — a non-protobuf `TResponse` gave an opaque
  `MissingMethodException` instead.** The null branch now calls `GetDescriptor` first (throwing the
  same `BenzeneException` on failure) before constructing via `Activator.CreateInstance`. Unreachable via
  generated code today (protoc-emitted types always have a public parameterless constructor) — fixed for
  error-message symmetry/quality. See WP-I.

### Tracked findings round 7–10, WP-H — CI-gating tools & codegen correctness (done)
Decisions, rationale, and the CLI `--fail-on` flag convention are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-H — CI-gating tools &
codegen correctness (P10, P11)".
- **[RESOLVED] #46 — `EventServiceDocumentDeserializer` reused one `EventServiceDocumentBuilder`
  (hence one `SchemaBuilder`/`SchemaRepository`) across calls, and `SchemaBuilder.AddSchema` is
  first-write-wins, so calling `Deserialize()` twice on one instance with two documents sharing a
  schema name silently resolved the second document's schema to the first's.** Corrupted both
  `benzene diff` (`DiffCommand`) and `SchemaCompatibility.EnsureBackwardCompatible(string,string)` -
  both call `Deserialize()` twice on a shared instance - so a real breaking change (a removed
  property) was silently reported as no change. `Deserialize()` now builds a fresh builder per call.
  See WP-H.
- **[RESOLVED] #65 — `benzene healthcheck` printed the response body and returned unconditionally,
  never inspecting `isHealthy`.** Never failed CI regardless of target health, unlike its
  already-fixed `diff`/`profile-check` siblings. Now parses `isHealthy` and throws
  `HealthCheckFailedException` when false, gated by `--fail-on` (default `unhealthy`). See WP-H.
- **[RESOLVED] #66 — `benzene build`'s C# client generator had no `oneOf`/`anyOf` branch in
  `CSharpTypeName.GetName`, so a top-level polymorphic request/response schema fell through to
  `return openApiSchema.Type;` (null), emitting uncompilable `Task<IBenzeneResult<>>`.** Now typed
  as the union members' shared `allOf` base when discoverable, else `object` (always compiles). See
  WP-H.
- **[RESOLVED] #67 — `CSharpNameFormatter.Format` never called the existing
  `CodeGenHelpers.RemoveNonIdentifierCharacters()` helper, so a schema property name like
  `"order-id"` generated uncompilable `public string Order-id { get; set; }`.** Now wired in
  (`"order-id"` → `Orderid`). See WP-H.
- **[RESOLVED] #86 — `Benzene.CodeGen.Markdown`'s `MarkdownTypeBuilder` had the same `oneOf`-blindness
  as #66, rendering a polymorphic property blank (`payment: ` with nothing after it).** Now renders
  the shared base type name when discoverable via `AllOf`, else a `oneOf: {A|B}` union listing. See
  WP-H.
- **[RESOLVED] #87 — `Benzene.CodeGen.ApiGateway`'s `ApiGatewayBuilderV1` grouped HTTP mappings only
  by `Path`, not `(Method, Path)`, so two topics sharing a method+path emitted duplicate-key YAML
  (two `get:` blocks under one path) and a corrupted CORS header (`'GET,GET,OPTIONS'`).** `BuildCodeFiles`
  now fails loudly with a `BenzeneException` on a method+path collision, mirroring
  `ReflectionHttpEndpointFinder`'s own duplicate-route check; `BuildOptions`'s CORS verb list is also
  `.Distinct()`ed as defense-in-depth for direct callers. See WP-H.

### Tracked findings round 7–10, WP-J — Schema comparer discriminator matching + coverage (done)
Decision, rationale, and rejected alternatives are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-J — Schema comparer
discriminator matching + coverage".
- **[RESOLVED] #53 — a discriminator mapping's *coverage* of a `$ref`'d `oneOf`/`anyOf` variant
  changing between baseline and current (e.g. a mapping entry newly added for a variant that was
  previously unmapped, nothing else about the variant changing) made `VariantKey` produce `disc:X` on
  the mapped side and `ref:X` on the unmapped side for the SAME logical variant — a key mismatch that
  made the pairwise matcher report a spurious `UnionVariantRemoved`+`UnionVariantAdded` pair, Breaking
  in either direction per `SchemaCompatibilityRules`, so a harmless additive discriminator-mapping edit
  failed a compatibility gate.** Matching priority in both twin comparers
  (`SchemaCompatibilityComparer`/`JsonSchemaComparer`) is now `$ref` target name first (when the member
  has one — a `$ref` already uniquely and stably identifies the target component, regardless of mapping
  coverage), then the discriminator mapping value (inline members only, where there is no `$ref` name to
  prefer), then position. The pre-existing reordered-and-fully-mapped-`$ref`'d-variants case
  (`OneOfDiscriminator_ReorderedVariants_MatchByDiscriminatorNotIndex`) still matches correctly under
  the new priority, since both sides now key on the same `$ref` name regardless of order. See WP-J.
- **[RESOLVED] #49 — `oneOf`+`allOf`-both-present and nested-`oneOf`-within-`oneOf` schemas were
  correct (verified by direct execution in the review round) but had no regression test.** Two corpus
  cases added to the shared-corpus parity test, run against both comparers: a schema carrying both
  `oneOf` and `allOf`, each losing a member (both `UnionVariantRemoved` findings asserted); and an outer
  `oneOf` variant that is itself a `oneOf`, losing an inner variant (both the outer
  `UnionVariantChanged` and the inner `UnionVariantRemoved` asserted). See WP-J.

### Tracked findings round 7–10, WP-R — Testing infrastructure & the coverage blind spot (done)
Ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-R — Testing
infrastructure & the coverage blind spot".
- **[RESOLVED] #81 — seven `*.TestHelpers` packages (`Benzene.Azure.EventHub.TestHelpers`,
  `Benzene.Azure.ServiceBus.TestHelpers`, `Benzene.Kafka.Core.TestHelpers`,
  `Benzene.RabbitMq.TestHelpers`, `Benzene.Azure.Function.QueueStorage.TestHelpers`,
  `Benzene.Azure.Function.EventGrid.TestHelpers`, `Benzene.GoogleCloud.Functions.Http.TestHelpers`)
  had their `.csproj` in `Benzene.sln`'s build graph but zero coverage from its *test* graph — no test
  project in the solution referenced any of them, so they were exercised only by
  `templates/content/*/BenzeneStarter.Tests` (a separate solution) against **published NuGet
  packages**, never against current `main` source. This is the blind spot that let #80 land
  unnoticed.** New project `test/Benzene.TestHelpers.SmokeTest` references all seven and exercises one
  basic scenario from each (envelope/topic/body shape for the message-builder extensions;
  method/path/header/body for the Google Cloud `HttpContextBuilder`), added to `Benzene.sln`. See
  WP-R.
- **[RESOLVED] #80 — `AsQueueStorageBenzeneMessage<T>(source, ISerializer)` serialized the *whole*
  envelope (`BenzeneMessageRequest { Topic, Headers, Body }`) with the caller-supplied serializer,
  crashing for a non-JSON serializer (e.g. `Benzene.Xml.XmlSerializer`) because `Headers` is an
  `IDictionary<string,string>` interface, unserializable by `System.Xml.Serialization.XmlSerializer` -
  unlike production (`BenzeneMessageQueueStorageHandler.TryExtractRequest`), which always deserializes
  the envelope with the DI-resolved default serializer regardless of the body's own format.** Now
  matches the correct sibling `AsEventHubBenzeneMessage`'s pattern exactly: the envelope is always
  fixed JSON, only `Body` uses the passed serializer. Lived exactly in #81's blind spot. See WP-R.

### Tracked findings round 7–10, WP-S — Hosting / HTTP adapters (done)
Ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-S — Hosting / HTTP
adapters".
- **[RESOLVED] #88 — `BenzeneHostedServiceAdapter` never observed whether the wrapped worker's own
  task faulted, so a dead/crashed worker left the process "up" with zero signal (no log, no
  propagation), unlike `BackgroundService`'s `BackgroundServiceExceptionBehavior` (default: stop the
  host).** The adapter now observes the worker's task for an unhandled fault the moment it happens (a
  fire-and-forget continuation started in `StartAsync`, not gated on the host later calling
  `StopAsync`), logs it at `Critical` via an optional `ILogger<BenzeneHostedServiceAdapter>`, and calls
  an optional `IHostApplicationLifetime.StopApplication()` - matching `BackgroundService`'s modern
  default of stopping the whole host on an unhandled worker fault. Both dependencies are optional
  constructor parameters (default `null`) since not every construction path (e.g.
  `BenzeneWorkerExtensions.BuildHostedService(this IBenzeneWorkerBuilder)`) has a resolver to supply
  them; `HostBuilderExtensions.UseBenzene<TStartUp>()` - the one path that does - now wires both. See
  WP-S.
- **[RESOLVED] #89 — `ApiGatewayHttpRequestAdapter` (v1) passed AWS's raw, case-sensitive,
  original-casing header dictionary straight through, unlike `AspNetHttpRequestAdapter`
  (`.ToLowerInvariant()` on every key) and `ApiGatewayV2Context.CombinedHeaders()`
  (`StringComparer.OrdinalIgnoreCase`) - every consumer (`BasicAuthMiddleware`,
  `OAuth2BearerMiddleware`, `CorsMiddleware`, the OIDC middleware) reads headers by lowercase literal
  key, relying on `HttpRequest.AsLowerCase()` having been called first, so a v1-API-Gateway-triggered
  request silently missed auth/CORS header lookups unless someone remembered to normalize.**
  `ApiGatewayHttpRequestAdapter.Map` now lower-cases header keys at the source, matching
  `AspNetHttpRequestAdapter`'s exact pattern - a raw `Map()` result's `TryGetValue("authorization",
  ...)` now succeeds without `AsLowerCase()`. See WP-S.
- **[RESOLVED] #90 — `AspNetRequestEnricher` took the first value for a repeated query-string key
  (`?status=active&status=inactive`), while `ApiGatewayRequestEnricher`/`ApiGatewayV2RequestEnricher`
  passed `QueryStringParameters` through as-is, which per AWS's payload shapes keeps only the LAST
  value for v1's single-value map and comma-joins repeated values into one string for v2 - so a
  repeated query key bound differently across transports for the identical route/handler.**
  Standardized on first-value-wins everywhere (matching AspNet, the more common convention): a new
  `QueryStringFirstWinsMapper` picks the first value per key from v1's
  `MultiValueQueryStringParameters` (falling back to the single-value map when the multi-value one is
  absent) and the first comma-separated segment from v2's already-joined value. See WP-S.
- **[RESOLVED] #91 — `ReflectionHttpEndpointFinder`'s duplicate-route startup check grouped by
  `new { Method, Path }` with case-sensitive string equality, but `RouteFinder`/`CompiledRoutePath`
  match case-INSENSITIVELY at runtime, so `[HttpEndpoint("GET","/Users")]` and
  `[HttpEndpoint("get","/USERS")]` on two different handlers weren't flagged as a duplicate at
  startup - the second silently became unreachable dead code instead of the documented fail-fast
  `BenzeneException`.** The `GroupBy` key now case-folds `Method`/`Path` (the stored values are
  unchanged). See WP-S.

### Tracked findings round 7–10, WP-T — MapReduce / SchemaRegistry / ResponseEvents (done)
Ruled in [`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-T —
MapReduce / SchemaRegistry / ResponseEvents".
- **[RESOLVED] #92 — `ScatterGatherAsync` discarded all per-shard exception detail: `Outcome.Failed`
  carried only the shard, so a thrown `ScatterGatherPartialFailureException` had `InnerException ==
  null` and no per-shard reasons, making "which shard failed and why" undiagnosable.** Every failed
  shard now carries its own reason: the new `FailedShard<TShard>` (`Shard` + `Exception? Reason`)
  replaces the bare shard in both `ScatterGatherResult<,>.FailedShards` (the `BestEffort` path) and
  `ScatterGatherPartialFailureException.Failures` (the `ThrowOnAnyFailure` path), and the exception
  aggregates every non-null reason onto `InnerException` as an `AggregateException` (plus a per-shard
  message listing shard + failure) so ordinary .NET exception inspection finds them too. `Reason` is
  `null` when a shard failed by returning an unsuccessful result rather than throwing (nothing to
  carry). Regression test (`ScatterGatherTest.ThrowOnAnyFailure_CarriesEachFailedShardsDistinctException`)
  sends 10 shards where 5 throw 5 *different* exception types concurrently and asserts every failed
  shard's own reason is captured and distinguishable, not just the shard identity or the count. See WP-T.
- **[RESOLVED] #93 — `InMemorySchemaRegistryClient.RegisterAsync` crashed with a raw
  `ArgumentNullException: Value cannot be null. (Parameter 'key')` (a `Dictionary<string,...>`
  null-key lookup) when `schema.Subject` was null, instead of a clear validation error (P9).**
  `SchemaDefinition`'s constructor now guards `Subject`/`Schema` for non-null/non-empty/non-whitespace,
  throwing a descriptive `ArgumentException` at construction time - before a bad value can travel deep
  into the registry. Regression test (`SchemaDefinitionTest`) covers null/empty/whitespace for both
  parameters. See WP-T.
- **[RESOLVED, doc] #94 — `CrudConventionResponseEventMapping` combined with an overlapping explicit
  `Map(...)` call double-publishes the same event topic** (confirmed: `.Map("order:create",
  "order:created")` + `.MapCrudConvention()` produce TWO `ResponseEventPublication`s for one handled
  message). Consistent with `ResponseEventMappings.Resolve`'s documented "multiple matches fan out"
  behaviour, so not a broken contract - no code change. Added a doc callout to
  `MapCrudConvention()`'s XML doc `<remarks>` and to `docs/cookbooks/response-as-event.md`'s CRUD
  convention section warning against registering an explicit `create`/`update`/`delete` mapping that
  overlaps a CRUD-convention-covered topic, since both will fire. See WP-T.
- **[RESOLVED, doc] #95 — `SchemaRegistrySerializer.Deserialize(Type, ReadOnlySpan<byte>)` decodes and
  discards the embedded Confluent wire-format schema id without validating it against the caller's
  requested `Type`, so bytes framed under one schema's id silently deserialize as a different type
  with no error.** Consistent with the class's documented scope (producer-side interop framing, not
  registry-driven consumer-side schema resolution - there is genuinely no id-to-type reverse map to
  check against even in principle), so no code change. Added a doc callout to the class's XML doc
  `<remarks>` and to `src/Benzene.SchemaRegistry.Core/CLAUDE.md` stating plainly that the embedded
  schema id is NOT validated against the caller's expected type. See WP-T.

### Tracked findings round 7–10, WP-Q — Autofac DI adapter parity (done)
Decisions, rationale, and rejected alternatives for all four are ruled in
[`bug-fix-designs-round7-10-2026-08.md`](archive/bug-fix-designs-round7-10-2026-08.md) §"WP-Q — Autofac DI adapter
parity (P8 — the alternate container must match the reference)".
- **[RESOLVED] #82 — `AutofacBenzeneServiceContainer.IsTypeRegistered` read Autofac's
  `ComponentRegistryBuilder`, which stays empty until `ContainerBuilder.Build()` runs, but is called
  during registration (well before `Build()`) by every `TryAdd*` extension - so it always returned
  `false`, silently turning every `TryAdd*` into an unconditional last-write-wins `Add*` and breaking the
  idempotency contract `AddMessageHandlers`' finder-lock-in fix (WP-7(a)'s sibling, `AddMessageHandlers`
  round-5/6) depends on.** Now backed by an explicit `HashSet<Type>` maintained by every `AddXxx`/
  `AddServiceResolver` call, mirroring `MicrosoftBenzeneServiceContainer`'s live `IServiceCollection`
  check. See WP-Q.
- **[RESOLVED] #83 — `CreateServiceResolverFactory()` called `ContainerBuilder.Build()`, which throws on
  a second call per builder; `Benzene.Grpc.AspNet`'s `GrpcMethodHandlerFactory.Create()` calls it on
  every gRPC request, so the second request ever handled with Autofac wired in threw.** The `IContainer`
  is now built once, lazily, on `AutofacBenzeneServiceContainer`'s first `CreateServiceResolverFactory()`
  call; every call (including the first) returns a cheap, non-owning `AutofacServiceResolverFactory`
  wrapping that already-built container. See WP-Q.
- **[RESOLVED] #84 — the single-`IComponentContext`-arg `AutofacServiceResolverAdapter` constructor
  (used by `AddServiceResolver()`'s registration, and by every `AddScoped/AddTransient/AddSingleton
  (Func<IServiceResolver,T>)` overload) never set the `IServiceResolverFactory` field, so a
  constructor-injected `IServiceResolver` asking for its own `IServiceResolverFactory` hit a raw
  `InvalidOperationException` instead of the enriched `BenzeneResolutionException`.** The adapter now
  builds one lazily on first use (mirrors `MicrosoftServiceResolverAdapter.ResolverFactory`'s `??=`
  pattern), wrapping the ambient scope it already has - no container `Build()` involved, so this can't
  collide with #83's fix. See WP-Q.
- **[RESOLVED] #85 — `AutofacServiceResolverFactory` didn't implement `IAsyncDisposable`, unlike
  `MicrosoftServiceResolverFactory`.** Added; disposes the owned container asynchronously when the
  factory owns one (a non-owning factory's `DisposeAsync` is a no-op, matching its `Dispose`). See WP-Q.

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
