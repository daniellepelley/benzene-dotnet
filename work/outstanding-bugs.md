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

> **Tracked findings, 2026-08-26 (round 10) — all fixed.** The round-10 review pass (task board #96)
> produced 22 evidence-backed findings (tasks #98–#119), successor to the round-7–10 pass above. All
> ten work packages (WP-V, WP-W, WP-X, WP-Y, WP-Z, WP-AA, WP-AB, WP-AC, WP-AD, WP-AE) landed and were
> pushed to `main` via 10 merge commits. Their design decisions, rationale, and the WP-V/
> `BenzeneMessageGetter` divergence (and the DI-deadlock finding that forced it) remain ruled in
> **[`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md)** (now
> archived, stamped with the landing commits); consult it before touching any of this code again so a
> decision made here doesn't get silently re-litigated.

> **Tracked findings, 2026-08-26/27 (round 11) — all 62 fixed.** The round-11 review pass (task board
> #121–#183: six parallel agents over event sourcing, rate limiting/cache, mesh discovery, less-common
> AWS/GCP transports, the spec/descriptor/CloudService pipeline, and the auth adapters) produced 62
> evidence-backed findings (tasks #121–#182). All eight work packages (WP-EventSourcing,
> WP-MeshDiscovery, WP-Transports, WP-Cache, WP-AuthCore, WP-RateLimiting, WP-AuthOidc,
> WP-CodeGenSchema) landed and were pushed to `main` via 8 merge commits. Their design decisions,
> rationale, and the three deliberate scope-downs (#135, #141, #171 — each recorded as its own
> `[DECISION]` below) remain ruled in
> **[`bug-fix-designs-round11-2026-08.md`](archive/bug-fix-designs-round11-2026-08.md)** (now
> archived, stamped with the landing commits and the re-verified baseline); consult it before touching
> any of this code again so a decision made here doesn't get silently re-litigated.

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

### Tracked findings rounds 12–14, WP-M — CodeGen.ApiGateway/Markdown escaping + guards (done)
Ruling in [`bug-fix-plan-rounds12-14-2026-08.md`](archive/bug-fix-plan-rounds12-14-2026-08.md) §"WP-M". Direct
continuation of #86/#87 above — same bug class, reached through different inputs.
- **[RESOLVED] #211 — `ApiGatewayBuilderV1`'s duplicate-route guard (`BuildCodeFiles`) grouped on the
  raw `Method`, unlike the `ReflectionHttpEndpointFinder` guard it mirrors (which explicitly
  case-folds `Method`, with a comment about this exact risk).** Two topics mapped to `"GET"` and
  `"get"` for the same path passed the guard silently and then both emitted a `get:` block under the
  same path (`BuildVerb` always lower-cases the emitted verb) — the identical duplicate-key YAML
  shape #87 fixed, reached via verb casing instead of identical casing. The grouping key now folds
  `Method` with `ToLowerInvariant()`, exactly like `ReflectionHttpEndpointFinder`; the thrown
  message still reports the first entry's original-cased `Method` so the common identical-casing
  case reads unchanged. See WP-M.
- **[RESOLVED] #212 — `ApiGatewayBuilderV1` interpolated user-controlled strings (topic names, the
  path-derived `tags:` entry, the configured CORS allow-headers value) straight into the generated
  YAML with no escaping.** A `"` in a topic name broke the double-quoted `summary:` scalar it landed
  in; a `: ` surviving `CreateTag`'s title-casing into the unquoted `tags:` sequence item made that
  item parse as a nested mapping instead of a scalar — both produced YAML a real parser rejects, the
  same root cause as #87 reached through different adversarial content instead of a structural
  duplicate. Fixed by routing every such interpolation through a small escaping helper
  (`YamlValueEscaping`, `src/Benzene.CodeGen.ApiGateway/YamlValueEscaping.cs`) instead of raw string
  interpolation: `QuoteSingle` always wraps a value in a single-quoted YAML scalar (doubling internal
  `'`s — the only escape a single-quoted scalar has, and sufficient for arbitrary content), used for
  the `tags:` sequence item (both `BuildOptions` and `BuildVerb`, previously emitted bare/unquoted);
  `EscapeForDoubleQuoted` escapes `\` and `"` for embedding inside a double-quoted scalar the call
  site already wraps in literal `"..."`, used for `summary:`'s topic and the two
  `Access-Control-Allow-Headers` header lines' `AllowedHeaders` value (preserving their existing
  double-quoted/AWS-required-single-quote-literal shape rather than changing it). Verified by
  actually loading the generated YAML with a real parser (`YamlDotNet`, added as a pinned dev
  dependency to `test/Benzene.Core.Test` — it was already present transitively via
  `ByteBard.AsyncAPI.NET.Readers` at the same version, so this adds nothing new to the dependency
  graph) rather than eyeballing the output. **Flag for reuse:** `YamlValueEscaping` is a small,
  self-contained, dependency-free static helper (no code shared with `ApiGatewayBuilderV1` beyond
  being in the same package) — round 15's WP-F fixes the same bug class in the Terraform/HCL
  generator (#244); if that generator's escaping need is YAML rather than HCL-specific, lifting
  `QuoteSingle`'s single-quoted-scalar approach (or the file itself, generalized and moved to
  `Benzene.CodeGen.Core`) may be worth it, though no code is shared as of this fix. See WP-M.
- **[RESOLVED] #213 — `MarkdownTypeBuilder.MapProperty` dereferenced an array schema's `Items`
  (`Items.Reference`/`Items.Type`) with no null check, throwing `NullReferenceException` for a
  hand-authored schema with `Items == null`, unlike the sibling `GetPropertyTypeName` (reached via
  the same method's final fallback branch), which already null-checks the equivalent case and
  renders a `"Void"` placeholder.** Added the same null check to `MapProperty`'s array branch
  (`openApiSchema.Items != null`, alongside the existing `Reference`/`Type` checks); a null `Items`
  now falls through to the method's generic fallback, which calls `GetPropertyTypeName` and renders
  the same `Void[]` placeholder the sibling method's guard already produces for a null schema, rather
  than throwing. Not reachable through Benzene's own `SchemaBuilder`-produced schemas, but the method
  is public and callable with any hand-authored schema. See WP-M.

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
- **[RESOLVED] #98 — `IMessageGetter`/`ResolvedTopicCache` served a version-blind topic to every
  non-router consumer** (confirmed live: a `benzene-version: v2` header through `UseMeshTrace(...)`
  exported `TopicVersion = null`; `Benzene.CloudService`, `Benzene.HealthChecks` and
  `Benzene.Auth.Core` read plain `GetTopic()` too and were equally blind — `Benzene.Diagnostics`'s
  `ActivityMiddlewareDecorator`/`EnrichmentExtensions` and the XRay decorator were already
  self-joining via `GetVersionedTopic` per WP-N/#70, so those were unaffected). **Fixed at the getter
  layer, not per-consumer, per the WP-V ruling:** `MessageGetter<TContext>.GetTopic` now joins the
  topic with the optionally-registered `IMessageVersionGetter<TContext>` (via the shared
  `GetVersionedTopic` helper, WP-P) and caches the JOINED topic in `ResolvedTopicCache`;
  `MessageRouter<TContext>` no longer takes its own `IMessageVersionGetter<TContext>` dependency and
  simply consumes `_messageGetter.GetTopic(context)`. **Scope extension beyond the generic facade:**
  `BenzeneMessageGetter` (`Benzene.Core.MessageHandlers.BenzeneMessage`) implements
  `IMessageGetter<BenzeneMessageContext>` directly and is registered ahead of the open-generic
  `MessageGetter<TContext>` facade (a closed-type DI registration always wins over an open-generic
  one, verified empirically) - so BenzeneMessage, the transport nearly every test in this repo uses,
  would otherwise have stayed version-blind even after the facade fix. `BenzeneMessageGetter` got the
  identical optional version-getter + `ResolvedTopicCache` join, reusing `GetVersionedTopic` via a
  small internal raw-topic adapter (to avoid the extension method's `GetTopic()` call re-entering
  `GetTopic()` itself). Also fixed the stale `MessageRouter` comment (:105-114) claiming every
  built-in topic getter converts an unresolvable topic to the `"<missing>"` sentinel — false for
  EventGrid/QueueStorage/Timer, which return a null `ITopic` (the ValidationError-vs-NotFound
  asymmetry this causes is an existing `[DECISION]`, unchanged). Tests: `MeshTraceVersionJoinTest`
  (resurrects the reviewer's `benzene-version: v2` + `UseMeshTrace` probe, red before / green after),
  `MessageGetterVersionJoinTest` (join, per-message caching, no-version-getter-registered
  degradation, preset-wins, missing-topic-id short-circuit), and `MessageRouterVersionWiringTest`
  rewritten for the router's new pass-through contract. See WP-V.

### Tracked findings round 10, WP-Z — API Gateway request adapter headers (done)
Ruled in [`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) §"WP-Z — API Gateway
request adapter headers".
- **[RESOLVED] #105 — `ApiGatewayHttpRequestAdapter.Map` (v1) built its `Headers` result with a
  plain-ordinal `Dictionary` (via `.ToDictionary(...)`), not `StringComparer.OrdinalIgnoreCase` -
  breaking `HttpRequest.Headers`'s documented case-insensitive contract for any lookup key not
  already in the adapter's own lower-cased form - and left `Method`/`Path` able to come back `null`
  from a hand-built payload or health-ping-shaped event, since `APIGatewayProxyRequest` is
  nullable-oblivious on the wire.** Audited every in-repo consumer of `HttpRequest.Headers` first
  (`src/`, `test/`): every one either calls `HttpRequest.AsLowerCase()` before reading (itself
  lower-casing all keys and consulting them by lowercase literal) or does a manual
  `StringComparison.OrdinalIgnoreCase` scan (`MeshRefreshGuardMiddleware`/`MeshDispatchGuardMiddleware`,
  both already null-checking `Headers` defensively) - so no in-repo consumer reads `Map()`'s raw
  result with an arbitrary-cased indexer/`TryGetValue`, and this closes a **latent** contract
  violation for `Headers`'s comparer, not an already-observable one. The `Method`/`Path` half is a
  **live** NRE risk though: `CorsMiddleware.HandleAsync` calls `httpRequest.Method.ToLowerInvariant()`
  unconditionally, so a v1 event with no `httpMethod` field (confirmed possible - the wire type
  carries no `NullableAttribute`/`NullableContextAttribute`, i.e. it's nullable-oblivious, not
  nullable-annotated-non-null) reaches that call as `null` and throws today. `Map` now accumulates
  headers into a `new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)` via a first-wins
  `TryAdd` loop (no existing `DictionaryUtils` helper fit this exact "single dict, case-insensitive,
  key-transform" shape, so a small loop matching the `Replace`/`FilterAndReplace` pattern was written)
  instead of `ToDictionary` (which threw `ArgumentException` on a lower-cased key collision), and
  `Method`/`Path` default to `string.Empty` when the wire value is `null`. Four regression tests in
  `ApiGatewayHeaderCasingAndQueryStringTest`: mixed/upper/lower-case lookups all succeed against one
  mapped request, two case-colliding header names resolve first-wins without throwing, and null
  `Method`/`Path` map to `string.Empty` without throwing. See WP-Z.

### Tracked findings round 10, WP-AC — health-check processor + adapters (done)
Ruling, rationale, and the red/green test conventions are in
[`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) §"WP-AC — health-check
processor + adapters (#111, #112, #113, #114)".
- **[RESOLVED] #111 — `CachingHealthCheckProcessor` had no single-flight guard: 50 concurrent
  cold-cache callers produced 50 full inner runs, recurring at every TTL expiry.** Added a per-key
  single-flight guard (`ConcurrentDictionary<string, Lazy<Task<IBenzeneResult>>>` with
  `LazyThreadSafetyMode.ExecutionAndPublication`), with the in-flight entry removed once its run
  settles (success or failure) so a faulted run doesn't poison later calls and the next cache miss
  after TTL expiry gets a fresh single-flight window. Corrected the class's XML remark, which
  previously (wrongly) blessed the stampede as "a couple of times concurrently - acceptable". The
  reviewer's 50-concurrent-caller repro (a gated inner processor) now asserts exactly one inner
  execution.
- **[RESOLVED] #112 — `HttpPingHealthCheck` lost the `Url` and dependency identity specifically on
  the "endpoint didn't respond at all" failure mode** (connection-refused escaped as
  `HttpRequestException` to the generic `ExceptionHandlingHealthCheck` decorator, which has no `Url`
  or dependency to report). `ExecuteAsync` now catches `HttpRequestException` and returns a failed
  result carrying `Url`, the dependency entry, and the exception type name - mirroring the EF/SNS/SQS
  checks - with `catch (OperationCanceledException) { throw; }` ahead of it so ambient/timeout
  cancellation still propagates for the "Cancelled" classification (#114's contract).
- **[RESOLVED] #113 — a throwing `Timeout`/`IsNonCritical`/`Type` health-check property getter
  crashed the entire `PerformHealthChecksAsync` aggregation, losing every other, healthy check's
  result with it** (the reads lived outside any try/catch, inside the `Task.WhenAll` selector).
  `HealthCheckProcessor.RunTimedAsync` now reads every per-check member inside its own guarded scope:
  `Type` is snapshotted once up front (so a later re-read through a decorator's own catch path can't
  re-throw), and `Timeout`/`IsNonCritical` plus the check's execution are wrapped in a try/catch that
  degrades to a `Failed` result for that one check on any exception instead of propagating to
  `Task.WhenAll`.
- **[RESOLVED] #114 — EF health checks (`DatabaseConnectionHealthCheck`, `DatabaseHealthCheck`)
  swallowed `OperationCanceledException` as an ordinary connection/migration failure, defeating
  `ExceptionHandlingHealthCheck`'s dedicated `"Cancelled"` classification** (the WP-K/#50 contract).
  Added `catch (OperationCanceledException) { throw; }` before the catch-all in both files'
  `TryConnect`/`TryGetAppliedMigrationsAsync`. **Scope extension** (grepped every `IHealthCheck`
  implementation for the same broad-catch shape not already covered by `HealthCheckError.Classify`):
  the same one-line rethrow was added to `Benzene.Clients.HealthChecks/ClientHealthCheck.cs` and
  `Benzene.Cache.Core/CacheHealthCheck.cs`, which had the identical unguarded
  `catch (Exception ex)` shape. `Benzene.HealthChecks.Disk/DiskHealthCheck.cs` and
  `Benzene.HealthChecks/MemoryHealthCheck.cs` were audited and left as-is: both are fully synchronous
  (no cancellable I/O reachable from their `try` blocks - `DriveInfo`/`GC.GetGCMemoryInfo()` never
  observe a `CancellationToken`), so the same catch shape there is not reachable for a genuine OCE and
  a rethrow guard would be dead code. Every other `IHealthCheck` implementer already either routes
  through `HealthCheckError.Classify` (which re-throws OCE) or has its own explicit
  catch/rethrow (`TcpHealthCheck`, `DynamoDbHealthCheck`, `RabbitMqHealthCheck`, `GrpcHealthCheck`) -
  confirmed clean, no change needed.

### Tracked findings round 10, WP-W — Validation status-mapping contract (done)
Ruled in [`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) §"WP-W —
validation status-mapping contract".
- **[RESOLVED] #99 — `ValidationStatusAttribute`/`IValidationStatusMapper` silently ignored by two
  of the three validation adapters.** The mechanism lives in the shared `Benzene.Abstractions.Validation`
  package (documented as the way to override a failed validation's result status) but only
  `Benzene.FluentValidation`'s `DefaultValidationStatusMapper` read it; `Benzene.DataAnnotations`'s
  and `Benzene.JsonSchema`'s middlewares hard-wired `IDefaultStatuses.ValidationError`/the built-in
  literal directly and didn't reference the abstractions package at all. Wired an optional
  `IValidationStatusMapper` into both (`ValidationMiddleware<TRequest,TResponse>.GetValidationStatus`
  in `Benzene.DataAnnotations`; the `SetValidationErrorAsync` call site in `Benzene.JsonSchema`'s
  `JsonSchemaMiddleware<TContext>`) - each now computes its failure status at exactly one call site,
  delegating to a registered mapper (honouring `[ValidationStatus]` on the resolved handler type) and
  falling back to today's behavior (`ValidationError`) when no mapper is registered. Added the
  `Benzene.Abstractions.Validation` project reference both packages lacked. Neither package ships its
  own `IValidationStatusMapper` implementation - only `Benzene.FluentValidation`'s
  `DefaultValidationStatusMapper` does, so an app installs that (or a custom mapper) once and all
  three adapters honour it identically.
- **[RESOLVED] #102 — `ValidationStatusAttribute` allowed `AttributeTargets.Method` that no code
  reads.** The sole reader (`DefaultValidationStatusMapper.GetStatus`, now also the two newly-wired
  call sites) resolves the attribute via `handlerType.GetCustomAttribute<ValidationStatusAttribute>()`
  - class-level only; a method-level attribute compiled but was silently dead. Dropped
  `AttributeTargets.Method` from `[AttributeUsage]` (pre-1.0, source-breaking only for code that was
  already silently broken). Grepped the repo for method-level usage first - none found (the one
  existing usage, `EnhancedFluentValidationTest.SampleHandler`, is already class-level); the full
  solution build after the change confirms nothing depended on the dropped target.

### Tracked findings round 10, WP-Y — Host/entry-point seams (done)
Ruled in [`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) §"WP-Y — host/entry-point
seams".
- **[RESOLVED] #104 — ASP.NET hosts never forwarded `HttpContext.RequestAborted` into the
  `SendAsync(event, cancellationToken)` overload**, unlike the Azure Functions and Google PubSub
  hosts (`AzureFunctionApp.cs`, `GooglePubSubFunctionHost.cs`), which do. Both call sites
  (`AspNetServerWorker.StartAsync`'s `app.Run` handler and `AspApplicationBuilder.Add`'s middleware)
  now call the token-taking overload with `context.RequestAborted`. Regression coverage added at both
  call sites directly (`AspNetCancellationForwardingTest`), isolated from the AspNetContext pipeline's
  own independent `RequestAborted` seeding middleware so each call site's forwarding is provable on
  its own. See WP-Y.
- **[RESOLVED] #106 — `InlineAwsLambdaStartUp.Build()` ran `Configure` before `ConfigureServices`,
  inverted vs. the production host (`AwsLambdaHost`'s constructor: `ConfigureServices` then
  `Configure`).** Since transport `Use*` extensions self-register defaults via `TryAdd*` (first
  registration wins), a user's `ConfigureServices` override of a framework default won in production
  but silently lost under the inline test host. Swapped the two calls to match `AwsLambdaHost`'s
  order. Added a red→green test registering a custom `IMessageHandlerResultSetter<SqsMessageContext>`
  via `TryAddScoped` in `ConfigureServices`, ahead of `UseSqs`'s own `TryAdd` default — failed before
  the fix (the default setter ran), passes after (`InlineAwsLambdaStartUpOrderingTest`). Also added an
  XML-doc remark that `Build()` deliberately runs `RunStartUpChecks()` but not `WarmUp()` (warm-up
  exists for Lambda's INIT phase, which an inline test host about to invoke immediately has no
  equivalent of). See WP-Y.
- **[RESOLVED] #107 — `AwsLambdaHost.FunctionHandlerAsync`'s `finally` block let a throw from the
  `OnInvocationCompleteAsync()` override point (documented for telemetry flush, which can plausibly
  throw — e.g. an exporter endpoint down) replace the invocation's real exception as the reported
  Lambda function error.** The call is now wrapped in its own try/catch: the override's exception is
  logged at `Error` via a logger resolved once at start-up, and the invocation's own outcome (success
  or exception) is always what propagates/is reported. Red→green test: a pipeline that always throws
  exception A, hosted under a subclass whose `OnInvocationCompleteAsync` override always throws
  exception B — before the fix B replaced A as the reported exception; after the fix A propagates and
  B is logged, not silently swallowed (`AwsLambdaHostInvocationCompleteTest`). Folded into the same
  commit: `AwsLambdaMiddlewareRouter.MapResponse` null-checked `context.Response` *after* already
  serializing into it — reordered to check first (dead/misleading rather than a live NRE today, since
  `AwsEventStreamContext` always initializes `Response` in its constructor; no test added per the
  ruling's "no test strictly required"). Also added an explicit remark on `IAwsHttpBridge` (XML doc)
  and `Benzene.Aws.Lambda.HttpBridge/CLAUDE.md` that the bridge implementer owns exception-to-response
  conversion — an exception from a hand-written bridge propagates as a raw Lambda function error (API
  Gateway 502), unlike Benzene's own `UseApiGateway` binding, which produces an in-band HTTP error
  response. See WP-Y.

---

### Tracked findings round 10 (2026-08-26) — all fixed

The round-10 review pass (task board #96: five parallel agents over the Lambda hosting bridges,
deeper CosmosDb/gRPC, health-check logic, Kafka/self-hosted workers, and the abstractions contract
packages, against `4657c9d`) produced **22 fix-worthy findings, tracked as task board #98–#119**,
all now resolved, ruled in
[`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) (now archived,
stamped with the landing commits; execution task #120 complete). Headlines: the version-blindness
root cause behind #69/#70 was the `IMessageGetter`/`ResolvedTopicCache` abstraction shape itself,
with a third live instance confirmed (`UseMeshTrace` exports `TopicVersion = null` for every
header-versioned message, #98 — WP-V; the fix also had to reach `BenzeneMessageGetter`, not just the
generic `MessageGetter<TContext>` facade the ruling named — see the archived doc's stamp for why);
three of the four self-hosted workers settled successfully-processed messages through calls gated on
the shutdown token, converting graceful shutdown into silent double-processing (SQS #115, EventHub
#116, ServiceBus #117 originally suspected — confirmed and fixed, see WP-AD above);
`CachingHealthCheckProcessor` had no single-flight guard (50 concurrent cold-cache callers → 50 full
dependency-hammering runs, #111 — WP-AC above); and `ValidationStatusAttribute` — documented in the
shared abstractions package — was honored by exactly one of the three validation adapters (#99 —
WP-W above). All ten work packages (WP-V, WP-W, WP-X, WP-Y, WP-Z, WP-AA, WP-AB, WP-AC, WP-AD, WP-AE)
landed and were pushed to `main`; every finding below carries its own `[RESOLVED]` line.

- **[RESOLVED] #108 — `BenzeneCosmosChangeFeedWorker`'s auto-checkpoint call sat inside the pipeline's
  own try/catch, so a checkpoint failure after a successful batch was misattributed to the handler
  ("Processing change feed batch ... failed"), and in skip mode (`CatchHandlerExceptions = true`) the
  catch block re-invoked the very same `checkpointAsync()` call a second time with zero backoff — if
  that retry also failed, the exception escaped the worker un-logged.** The auto-checkpoint call
  (`OnChangesAsync`) now runs after the handler's try/catch returns, in its own try/catch: on failure
  it is logged explicitly as a checkpoint failure naming the lease container as the failing
  dependency (not the handler, not the batch), is never re-invoked, and the batch is simply left
  un-checkpointed so the SDK redelivers it — the correct at-least-once outcome in both modes — without
  faulting the worker. See WP-AA.

New `[DECISION]` items surfaced by the same pass (recorded under "Open — maintainer decisions"
below): worker self-stop leaves the process Ready with health green; EventHub has no poison-message
escape hatch (Kafka's DLT argument applies verbatim); gRPC client per-call deadlines are not
settable; missing-topic status asymmetry (`ValidationError` vs `NotFound`) across
EventGrid/QueueStorage/Timer vs the sentinel transports.

### Tracked findings round 10, WP-X — contract-annotation alignment (done)
Ruled in [`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) §"WP-X —
contract-annotation alignment (#100, #101, #103)". Annotation-only, no behavioral change: aligning
three nullable-reference-type contracts with what their implementations/callers already did in
practice, landed after WP-V (which touches the same `MessageGetter.cs`).
- **[RESOLVED] #100 — `IBenzeneResult.PayloadAsObject`/`IBenzeneResult<T>.Payload` were declared
  non-nullable but were `null` for every failed/void result** (`ServiceBenzeneResultInternal<T>`
  emitted a CS8603 warning at its own `PayloadAsObject` getter; seasoned consumers already
  null-checked or used `?.`, e.g. `CrudConventionResponseEventMapping`,
  `SerializerResponseRenderer`). Annotated `object? PayloadAsObject` and `T? Payload` on both
  interfaces and documented the failure/void-path `null` behavior in their XML docs. Did a full
  solution build after the annotation change and worked through every newly-surfaced warning with
  honest null-handling (real null-checks, `??`, pattern matching / `Assert.IsType<T>` - never `!`):
  `Benzene.CloudService/MeshAnnouncer.cs` (`??` fallback for the health-report payload),
  `Benzene.Core.MessageHandlers/Response/DefaultResponsePayloadMapper.cs` (`SerializePayload`'s
  `payload` parameter widened to `object?` - the method already null-checked its body, only the
  signature was overclaiming), `Benzene.Cache.Core/CacheEntry.cs` + `CacheWriteActions.cs` +
  `ICacheWriteActions.cs` (a `null` payload/cache-value is now an explicit "nothing to write back"
  skip rather than an assumed-non-null write), `Benzene.MapReduce/ScatterGatherExtensions.cs`
  (`Outcome.Ok`'s `partial` parameter widened to match the field it was already stored in as),
  `Benzene.Results/BenzeneResultExtensions.cs` (`As`'s `map` delegate parameter widened to `T?` on
  both the sync and `Task`-returning overloads), `Benzene.Saga/SagaStep.cs` + `StepBuilder.cs` (the
  `Compensate` delegate's payload parameter widened to `T?` to match the forward result's now-honest
  `Payload` type), and five test-file dereferences (`GrpcBenzeneMessageClientTest`,
  `GrpcClientIntegrationTest`, `MeshAggregateMessageHandlerTest`, `MeshCollectorStoreTest`,
  `MeshDispatchTest`) fixed with `?.` or `Assert.IsType<T>(...)`, matching the pattern already used
  elsewhere in the test suite. The same ripple reached the (separately-built) example solution:
  `examples/Saga/Benzene.Example.Saga/SignupSaga.cs`'s four `Compensate` lambdas now
  `ArgumentNullException.ThrowIfNull` their payload before use (compensation only ever runs for an
  already-succeeded step, so this is a checked assumption, not a behavior change), and
  `InMemoryOrderDbClientTest`/`OrderServiceTest` got the same `?.` treatment. Full solution build
  after all fixes: 0 errors, and the CS8xxx warning set is a strict subset of the pre-change
  baseline (three pre-existing warnings - the ones this exact annotation fixes - disappeared; zero
  new ones appeared). `Benzene.Core.Test` (3056/2/0), `Benzene.Mesh.Test` (536), and
  `Benzene.Mesh.Host.Test` (141) all still pass, and `Benzene.Examples.sln` still builds with 0
  errors and no new warnings after the example-file fixes.
- **[RESOLVED] #101 — `MessageGetter<TContext>`'s `GetBody`/`GetTopic` facade methods narrowed the
  nullability of the interfaces they forward to** (`string`/`ITopic` vs. the underlying
  `IMessageBodyGetter<TContext>`/`IMessageTopicGetter<TContext>`'s own `string?`/`ITopic?`, with two
  `!` null-forgiving suppressions in `GetTopic` papering over the mismatch). Re-read WP-V's merged
  `GetTopic` (the version-join + `ResolvedTopicCache` logic, task #98) before touching this file, per
  the ruling's coordination note. Aligned both signatures to `string?`/`ITopic?` and removed both `!`
  suppressions - they're no longer needed once the return type is honest; no caller actually required
  a non-null guarantee at either call site (every consumer already reads `IMessageGetter<TContext>`
  through the interface, which was already nullable, so this was a false non-null promise the
  concrete class alone made and nothing in the solution relied on). Updated the class's XML docs to
  state the `null` contract explicitly. Full solution build shows zero new warnings from this change
  (one pre-existing warning at the old `GetBody` implementation disappeared, since the honest
  signature no longer needs a null check the old one lacked).
- **[RESOLVED] #103 — `IVersionSelector.Select(string requestedVersion, ...)` declared a
  non-nullable `requestedVersion`, but its only caller (`MessageHandlerDefinitionLookUp.FindHandler`)
  passes `topic.Version`, which is null/empty for every unversioned message per
  `IMessageVersionGetter`'s documented "null/empty means the topic's default version" contract.**
  Declared the parameter `string? requestedVersion` on the interface and the default
  `VersionSelector` implementation, and documented that the "return value must be one of
  `availableVersions`" contract presumes a non-empty array - which can't hold for zero available
  versions, but is unreachable via the default lookup path today (`MessageHandlerDefinitionLookUp`
  early-returns on zero registered handlers before calling `Select`, and fast-paths past `Select`
  entirely when exactly one is registered) - documented only, no defensive code added for the
  unreachable case, per the ruling. No behavioral change; no new warnings.

### Tracked findings round 10, WP-AB — gRPC client cancellation + health bridge diagnosability (done)
Ruled in [`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) §"WP-AB — gRPC
client cancellation + health bridge diagnosability".
- **[RESOLVED] #109 — `GrpcBenzeneMessageClient.SendMessageAsync` logged routine ambient
  cancellation at `LogLevel.Error` and mapped it to `ServiceUnavailable` via its catch-all** (a bare
  `TaskCanceledException` from `ICancellationTokenAccessor`'s ambient token firing mid-send hit the
  same catch block as a genuine unexpected exception). Now caught separately, mirroring the server
  side's own `OperationCanceledException` handling in `GrpcMethodHandler.RunPipelineAsync` (no log):
  `SendMessageAsync` catches `OperationCanceledException` before the general catch-all and returns a
  `ServiceUnavailable` failure with no `LogError` call - the same classification a mid-flight
  `RpcException(Cancelled)` already resolves to via `DefaultGrpcStatusReverseMapper`, so both
  cancellation surfaces agree and neither generates Error-level noise. The `Cancelled ->
  ServiceUnavailable` reverse mapping itself is deliberately left unchanged (a wire-visible
  vocabulary question, deferred to the spec level - see the comment added at
  `DefaultGrpcStatusReverseMapper.cs:33`). Doc-only: `src/Benzene.Grpc.Client/CLAUDE.md` now states
  that resilience/retry for an unreachable channel is the app-owned `GrpcChannel`'s own
  `ServiceConfig` retry policy - Benzene adds none. Regression tests in
  `GrpcBenzeneMessageClientTest` assert no `Error`/`Critical` log entry and the
  `ServiceUnavailable` classification for the ambient-cancellation path. See WP-AB.
- **[RESOLVED] #110 — `BenzeneHealthCheckBridge`: a typo'd `LivenessCheckTypes`/`ReadinessCheckTypes`
  entry matching no registered check yielded an unconditional `Healthy("No Benzene health checks are
  registered.")` at every probe, and `data[result.Type] = result.Status` silently collapsed two
  checks sharing the same `Type`.** The `includeTypes` constructor now validates every configured
  type against the registered checks' actual `Type` values and throws `InvalidOperationException`
  immediately (at construction / wiring time, not deferred into `CheckHealthAsync`'s probe-time
  "zero checks matched" branch) naming the unmatched type(s) - the same "never silently
  under-enforced" fail-fast principle `Benzene.Mesh.Host.MeshAuthGate.Validate` applies to its own
  config satisfiability. The result `data` dictionary is now built through a private
  `DuplicateTypeSuffixer` that reuses `Benzene.HealthChecks.HealthCheckNamer`'s suffixing convention
  (first occurrence unchanged, collisions suffixed `-2`, `-3`, ...) as an independent copy rather
  than a new project reference, per this package's documented deliberate non-dependency on the full
  `Benzene.HealthChecks` pipeline package. Tests in `BenzeneHealthCheckBridgeTest` cover: an
  unmatched `includeTypes` entry throws at construction; a fully-satisfiable `includeTypes` and the
  no-`includeTypes`/no-registered-checks default both still construct cleanly; two checks sharing a
  `Type` both appear distinctly (suffixed) in `CheckHealthAsync`'s result data. See WP-AB.

### Tracked findings round 10, WP-AE — Kafka rebalance + config hygiene (done)
Ruled in [`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) §"WP-AE — Kafka
rebalance + config hygiene".
- **[RESOLVED] #118 — `BenzeneKafkaWorker`'s `ConfigureRebalanceDrain` registered no
  `SetPartitionsLostHandler`, so Confluent.Kafka fell back to the *revoked* handler for a genuine
  partition-LOSS event (session timeout, a long GC pause), paying its up-to-`DrainTimeout` (30s)
  drain wait and then attempting a `Commit()` the broker's own generation fencing would reject (the
  partition is likely already reassigned to another consumer by the time the callback fires) - a
  rejection the code mislabeled as a benign "no offsets to commit" at Debug.** A `SetPartitionsLostHandler`
  is now registered alongside the revoked handler; it never commits and does not drain at all (per
  Confluent's own guidance that draining buys nothing once the partition is likely already
  reassigned - "as fast as possible back to rejoining"), and logs a clearly distinct "Partitions LOST
  (not revoked)" event at `Information`, never conflated with or buried under the revoked-handler's
  logging. The revoked-handler logic itself is unchanged (still drains + commits under manual offset
  management), just extracted into `BenzeneKafkaWorker.OnPartitionsRevoked` alongside the new
  `OnPartitionsLost` - both `internal` + `InternalsVisibleTo("Benzene.Test")` so tests can invoke the
  callback logic directly (`ConsumerBuilder<TKey,TValue>`'s registered `PartitionsRevokedHandler`/
  `PartitionsLostHandler` live on non-public properties, so there is no way to read the delegates back
  off a real builder without a live broker connection - confirmed by hitting `CS0122` when the first
  test draft tried exactly that). Tests: `KafkaWorkerDeadLetterAndDrainTest.PartitionsLostHandler_NeverCommits_AndReturnsImmediately`
  (asserts zero `Commit()` calls and sub-second return - the "does it drain" question resolved by
  never wiring the lost handler to a dispatcher at all, not merely bounding a wait);
  `PartitionsRevokedHandler_StillDrainsAndCommits_WhenManagingOffsetsManually` (regression guard - the
  extraction didn't change the revoked path's behavior); `DrainOnRevokeOn_RegistersBothRevokedAndLostHandlersWithoutThrowing`
  (the wiring half - applying the worker's configure-builder callback to a real, never-`Build()`-ed
  `ConsumerBuilder` registers both handlers without `ConsumerBuilder`'s own double-registration guard
  tripping). **Residual gap:** none of this is exercised against a live broker/real rebalance (the
  ruling itself anticipated this - "inherently rebalance-integration-shaped ... may be hard to
  unit-test directly against a real broker"); the direct-invocation tests cover the callback bodies'
  logic exactly, not librdkafka's actual decision of when it calls lost vs. revoked.
- **[RESOLVED] #119 — `StartAsync` mutated the CALLER's shared `ConsumerConfig` instance directly**
  (`_benzeneKafkaConfig.ConsumerConfig.EnableAutoOffsetStore = false` for `CommitOnlyOnSuccess`/dead-lettering)
  **- surprising/dangerous if the same `ConsumerConfig` is reused elsewhere (a health check, a second
  worker instance).** `StartAsync` now clones (`new ConsumerConfig(new Dictionary<string,string>(ConsumerConfig))
  { EnableAutoOffsetStore = false }` - `ConsumerConfig`'s `ClientConfig`-typed copy constructor was
  tried first and rejected: it shares the underlying dictionary rather than copying it, confirmed by a
  throwaway repro where mutating the "clone" mutated the original too) when manual offset management
  needs the adjustment, and hands the clone to `IKafkaConsumerFactory.Create` - the caller's own
  object is never touched. Test: `KafkaWorkerDeadLetterAndDrainTest.StartAsync_ManagingOffsetsManually_DoesNotMutateCallersConsumerConfig`
  (asserts the original object's `EnableAutoOffsetStore` is unchanged and the object the factory
  receives is a *different* instance with it correctly disabled); the pre-existing
  `BenzeneKafkaWorkerTest.StartAsync_CommitOnlyOnSuccessWithValidCombination_*` test previously
  asserted the mutate-in-place behavior as correct - flipped (and renamed) to assert non-mutation,
  the actual red→green signal for this fix.

### Tracked findings round 10, WP-AD — self-hosted worker settlement-on-shutdown (done)
Decisions, rationale, and the #117 verification evidence are ruled/recorded in
[`bug-fix-designs-round10-2026-08.md`](archive/bug-fix-designs-round10-2026-08.md) §"WP-AD — self-hosted
worker settlement-on-shutdown (#115, #116, #117)". One unifying theme across three transports: each
settled *successfully processed* work through a call gated on the shutdown/processor token itself, so
graceful shutdown could convert already-completed work into silent redelivery/double-processing
(Kafka already did this correctly via synchronous `StoreOffset` + a commit in the run task's
`finally`, and was not in scope). All three are now fixed on the same principle: settlement of
already-completed work runs under `CancellationToken.None`, never the run/stop token.
- **[RESOLVED] #115 — SQS: a successfully-processed message was silently never deleted when
  shutdown fired mid-batch** (`SqsConsumer.cs`: the delete call passed the poll loop's own
  `cancellationToken`; a catch for `OperationCanceledException` swallowed the resulting failure with
  no log line). Confirmed by an executed probe. Fixed: once a batch's pipeline run has completed, the
  delete of successfully-processed messages runs under `CancellationToken.None` (bounded by the AWS
  SDK client's own HTTP timeout, not by an artificial one); if the delete call itself still fails (as
  opposed to a partial per-entry failure, already logged), it's now caught and logged naming the
  message ids that will be redelivered. Regression test:
  `SqsConsumerCancellationTest.StartAsync_ShutdownFiresAfterHandlerSucceeds_MessageIsStillDeleted`
  (red pre-fix: asserted the token used for the delete call is not the cancelled run token).
- **[RESOLVED] #116 — EventHub: `UpdateCheckpointAsync` sat outside the handler's try/catch and used
  the (shutdown-cancellable) `args.CancellationToken`** (`BenzeneEventHubWorker.cs`). Confirmed by an
  executed probe: a successful handler with `args.CancellationToken` already cancelled (the SDK's
  documented state once `StopProcessingAsync` is called, which `StopAsync` calls while in-flight
  handlers are still awaited) threw an `OperationCanceledException` that propagated UNHANDLED out of
  `OnProcessEventAsync` — which per the SDK's own docs faults the partition-processing task and can
  crash the process on some hosts; separately, any transient checkpoint-store failure escaped the
  same way, bypassing the worker's own `CatchHandlerExceptions` policy entirely. Fixed: the checkpoint
  call now runs in its own try/catch, under `CancellationToken.None`. A cancellation path (defensive —
  the checkpoint store itself throwing `OperationCanceledException` for its own reasons) is logged at
  Information ("skipped due to shutdown ... redelivered on restart") rather than treated as a failure;
  any other checkpoint failure is logged at Error and routed through the same
  `CatchHandlerExceptions` stop-or-continue policy every other failure in the file already uses (the
  stop logic was extracted into a shared `StopProcessorOnce()` helper so both paths use it
  identically). Regression tests in `EventHubWorkerCheckpointCancellationTest` (all three paths: token
  already cancelled → still checkpoints; checkpoint store throws OCE → Information log, no stop;
  checkpoint store throws an ordinary exception → Error log, routed through
  `CatchHandlerExceptions`).
- **[RESOLVED] #117 — ServiceBus: settlement used `args.CancellationToken` — same shutdown race,
  plus an abandon failure in the catch path could replace the original handler exception**
  (`BenzeneServiceBusWorker.cs`: the settler passed `_args.CancellationToken` into
  `CompleteMessageAsync`/`AbandonMessageAsync`; the failure-path catch called `AbandonMessageAsync()`
  with a bare `throw;` after it, so an abandon failure propagated in place of the original exception).
  This was **SUSPECTED, not confirmed** going into the work package — the prior review had only
  documentary SDK evidence (`ProcessMessageEventArgs.CancellationToken`'s docs use the identical
  wording as the EventHub case: "will be cancelled when `StopProcessingAsync` is called") and could
  not execute a repro because the settler seam is `internal`. **Verified this round, no
  `InternalsVisibleTo`/refactor needed**: `ProcessMessageEventArgs` has a public constructor
  `(ServiceBusReceivedMessage, ServiceBusReceiver, CancellationToken)` and `ServiceBusReceiver` has a
  protected parameterless constructor with virtual settle methods, both mockable directly; combined
  with the same reflection-invoke-the-private-handler pattern already used for
  `BenzeneEventHubWorker.OnProcessEventAsync` in this codebase, this let the race be reproduced with
  the SDK's own real types rather than a mock of Benzene's own seam. **The race is real**: with the
  pre-fix code, a real `OperationCanceledException` was observed propagating unhandled out of
  `BenzeneServiceBusWorker.HandleMessageAsync` for a message whose handler had already succeeded (and
  separately, for one whose handler had already failed and was being abandoned) — confirmed by
  reverting the fix and re-running the new regression tests, which failed with exactly that exception
  before being restored to green. Fixed: every settle call (`Complete`/`Abandon`/`DeadLetter`/`Defer`,
  both the regular and session settlers) now uses `CancellationToken.None`, not
  `MessageLockCancellationToken` — chosen over the lock-scoped token because it fires on lock
  loss/renewal-duration-elapsed, not shutdown, and using it would risk turning a genuine lock-loss
  into a client-side `OperationCanceledException` instead of the SDK's own (more diagnosable) failure
  for that case; `CancellationToken.None` bounds the call only by the SDK's own operation timeout, per
  the WP's principle. Also fixed: the abandon-on-failure call in the catch path is now wrapped in its
  own try/catch, so an abandon failure is logged but never replaces the original handler exception in
  the rethrow. Regression tests in `BenzeneServiceBusWorkerSettlementCancellationTest` (success +
  cancelled token → still completes; failure + cancelled token → still abandons; handler throws +
  abandon also throws → original exception still propagates, both logged).

> **Tracked findings, 2026-08-26 (round 11, §2 Event Sourcing) — all 12 fixed.** The round-11 review
> pass's event-sourcing section (`Benzene.EventSourcing` + `Benzene.EventSourcing.DynamoDb`) produced
> 12 evidence-backed findings (tasks #121–#132). All landed in one pass. Full evidence and fix
> rationale remain in
> **[`bug-fix-designs-round11-2026-08.md`](archive/bug-fix-designs-round11-2026-08.md)** §2 (now
> archived, stamped with the round's 8 landing commits — see the top-of-file summary blockquote
> above); consult it before touching this code again so a decision made here doesn't get silently
> re-litigated.

- **[RESOLVED] #121 — `DynamoDbEventStore.AppendAsync` never verified the stream was actually AT
  `expectedVersion`, only that the target Put slots were free.** An `expectedVersion` ahead of the
  real head found its target slots free (nothing had written them yet), so the transaction succeeded
  and permanently gapped the stream for any correct writer that folds it from the start; a negative
  `expectedVersion` wrote durable events `ReadAsync` could never return. Fixed: when
  `expectedVersion > 0`, the transaction now includes a `ConditionCheck` transact item on
  `(streamId, expectedVersion)` with `attribute_exists(#pk)`, so the head must genuinely be at that
  version, not merely have free slots above it; `expectedVersion < 0` now throws
  `ArgumentOutOfRangeException` before any request is built. Tests:
  `Append_WithExpectedVersionGreaterThanZero_IncludesAConditionCheckOnTheExpectedVersion`,
  `Append_WithExpectedVersionZero_OmitsTheConditionCheck`,
  `Append_WithANegativeExpectedVersion_Throws`.
- **[RESOLVED] #122 — a blanket `catch (TransactionCanceledException)` mislabeled throttling,
  capacity, and validation failures as concurrency conflicts**, with a message that compared the
  stream's head to itself. Fixed: each `CancellationReason.Code` in the exception's
  `CancellationReasons` is now inspected; only `ConditionalCheckFailed`/`TransactionConflict`
  translate to `EventStoreConcurrencyException`, everything else rethrows the original exception
  untouched. Tests: `Append_WhenTransactionCancelledByAConditionalCheckFailure_...`,
  `Append_WhenTransactionCancelledForAThrottlingReason_RethrowsTheOriginalException`.
- **[RESOLVED] #123 — `EventStoreConcurrencyException` had no inner-exception constructor**, so the
  real AWS failure behind a translated conflict was always discarded (this blocked fixing #122
  properly, since there was nowhere to hang the original `TransactionCanceledException`). Added an
  overload taking `Exception? innerException`, passed through to the base `Exception` constructor;
  the existing 3-arg constructor now delegates to it with `null`. Covered by the #122 tests above,
  which assert `ex.InnerException` is the original exception.
- **[RESOLVED] #124 — the post-conflict "actual version" diagnostic read (`CurrentVersionAsync`) ran
  on the caller's own `CancellationToken` with no guard**, so a throttled read-back or a raced
  cancellation could replace a genuine conflict exception with an unrelated one, silently losing the
  conflict. Fixed: the read-back now runs under its own `CancellationToken.None` (a caller racing
  another writer and then cancelling should still see the conflict, not an OCE) and is wrapped in its
  own try/catch, falling back to `ActualVersion = -1` ("unknown", already documented on the property)
  on any failure rather than letting that failure propagate in place of the conflict. Tests:
  `Append_WhenConflictDiagnosticReadFails_FallsBackToUnknownActualVersion`,
  `Append_WhenConflictDiagnosticRead_IgnoresTheCallersCancellationToken`.
- **[RESOLVED] #125 — `InMemoryEventStore.AppendAsync` was not atomic across a batch**; a mid-batch
  throw (e.g. a null element) left a partial append visible to readers, diverging from
  `DynamoDbEventStore`'s genuine all-or-nothing `TransactWriteItems` — the store every test suite runs
  against by default behaved differently from the one that ships to a fleet. Fixed: the new
  `StoredEvent`s are now built into a local list first; only after the whole batch builds without
  error are they spliced into the stream (and, per #132 below, only then is a brand-new stream
  registered in the dictionary at all). Test:
  `Append_WhenABatchElementThrowsMidBatch_LeavesTheStreamUnaffected` (a null element mid-batch;
  asserts the stream is still exactly at its pre-append version and a correct retry succeeds).
- **[RESOLVED] #126 — `DynamoDbEventStore`'s constructor had no fail-fast validation of its table/key
  configuration** (null client, null/empty table name, empty partition/sort key attribute name,
  `pk == sk`, or a key attribute colliding with one of the reserved event attribute names
  `eventType`/`payload`/`timestamp` — any of which would silently corrupt every write). Added
  constructor guards for all of the above (`ArgumentNullException` for the client,
  `ArgumentException` for the rest). Tests: `Constructor_WithANullClient_Throws`,
  `Constructor_WithAnInvalidTableName_Throws`,
  `Constructor_WithTheSamePartitionAndSortKeyAttribute_Throws`,
  `Constructor_WithAPartitionKeyCollidingWithAReservedAttribute_Throws`,
  `Constructor_WithASortKeyCollidingWithAReservedAttribute_Throws`.
- **[RESOLVED] #127 — `ToStoredEvent` silently defaulted an unrecognized or missing `eventType`/
  `payload` attribute to `string.Empty`** rather than surfacing the corruption (a non-`S`-type
  attribute leaves `AttributeValue.S` null, which the old code handed straight to the non-nullable
  `StoredEvent.EventType`/`Payload` properties). Fixed: a shared `RequireStringAttribute` helper now
  throws `InvalidOperationException` naming the stream id and version when the attribute is missing
  or not a string (`S`) type. Tests: `Read_WhenAnEventItemIsMissingARequiredAttribute_Throws` (theory
  over `eventType`/`payload`), `Read_WhenEventTypeIsNotAStringAttribute_Throws`.
- **[RESOLVED] #128 — an empty-batch append skipped the concurrency check in `DynamoDbEventStore`
  only**, diverging from `InMemoryEventStore` (which always checks, even for an empty batch, since its
  check runs before the loop over events). Picked `InMemoryEventStore`'s semantic — both stores must
  still validate an empty append against the stream's real current version. Since an empty batch has
  no Put items to hang a transact-item condition off, `DynamoDbEventStore` now runs its existing
  `CurrentVersionAsync` (`ConsistentRead`) helper directly for the empty-batch case and throws
  `EventStoreConcurrencyException` on a mismatch. Tests:
  `Append_EmptyBatch_StillChecksConcurrency_AndThrowsOnMismatch`,
  `Append_EmptyBatch_ReturnsExpectedVersionWhenItMatchesTheHead`.
- **[RESOLVED] #129 — no `ClientRequestToken` on the transact-write**, so an ambiguous retry (client
  timeout, network blip) after DynamoDB had actually applied the write would look like a fresh,
  potentially conflicting attempt rather than a safe retry of the same one. Fixed: a deterministic
  token is now derived from `(streamId, expectedVersion, each event's EventType + Payload)` via
  SHA-256, folded into a `Guid` (DynamoDB's 1–36 character limit). Test:
  `Append_SameStreamExpectedVersionAndEvents_ProducesTheSameClientRequestToken` (same inputs → same
  token; a different payload → a different token).
- **[RESOLVED] #130 — `InMemoryEventStore` never observed its `CancellationToken` parameter.** Added
  `cancellationToken.ThrowIfCancellationRequested()` at the top of both `AppendAsync` and `ReadAsync`.
  Tests: `AppendAsync_WithAnAlreadyCancelledToken_ThrowsWithoutMutatingState`,
  `ReadAsync_WithAnAlreadyCancelledToken_Throws`.
- **[RESOLVED] #131 — `MaxEventsPerAppend` (the 100-event transaction-size ceiling) was enforced only
  in `DynamoDbEventStore`**, so app code developed against `InMemoryEventStore` (the default in most
  test suites) could pass a batch that only fails once pointed at the real store. Mirrored the same
  constant and check in `InMemoryEventStore`. Test: `Append_MoreThanMaxEventsPerAppend_Throws`.
- **[RESOLVED] #132 — `InMemoryEventStore` created the empty `List<StoredEvent>` stream entry in its
  dictionary *before* the concurrency check**, so every rejected append against an unknown stream id
  leaked a permanent empty entry. Fixed (alongside #125): the dictionary insert now happens only after
  the version check has passed. Test:
  `Append_RejectedAgainstAnUnknownStream_DoesNotLeakAnEmptyStreamEntry` (reflects into the private
  `_streams` field to assert it stays empty after a rejected append).

### Tracked findings round 11, WP — Mesh discovery + catalog pipeline (done)
Decisions, rationale, and the residual scope note are ruled in
[`bug-fix-designs-round11-2026-08.md`](archive/bug-fix-designs-round11-2026-08.md) §"§4 Mesh discovery +
catalog pipeline".
- **[RESOLVED] #148 — `MeshDiscoveryRunner`'s `foreach` over providers had no try/catch; one provider
  throwing lost every other provider's results, and the discovery host wrote no registry document at
  all on any failure.** Each provider call is now individually try/caught (with its own timeout, see
  #157) and a failed provider contributes nothing but the loop continues; failures are surfaced via a
  new optional `ICollection<MeshDiscoveryProviderFailure>? failures` out-parameter on `DiscoverAsync`
  (provider key + exception type, never the message). `Benzene.Mesh.Discovery.Host`'s `Program.cs` now
  refuses to publish (non-zero exit, previous registry left untouched) only when *every* configured
  provider failed — decided in new `DiscoveryPublicationDecision.ShouldPublish` (unit-tested directly)
  — while publishing the partial registry, with the failures logged to stderr, when only *some* did. A
  run with zero providers configured is unaffected (README's documented "nothing wired" case, distinct
  from "everything failed").
- **[RESOLVED] #149 — `MeshSnapshotBuilder.TryGetPreviousSpecHashAsync`'s `store.TryReadAsync` call had
  no try/catch (only the JSON deserialize below it was guarded), so one throttled/failed read aborted
  the `Task.WhenAll` driving the WHOLE aggregation run.** Now guarded the same way
  `MeshAggregator.ApplyCatalogDiffAsync`'s equivalent read already was: an unreadable previous snapshot
  is treated as "no baseline", never a failed run. `ArtifactStoreMeshReportPublisher.PublishAsync`
  shares the same fix for free, since it calls the same builder method.
- **[RESOLVED] #150 — `AwsLambdaDiscoveryProvider`'s `Task.WhenAll` over per-function `ListTags` calls
  meant one function failure (deleted/access-denied) lost every other function's result.** Each
  `ListTagsAsync` call is now individually try/caught inside the `Select` lambda; a function whose tags
  can't be read is dropped (it can't be tag-matched without them) and every other function still
  contributes.
- **[RESOLVED] #151 — `FileSystemMeshArtifactStore.PublishAsync` wrote via
  `File.WriteAllTextAsync` (truncate-then-write in place), exposing a concurrent `TryReadAsync` in the
  same process to a torn read.** Now writes to a temp file in the same directory, then
  `File.Move(tmp, path, overwrite: true)` - atomic on both POSIX and Windows.
- **[RESOLVED] #152 — `MeshAggregator.InlineSchema`'s pre-comparison `$ref` inlining defeated
  `JsonSchemaComparer`'s `$ref`-name-based variant matching (inlining replaces the `$ref` node
  entirely), so a pure `oneOf`/`anyOf`/`allOf` branch reordering was published as a fabricated
  "breaking" change.** `JsonSchemaComparer.RefId` now falls back to the member's `title` when there is
  no `$ref` to read - exactly the field `InlineSchema` already stamps with the original ref name for
  this purpose. Verified red→green: `MeshAggregatorCompatibilityTest.OneOfBranchReordering_IsNotBreaking_DespitePreComparisonRefInlining`
  failed (reported `breaking`) before the fix and passes (`compatible`, zero changes) after.
- **[RESOLVED] #153 — `MeshAggregateMessageHandler` bypassed `MeshAggregationPass`'s single-writer
  gate entirely by calling `_aggregator.RunOnceAsync` directly, as did `MeshPollBackgroundService`.**
  The gate now lives inside `MeshAggregator.RunOnceAsync` itself (a `SemaphoreSlim(1,1)` wrapping the
  whole run), so every call site is covered, including both of those and any future one, without
  relying on each host to remember to add its own. `MeshAggregationPass`'s own gate is now a redundant
  (harmless) outer layer for its own call site; left in place since removing it would be a bigger,
  unrequested behavioural change and it costs nothing extra.
- **[RESOLVED] #154 — permission failures (403/`AccessDenied`) were indistinguishable from transient
  failures (network/timeout) in `MeshAggregator`'s recorded error (`ex.GetType().Name` only).** Added
  `MeshServiceSnapshot.ErrorClass` (`MeshErrorClass`: permission/unreachable/timeout/other), populated
  by a new `MeshAggregator.ClassifyError` that reads each SDK's status-code shape via reflection
  (`HttpRequestException.StatusCode`, `AmazonServiceException.StatusCode`,
  `Google.GoogleApiException.HttpStatusCode`, `Azure.RequestFailedException.Status`, the Kubernetes
  client's `HttpOperationException.Response.StatusCode`) rather than taking a compile-time dependency
  on every cloud SDK from the SDK-agnostic aggregator package. Additive/optional field, backward
  compatible with snapshots written before it existed.
- **[RESOLVED] #155 — `KubernetesApiServiceLister` ignored pagination (`limit`/`continueParameter`
  never set, `ContinueProperty` never read), silently dropping Services beyond the first page if the
  API server ever returned a continuation token.** Now sets an explicit `limit` (500) and loops on
  `ContinueProperty` until the server reports none left. This adapter had zero prior test coverage
  (every existing discovery test exercised the `IKubernetesServiceLister` port, not this SDK-backed
  implementation) - new `KubernetesApiServiceListerTest` covers both the all-namespaces and
  single-namespace paths, multi-page continuation, and the explicit `limit`.
- **[RESOLVED] #156 — a failed artifact write could leave a split catalog (old manifest beside new
  per-service artifacts) with no run stamp to detect the mismatch.** Every artifact of a run is now
  stamped with one shared timestamp captured once at the top of `RunOnceAsync` (reusing the manifest's
  own `GeneratedAtUtc`/each snapshot's `FetchedAtUtc` field rather than adding a new one, so it's a
  free run id), and `manifest.json` is now published LAST, strictly after `Task.WhenAll` of every other
  artifact write - so a reader can no longer see a manifest referencing an artifact that hasn't landed.
- **[RESOLVED] #157 — `MeshDiscoveryRunner` had no per-provider timeout, unlike
  `MeshAggregator.PerServiceFetchTimeout`.** Added an equivalent `PerProviderTimeout` (10s, same
  convention), wrapping each provider call with a token linked to the caller's own, so a genuine
  caller-driven cancellation still propagates instead of being recorded as a provider failure.

### Tracked findings round 11, §5 — less-common AWS/GCP transports (#158–#165, done)
Ruling, rationale, and scope decisions are in
[`bug-fix-designs-round11-2026-08.md`](archive/bug-fix-designs-round11-2026-08.md) §5 "Less-common AWS/GCP
transports".
- **[RESOLVED] #158 — S3 object keys were never URL-decoded** (`S3MessageBodyGetter.cs`,
  `S3MessageHeadersGetter.cs`): S3 URL-encodes an object key on the event notification record (space
  → `+`, reserved/non-ASCII bytes percent-encoded), so any key containing one of those reached the
  handler still encoded, and calling `GetObjectAsync` with it returned `NoSuchKey`. Fixed with a
  shared `S3ObjectKeyCodec.Decode` helper using `WebUtility.UrlDecode` (not `Uri.UnescapeDataString`,
  which doesn't decode `+` to a space). The body's `key` field and the `key` header now carry the
  decoded key; a new `keyRaw` header preserves the original wire encoding for callers who need it
  (e.g. building a pre-signed URL). Regression tests in `S3MessageMapperTests`.
- **[RESOLVED] #159 — Pub/Sub: a CloudEvent with no `Message` NREs in all three getters, and the NRE
  escaped `CatchExceptions = true`** because the catch block itself dereferenced
  `context.Message.MessageId` while logging, replacing the real exception with an unrelated NRE
  (`PubSubMiddlewareApplication.cs`). Fixed: `context.Message?.MessageId` in both the log call and the
  escalated `PubSubMessageProcessingException` construction (its `messageId` parameter is now
  nullable), and null-conditional chains through `Message`/`Attributes` in
  `PubSubMessageBodyGetter`/`PubSubMessageTopicGetter`/`PubSubMessageHeadersGetter` so each degrades
  to null/empty instead of throwing, matching the SNS/SQS siblings' hardening. Regression tests in
  `PubSubGettersTest` (each getter, no-message case) and `PubSubFailureHandlingTest` (the log-call
  case, using `FakeLogger` to assert the *real* exception instance reaches the logger, and the
  escalation case, asserting a null `MessageId` rather than a thrown NRE).
- **[RESOLVED] #160 — S3, DynamoDB, EventBridge, and Google Pub/Sub's `DependencyInjectionExtensions`
  used plain `AddScoped`/`AddHeaderMessageVersionGetter` instead of `TryAdd*` for their per-context
  topic/body/header/version getters**, silently shadowing a user's earlier registration
  (`ConfigureServices` runs before `Configure`, MS DI is last-wins) — the same defect class the
  `customization-robustness-review-2026-08.md` pass fixed on nine other packages but missed these
  four. Fixed mechanically in all four packages' `DependencyInjectionExtensions.cs`, matching
  `Benzene.Aws.Lambda.Sns`'s already-correct pattern. Regression test
  `AwsGoogleTransportGetterOverrideTest`: registers a custom `IMessageHeadersGetter<TContext>` before
  calling each of `AddS3`/`AddDynamoDb`/`AddEventBridge`/`AddGooglePubSub`, confirms the custom one
  wins.
- **[RESOLVED] #161 — Pub/Sub's outbound converter had no attribute-limit guard**
  (`OutboundPubSubContextConverter.cs`), unlike SNS's `GuardAttributeLimit`. Fixed: a `GuardAttribute`/
  `GuardAttributeCount` pair enforcing Pub/Sub's documented limits (100 attributes max per publish,
  256-byte keys, 1024-byte values, no `goog`-prefixed keys), throwing `InvalidOperationException` with
  a clear message — same choice (throw, not drop-with-log) SNS made. Regression tests in
  `OutboundPubSubContextConverterTest`.
- **[RESOLVED] #162 — Kinesis's resume-point computation could NRE outside `CatchExceptions`'s
  protection** (`KinesisStreamCheckpointer.FirstUncheckpointedSequenceNumber`, `.Kinesis.SequenceNumber`):
  the getter runs from `KinesisStreamApplication`'s `resultMapper`, which the base
  `MiddlewareApplication` invokes *after* `CatchAndCheckpointPipeline`'s own try/catch has already
  returned, so a malformed record with no `Kinesis` payload at the resume index threw an NRE that
  cascaded unhandled instead of being caught. Fixed with `?.Kinesis?.SequenceNumber`, degrading to a
  `null` resume point (empty `BatchItemFailures`) instead of crashing the invocation. **[DECISION]
  scoped down**: a malformed record at exactly the resume boundary can't be named as a specific
  sequence number to redrive, so this trades "crash, AWS retries the whole batch" for "report success,
  the malformed record is not retried" — the alternative (restructuring `MiddlewareApplication` so
  `resultMapper` runs inside the same try/catch as the pipeline) would let a real fix name *something*
  but is a larger, cross-cutting change to a shared base class touched by every stream transport;
  deferred. Regression test
  `KinesisStreamApplicationTest.HandleAsync_ResumePointRecordHasNoKinesisData_DoesNotThrow`.
- **[RESOLVED] #163 — EventBridge's body getter mishandled explicit JSON `null` detail and
  double-encoded string detail** (`EventBridgeMessageBodyGetter.cs`): an explicit JSON `null` detail
  fell through to `GetRawText()`, handing a handler the literal 4-character string `"null"` instead of
  no body; a string-typed detail (a malformed/synthetic delivery - real EventBridge always delivers an
  object) was handed through as an escaped JSON string literal a handler could never deserialize.
  Fixed: `JsonValueKind.Null` now returns `null`; a string-typed detail is unwrapped by parsing its
  content and using it if it's a JSON object, or throwing `InvalidOperationException` naming the
  actual `ValueKind` (or "not itself valid JSON") when it isn't. Regression tests in
  `EventBridgeGettersTest`.
- **[RESOLVED] #164 — GoogleCloud Functions HTTP inherits `Benzene.AspNet.Core`'s
  `AspNetHttpRequestAdapter`, which had the same header-casing/null-`Method`/`Path` defect class
  already fixed for API Gateway in #105** (`ApiGatewayHttpRequestAdapter.cs` was the reference fix).
  This adapter also serves Cloud Run and `Benzene.Azure.Function.AspNet`'s ASP.NET Core integration,
  so the fix lands once for all three. Fixed the same shape as #105: headers built with
  `StringComparer.OrdinalIgnoreCase` + first-wins `TryAdd` instead of a plain-ordinal
  `IEnumerable.ToDictionary`, and `?? string.Empty` on `Method`/`Path.Value` (ASP.NET Core's
  `PathString` implicit `string` conversion can be `null` for a default/unset `PathString`).
  Regression tests in `AspNetHttpRequestAdapterTest`. While auditing the same defect class, found and
  fixed the identical bug in a *different* file, `Benzene.Azure.Function.AspNet`'s own
  `AspNetMessageHeadersGetter` (its `ToDictionary` had no comparer either) — see #165 below.
- **[RESOLVED] #165 — SNS and Pub/Sub headers getters used an inconsistent comparer depending on
  whether attributes were present** (`SnsMessageHeadersGetter.cs`, `PubSubMessageHeadersGetter.cs`:
  case-insensitive on the empty-fallback path, plain-ordinal `ToDictionary` on the populated path).
  While verifying the cookbook's claim that "every built-in getter's headers dictionary is
  case-insensitive by construction" (`docs/cookbooks/multi-tenancy.md:94-97`), found the identical
  inconsistent-branch bug in both `Benzene.Aws.Lambda.Sqs.SqsMessageHeadersGetter` and
  `Benzene.Aws.Sqs.Consumer.SqsConsumerMessageHeadersGetter`, and a plainer always-case-sensitive
  `ToDictionary` (no comparer at all, not merely inconsistent) in
  `Benzene.Azure.Function.ServiceBus.ServiceBusMessageHeadersGetter`,
  `Benzene.Azure.ServiceBus.ServiceBusConsumerMessageHeadersGetter`,
  `Benzene.Azure.Function.EventHub.Function.EventHubMessageHeadersGetter`,
  `Benzene.Azure.EventHub.EventHubConsumerMessageHeadersGetter`, and (the #164-class defect)
  `Benzene.Azure.Function.AspNet.AspNetMessageHeadersGetter`. Fixed all eight getters onto
  `StringComparer.OrdinalIgnoreCase` consistently (checked every other built-in getter too — S3,
  DynamoDB, EventBridge, RabbitMq, Grpc, all three Kafka getters, QueueStorage, EventGrid, both API
  Gateway getters via `DictionaryUtils.Replace`, and `Benzene.AspNet.Core`'s own headers getter — all
  already correct). The cookbook's claim is now true for every built-in getter; left the doc line as
  written and added one clarifying sentence about why `GetHeader` is still the recommended lookup even
  so (a custom `IMessageHeadersGetter<TContext>` has no obligation to follow suit). Regression tests
  added to each transport's existing getter test file.
### Tracked findings round 11, WP-4 — Cache write-through/cancellation/serializer hardening (done)
Ruled in [`bug-fix-designs-round11-2026-08.md`](archive/bug-fix-designs-round11-2026-08.md) §3 "Cache" half
(`Benzene.Cache.Core`/`Benzene.Cache.Redis`).
- **[RESOLVED] #139 — write-through failure handling was backwards.** A cache-side exception thrown
  *after* the database write had already committed (e.g. `Serializer.Serialize` failing inside
  `SetValueAsync`, called from `WriteThroughAsync`'s `Set` branch) propagated uncaught out of
  `WriteThroughAsync`/`WriteThroughInvalidateAsync`, surfacing an already-successful write as the
  overall operation's *failure* and inviting a caller to retry a write that had, in fact, succeeded.
  Separately, `InvalidateAsync()`'s `bool` return was discarded at both of its `WriteThroughAsync`/
  `WriteThroughInvalidateAsync` call sites, so a cache provider honestly reporting a failed invalidate
  (Redis already did this correctly - catches its own exceptions, logs a `Warning`, returns `false`)
  had that signal silently thrown away one layer up, with no correlation back to which write's cache
  sync had failed. Fixed with one new `CacheInvalidateActions.SyncCacheAfterWriteAsync` helper used by
  both `WriteThroughAsync`'s `Set`/`Invalidate` branches and `WriteThroughInvalidateAsync`: it runs the
  cache-side action, and either an exception or a `false` result is logged (`Warning`, with the key and
  which action) and swallowed - the already-successful database result is still returned. A caller-
  driven `OperationCanceledException` is the one exception that still propagates (that's not a cache
  failure to log past). No new result type / interface redesign - `SetValueAsync`/`InvalidateAsync`
  themselves are unchanged for direct callers outside write-through, where an exception is still the
  primary requested action's own failure, not a second phase after a committed success.
- **[RESOLVED] #140 — see the replaced `[DECISION]` entry above** (cached-null negative caching).
- **[RESOLVED] #141 — the entire cache surface (`ICacheService`/`ICacheInvalidateActions`/
  `ICacheWriteActions<T>`/`ICacheEntry<T>`, 10/10 members) was uncancellable, and `RedisCacheService`'s
  connect had no deadline of any kind - internal or caller-supplied - so a hung Redis connect held
  every in-flight request past client disconnect and host shutdown.** Every interface member now takes
  an optional trailing `CancellationToken cancellationToken = default` (source-compatible; binary-
  breaking, acceptable pre-1.0 per `version.txt` `0.0.3`/no git tag - see
  `work/benzene-result-errors-ruling.md` §3.5/§4.1 for the standing precedent on this repo's freeze
  policy). The token flows into the `protected abstract` primitives (`GetEntryValueAsync`/
  `SetEntryValueAsync`/`InvalidateEntryAsync`) and from there into every Redis call via
  `Task.WaitAsync(cancellationToken)` (StackExchange.Redis's `IDatabase` methods don't accept a
  `CancellationToken` themselves, so this is the standard wrap-a-non-cancelable-task idiom - it bounds
  *this caller's* wait; the underlying Redis operation keeps running in the background, same as
  `UseTimeout`'s documented "only interrupts cooperative work" caveat elsewhere in the repo).
  `RedisCacheService.RedisSetup` now applies the same `WaitAsync(cancellationToken)` to the shared,
  memoized connect task - deliberately NOT by cancelling the shared task itself (that's awaited by
  every concurrent caller; cancelling it for caller A would break caller B's unrelated in-flight wait
  too), but by bounding each caller's own wait on it. `CacheHealthCheck<TCacheService>` now forwards
  its own `cancellationToken` into `CanConnectAsync` (previously documented as "out of WP-7's scope" -
  that scope note is removed, it's in scope now). Every Redis-layer catch block also gained an explicit
  `catch (OperationCanceledException) { throw; }` guard ahead of its catch-all, so a caller-driven
  cancellation is never misreported as an ordinary cache miss/failure - the same pattern already
  established for health checks. **Scoped out:** the caller-supplied `Func<Task<TResult>>`
  `modifyDatabaseFunc`/`databaseReadFunc` delegates on `WriteThroughAsync`/`WriteThroughInvalidateAsync`/
  `LazyLoadAsync` are unchanged (still zero-arg) - the token is threaded through the *cache's own* I/O
  only, not into arbitrary caller-supplied database delegates, which is what the finding's "entire cache
  surface" scoped as broken. Retrofitting a `CancellationToken` parameter onto those delegates too would
  be a much larger, independently-justified API change; nothing in this WP needed it. See the new
  `[DECISION]` entry below recording this as the intentional residual scope boundary.
- **[RESOLVED] #144 — per-call TTL was unreachable through `LazyLoadAsync`/`WriteThroughAsync`**, even
  though `SetValueAsync` always exposed `expireIn`. Both now take an optional `TimeSpan? expireIn = null`
  that flows through to the underlying `SetValueAsync` call (`LazyLoadAsync`'s cache-aside write-back,
  `WriteThroughAsync`'s `Set` branch); `null` still means "use `DefaultCacheLifespan`", unchanged.
- **[RESOLVED] #145 — the cache layer hard-wired `System.Text.Json` in `CacheWriteActions()`'s
  constructor** (`new Benzene.Core.MessageHandlers.Serialization.JsonSerializer()`, ignoring whatever
  `ISerializer` DI had registered, reallocated fresh per cache-entry instance - one per
  `CreateCacheEntry<T>`/`CreateMultiKeyActions<T>` call). `CacheWriteActions<T>`/`CacheEntry<T>` now
  take an optional constructor `ISerializer? serializer`; `RedisCacheService`'s constructor takes the
  same (optional, resolved automatically by DI when a subclass is constructed through it, since
  `Benzene.Core.MessageHandlers` already `TryAddSingleton<ISerializer, JsonSerializer>()`s one) and
  passes its own `Serializer` into every `RedisCacheEntry<T>`/`RedisMultiKeyActions<T>` it creates, so
  every cache entry from the same service instance shares one serializer. When nothing is supplied
  anywhere, a single new `CacheSerializerDefaults.Serializer` static (`Benzene.Cache.Core`) is shared
  process-wide instead of allocating fresh per instance.
- **[RESOLVED] #146 — `RedisCacheService.DisposeAsync` had no disposed flag**, so a `RedisSetup()`/
  `StartConnection()` call arriving after disposal silently opened and cached a brand-new
  `IConnectionMultiplexer` that `DisposeAsync` had already returned and would never be asked to dispose
  again - a leak. A `_disposed` flag (guarded by the same `_connectionLock` already serializing connect/
  dispose) now makes `GetConnectionTask()` throw `ObjectDisposedException` once disposed, and makes
  `DisposeAsync` itself idempotent by that same flag (previously it happened to be idempotent only as a
  side effect of the connection-task field being nulled on first call).
- **[RESOLVED] #147 — `RedisMultiKeyActions.SetEntryValueAsync` wrote its N keys in a sequential
  `foreach`, so an exception on key 2 of 3 aborted the loop (key 3 never attempted) while key 1's
  already-recorded success still made the method report overall success** - a partial write silently
  reported as if every key had been considered. Redis has no native multi-key `SET`-with-per-key-TTL
  primitive (`MSET` has no expiry support at all - a genuine transaction was considered and rejected:
  `Benzene.Cache.Redis`'s own CLAUDE.md already documents "No atomic / conditional operations" as a
  deliberate boundary of this package, so a `MULTI`/`EXEC` transaction here would have contradicted a
  stated non-goal), so each key is still its own `StringSetAsync` call - but now issued **concurrently**
  via `Task.WhenAll`, with each key's outcome (success, a provider-reported `false`, or a thrown
  exception) captured **independently** rather than accumulated by a loop a throw could abandon midway.
  Every key is always attempted; the aggregate `bool` (`any succeeded`, unchanged contract) reflects
  what actually happened to all of them, not just the ones reached before an early abort.
  `InvalidateEntryAsync` on the same type had the identical sequential-loop shape for `KeyDeleteAsync` -
  replaced with a single atomic multi-key `DEL` (`KeyDeleteAsync(RedisKey[])`), reusing the exact
  batched pattern `RedisWildcardActions` already uses for its own (pattern-based) invalidate path, which
  removes the partial-failure hazard entirely for that path (one Redis command, not N).
- **[DECISION] #141 residual scope: caller-supplied write-through/lazy-load delegates stay
  uncancellable.** `WriteThroughAsync`/`WriteThroughInvalidateAsync`/`LazyLoadAsync`'s
  `modifyDatabaseFunc`/`databaseReadFunc` parameters remain `Func<Task<TResult>>` - the new
  `cancellationToken` parameter is threaded through the cache's own read/write/invalidate I/O only,
  never into these caller-supplied closures. Changing that delegate shape to
  `Func<CancellationToken, Task<TResult>>` would be a second, independently-justified breaking change
  to how every existing caller writes their database/service call, and #141 as filed is specifically
  about the *cache* surface being uncancellable, not about retrofitting cancellation onto arbitrary
  caller code the cache layer merely invokes. A caller that needs its own database call cancelled
  observes the ambient token itself, the same way it would today calling that database directly outside
  `LazyLoadAsync`/`WriteThroughAsync`.

Tests: `test/Benzene.Core.Test/Cache/CacheEntryTest.cs` (negative-caching hit via `SetValueAsync(null)`
then `LazyLoadAsync`; per-call `expireIn` threading), `test/Benzene.Core.Test/Cache/Redis/RedisCacheServiceTest.cs`
(write-through cache-side-failure-doesn't-fail-the-database-result cases for both `SetValueAsync` and
`InvalidateAsync`; `DisposeAsync` then a further call throws `ObjectDisposedException`; multi-key
partial-failure - one key throws, the others still get attempted and the result still reflects an
overall success/failure correctly; cancellation is observed rather than silently ignored). Full solution
build + `Benzene.Core.Test` re-verified green after this WP.
### Tracked findings round 11, §7 Auth adapters (partial - auth-core work package)
Ruled in [`bug-fix-designs-round11-2026-08.md`](archive/bug-fix-designs-round11-2026-08.md) §"§7 Auth
adapters". This covers task board #174, #176, #179, #181, #182 (`Benzene.Auth.OAuth2`,
`Benzene.Auth.Core`, `deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs`); the remaining §7 findings
(#172, #173, #175, #177, #178, #180) are a different work package's scope.
- **[RESOLVED] #174 — `OAuth2BearerOptions.Validate()` had the same bug class as round 1's #20
  (a non-https `Authority`/`JwksUri` with `RequireHttpsMetadata` true reached the JWKS-fetching
  configuration manager unvalidated), plus several length-only allowlist gaps.** Fixed all in
  `Validate()`: (1) an `http://` `Authority`/`JwksUri` is now rejected unless
  `RequireHttpsMetadata` is explicitly `false` - mirrors `MeshAuthGate.Validate`'s existing
  `auth.oidc.authority` check; (2) `ValidIssuers`/`ValidAudiences` now reject
  null/whitespace/`"*"` entries (a `"*"` looked like a wildcard but `TokenValidationParameters`
  has no such concept - it would only ever match a token whose issuer/audience claim was
  literally `"*"`); (3) `ValidAlgorithms` now rejects `"none"` explicitly (named separately from
  the next check, since it's RFC 8725 §3.1's canonical algorithm-confusion attack) and validates
  every entry against a curated allowlist of the real JWS signing algorithms
  `Microsoft.IdentityModel.Tokens.SecurityAlgorithms` defines (HS256/384/512, RS256/384/512,
  ES256/384/512, PS256/384/512) - deliberately narrower than every string constant that class
  exposes, since most of them (XML-dsig URIs, key-wrap, content-encryption algorithm names) are
  never a legitimate JWT `alg` value; (4) `ClockSkew` is now capped at a new
  `OAuth2BearerOptions.MaxClockSkew` (15 minutes) - generous enough for real multi-region NTP
  drift, far too small to meaningfully weaken `exp`/`nbf` enforcement, unlike the unbounded value
  before (a 10-year `ClockSkew` was previously accepted outright). Adversarial tests drive real
  tokens through the real `UseOAuth2Bearer` entry point (not the internal `Validate()` directly) -
  `OAuth2BearerOptionsValidationTest`, 21 new cases covering each rejection and its boundary.
- **[RESOLVED] #176 — `MeshAuthGate`'s proxy-trust check (`trusted.Equals(peer)`) rejected
  IPv4-mapped IPv6 peers** (`::ffff:10.0.0.5` was never treated as equal to `10.0.0.5`),
  breaking `auth.mode: proxy` entirely on any dual-stack listener (Kestrel bound to `[::]`, the
  container default) - fail-closed, not a bypass, but a real operability gap. Fixed by
  normalizing both the peer and each `trustedProxies` entry through a new
  `NormalizeForComparison` helper (`MapToIPv4()` when `IsIPv4MappedToIPv6`, else left as-is)
  before comparing, so either written form (`10.0.0.5` or `::ffff:10.0.0.5`) matches the other.
  Also improved the refusal message to include the observed peer address (`"Untrusted proxy
  (peer: ...)"` - it previously named none), for production diagnosability. Adversarial tests in
  `MeshAuthGateTest`: a peer reported as `::ffff:10.0.0.5` against a `trustedProxies` list
  containing `10.0.0.5` is now admitted (and the mirror image); a mapped peer whose underlying
  IPv4 genuinely isn't trusted is still refused (confirms the fix doesn't widen trust); the
  refusal body now names the observed peer.
- **[RESOLVED] #179 — `AuthorizationExtensions.RequirePolicy(policyName)` resolved its
  `IAuthorizationPolicy` (a DI `GetServices<>()` + `FirstOrDefault` scan by `Name`, throwing if
  none matched) inside the per-request middleware factory instead of once**, so a misconfigured
  policy name surfaced only as a 500 on the first real request, and paid the DI/LINQ cost on
  every request thereafter. The pipeline-builder architecture has no built `IServiceResolver`
  available at the point `RequirePolicy` itself runs (unlike `UseOAuth2Bearer`, which validates
  synchronously at that same call site because its config needs no DI resolution) - so the fix is
  the documented fallback: the resolved policy (or the "not registered" failure) is cached in a
  closure-scoped field after the first lookup, via double-checked locking, and reused on every
  later invocation. A missing policy still throws every time (not cached), so the wiring error
  keeps surfacing consistently rather than "succeeding" after the first failure. Test
  (`AuthorizationTest.RequirePolicy_ByName_ResolvesRegisteredPolicyOnceAndReusesIt`) drives the
  real pipeline (in-process, via `MiddlewarePipelineBuilder`/`MicrosoftServiceResolverFactory` -
  no Kestrel host needed to observe this) through three policy-satisfying invocations and asserts
  the by-name lookup ran exactly once.
- **[RESOLVED] #181 — `MeshAuthGate.IsPermitted` admitted a syntactically-invalid email with an
  empty local part (`"@example.com"`) and trimmed asymmetrically** (leading whitespace on the
  claim value sat in the local part and never touched the extracted domain, so it was tolerated;
  trailing whitespace landed inside the extracted domain and silently defeated the
  `AllowedEmailDomains` match, so it wasn't). Fixed via a new `ExtractDomain` helper: `Trim()`s
  the whole email value first (matching `Benzene.Mesh.Auth.Oidc.EmailAllowlist.IsAllowed`'s
  existing discipline), and requires the `@` to not be the first character (a non-empty local
  part). Adversarial tests in `MeshAuthGateTest`: `"@example.com"` is now `Forbidden`;
  `"alice@example.com "` (trailing space) now still matches a clean `"example.com"`
  `allowedEmailDomains` entry.
- **[RESOLVED] #182 — two role-claim readers in the repo disagreed**:
  `MeshAuthGate.HasAnyRole` read raw claim values ordinally with no JSON-array expansion, while
  `Benzene.Auth.Core.RoleClaims.IsInAnyRole` (used by `AuthorizationExtensions.RequireRole`)
  already expanded a JSON-array `roles` claim (the Azure AD app-roles shape) correctly - the same
  principal could hold a required role by one reader's answer and not the other's.
  `MeshAuthGate.HasAnyRole` now delegates to `RoleClaims.IsInAnyRole` for everything except the
  `groups` claim (this host's own proxy/OIDC-groups convention, which `RoleClaims` doesn't cover
  and isn't a general role claim), instead of reimplementing a weaker version. Required adding
  `InternalsVisibleTo("Benzene.Mesh.Host")` to `Benzene.Auth.Core.csproj` (`RoleClaims` is
  internal) - same production-dependency precedent as `Benzene.Results` → `Benzene.Clients`.
  Adversarial tests in `MeshAuthGateTest`: a JSON-array `roles` claim now satisfies both
  `RequiredGroups` and `DispatchRole` gates identically to how `RequireRole` already treated it.
### Round 11, `Benzene.RateLimiting` hardening (#133–#138, #142, #143) — done
Design/rationale in [`bug-fix-designs-round11-2026-08.md`](archive/bug-fix-designs-round11-2026-08.md)
§"§3 Rate Limiting + Cache" (rate-limiting half). All eight findings landed in one work package;
`RateLimitingMiddlewareBase<TContext>` now holds the shared cost-validation/rejection/logging logic
for both `RateLimitingMiddleware<TContext>` and the new `PartitionedRateLimitingMiddleware<TContext>`.
- **[RESOLVED] #133** — the three convenience `UseFixedWindowRateLimiting`/`UseTokenBucketRateLimiting`/
  `UsePayloadSizeRateLimiting` entry points created an `AutoReplenishment = true` limiter (a live
  `Timer`) that nothing ever disposed — a leak per pipeline build. A middleware-level `ownsLimiter`
  flag alone cannot fix this: a fresh `RateLimitingMiddleware<TContext>` wrapper is constructed per
  message (see `MiddlewarePipeline<TContext>`'s own remarks), so whichever per-message instance
  disposed the *shared* underlying `RateLimiter` first would break every later message. The actual
  fix (`Extensions.UseInternallyOwnedRateLimiting`, private) registers the limiter with the DI
  container via a **factory** registration (`x.AddSingleton<RateLimiter>(_ => rateLimiter)`, never a
  pre-built instance) — the same convention this codebase already relies on for other
  container-created disposables (`RabbitMqConnectionProvider`, `MeshAnnouncer`): a compliant
  container disposes a singleton it constructed itself when the container is disposed, but never an
  externally-supplied instance. `RateLimitingMiddleware<TContext>` still carries the `ownsLimiter`
  flag and implements `IAsyncDisposable` (disposing only when it's `true`) so a caller managing a
  middleware instance's lifetime directly gets correct semantics too, and so ownership is explicit
  at the type level — but the DI registration is what actually closes the leak in the pipeline's
  normal, unmodified lifecycle. Stacking two internally-created limiters on one pipeline is not
  supported (both would silently collide on the same `RateLimiter` DI key) — this now fails fast
  with `InvalidOperationException` instead of letting the second shadow the first; combine limits
  into one `RateLimiter` and use `UseRateLimiting`, or use `UsePartitionedRateLimiting`. Test:
  `RateLimitingPipelineTest.InternallyCreatedLimiter_IsDisposedWhenTheContainerIsDisposed` (drives one
  message through, then disposes the container-owning `MicrosoftServiceResolverFactory` and asserts
  the resolved `RateLimiter` now throws `ObjectDisposedException`) and
  `StackingTwoInternallyCreatedLimiters_OnOnePipeline_FailsFast`.
- **[RESOLVED] #134** — a caller-disposed BYO limiter turned every subsequent message into an
  unhandled `ObjectDisposedException`. `RateLimitingMiddlewareBase<TContext>.HandleAsync` now catches
  `ObjectDisposedException` alongside the existing `ArgumentOutOfRangeException`, failing **CLOSED**
  with the same `TooManyRequests` rejection every other denial gets (never silently failing open —
  documented as the deliberate choice in the base class's XML doc). Test:
  `BringYourOwnLimiter_AlreadyDisposed_FailsClosedInsteadOfCrashing`.
- **[RESOLVED] #135** — `UsePayloadSizeRateLimiting` cannot bound memory: the cost delegate runs
  after ASP.NET Core's `UseBufferedRequestBody()` has already buffered the whole body, unconditionally
  and before any message-pipeline middleware runs. **Partial fix, scope deliberately narrowed** — see
  the amended finding text in `archive/bug-fix-designs-round11-2026-08.md` and the `[DECISION]` below for the
  residual gap this leaves open. Shipped: (1) a `Content-Length` pre-check — when the transport
  reports one (via `IMessageHeadersGetter<TContext>`) and it already exceeds `maxBurstBytes`, the
  cost delegate rejects on the declared size directly, without reading/measuring the
  already-buffered body; (2) the XML doc on `UsePayloadSizeRateLimiting`, `docs/rate-limiting.md`,
  and the capability matrix now say plainly that this is a rate bound, not a memory bound, and name
  the residual gap (no `Content-Length` means no protection at all, and the buffering itself is
  never prevented). Test: `PayloadSizeLimiting_DeclaredContentLengthOverTheBucket_RejectsWithoutReadingTheBody`.
- **[RESOLVED] #136** — partitioned-limiter support was documented in four places, didn't compile
  (`PartitionedRateLimiter<T>` cannot convert to `RateLimiter`), and didn't exist. Implemented for
  real rather than striking the docs: `PartitionedRateLimitingMiddleware<TContext>` +
  `UsePartitionedRateLimiting` over a caller-supplied `PartitionedRateLimiter<TContext>` (the
  partition-key selector is baked into the limiter by the caller via
  `PartitionedRateLimiter.Create<TContext,TKey>`; this middleware just calls
  `AttemptAcquire(context, cost)`, letting the partitioner read whatever it needs off the message).
  Always BYO — there is no built-in convenience entry point, since the partition key is inherently
  caller-specific — so its disposal defaults to caller-owned, matching `UseRateLimiting`. Documented,
  everywhere the capability is claimed, that a client-supplied key is spoofable but still strictly
  better than the single shared limiter every other entry point defaults to (an attacker must expend
  active effort to defeat it, versus zero effort to exhaust a shared limiter today). Test:
  `PartitionedLimiter_OneAbusivePartition_DoesNotStarveTheOther` (an "abuser" partition is throttled
  after 1 message while a "victim" partition keyed differently sails through).
- **[RESOLVED] #137** — 429 responses never carried `Retry-After` despite the limiter supplying
  `RETRY_AFTER` lease metadata on the non-queuing path. `RateLimitingMiddlewareBase<TContext>` now
  reads the metadata and sets the standard `Retry-After` response header via
  `IBenzeneResponseAdapter<TContext>` (best-effort — resolved via `TryGetService`, so a transport
  with no response-header concept simply skips it), matching the pattern already used in
  `MeshRefreshGuardMiddleware`/`MeshDispatchGuardMiddleware`. `SlidingWindowRateLimiter` never
  supplies the metadata, so it never gets the header — documented, not a bug. Test:
  `OverTheLimit_SetsRetryAfterHeaderFromTheLease`.
- **[RESOLVED] #138** — rate-limit rejections were completely unobservable. `RateLimitingMiddlewareBase<TContext>`
  now takes an optional `ILogger` (resolved via `TryGetService` in `Extensions.cs`, so it's a no-op
  when nothing is registered) and logs a structured warning on every rejection, naming the limiter
  type (or `"partitioned, partition=<key>"` when a partition-key-for-logging selector was supplied)
  and the rejection detail (which now also carries the cost/disposal distinction from #142/#134).
- **[RESOLVED] #142** — the oversized-payload rejection (the `ArgumentOutOfRangeException` path)
  gave a bare `"Rate limit exceeded"`, indistinguishable from a normal throttle. It now reads
  `"Rate limit exceeded: the message's cost is invalid, or exceeds the limiter's capacity and can
  never be granted"`. Test: `PayloadSizeLimiting_RejectsAPayloadLargerThanTheBucket` (asserts the
  distinguishing substring).
- **[RESOLVED] #143** — `Math.Max(0, cost)` silently clamped a negative cost to 0 (always granting
  it, hiding a caller bug in the cost delegate), and the cost delegate ran outside the `try` block so
  a throwing delegate escaped unhandled and bypassed the limiter entirely. Fixed:
  `RateLimitingMiddlewareBase<TContext>` now validates `cost >= 0` explicitly, throwing
  `ArgumentOutOfRangeException` for a negative cost (routed through the same #142 rejection path as
  any other invalid cost, rather than a second bespoke path) instead of clamping; the cost delegate
  invocation moved inside the same `try` as the acquire call, so a delegate that throws for any other
  reason still propagates unhandled (a genuine bug, not a rate-limit decision — it must reach the
  app's own exception handling, not be silently swallowed into a 429). Tests:
  `BringYourOwnCost_NegativeCost_IsRejectedRatherThanSilentlyGranted`,
  `BringYourOwnCost_ThrowingDelegate_PropagatesRatherThanBypassingTheLimiter`.

**[DECISION] #135 residual gap — payload-size limiting still cannot prevent the upstream buffering.**
The Content-Length pre-check above is a genuine, shipped improvement, but it runs inside
`Benzene.RateLimiting`'s own middleware, which is structurally downstream of `Benzene.AspNet.Core`'s
`BenzeneExtensions.cs` calling `pipeline.UseBufferedRequestBody()` unconditionally before any
caller-supplied middleware runs. No change inside `Benzene.RateLimiting` can run earlier than that.
Closing this fully needs one of: (a) an async, stream-aware cost delegate evaluated before
buffering — a larger redesign of this middleware's synchronous `Func<IServiceResolver, TContext,
int>` cost shape, or (b) making `UseBufferedRequestBody()`'s placement conditional in
`Benzene.AspNet.Core` (a different package, out of this work package's scope and file footprint —
touching it risked colliding with sibling round-11 work packages editing other files in the same
build). Until one of those lands, the honest position (now documented in the XML doc,
`docs/rate-limiting.md`, and the capability matrix) is: this middleware bounds the *rate* of
oversized payloads reaching the handler, not the peak memory a single request costs the process: a
genuine memory bound for a payload-size-sensitive endpoint still needs a host-level cap in front of
Benzene entirely (Kestrel's `MaxRequestBodySize`, a gateway body-size limit).
### Tracked findings round 11 — Benzene.Mesh.Auth.Oidc DI-lifetime/CSRF/fail-fast fixes (done)
Task board #172, #173, #175, #177, #178, #180 (see `work/archive/bug-fix-designs-round11-2026-08.md` §7). All
five worth-fixing items plus the one minor item assigned to this package are landed; full design
rationale and cross-references to round 1's #4/#20 rulings are in that doc.

- **[RESOLVED] #172 — `OidcSessionGateMiddleware` was registered `AddSingleton` despite taking a SCOPED
  `IOidcSessionSink` through its constructor**, so the container resolved that scoped sink exactly once,
  at whichever scope happened to ask for the middleware first, and pinned that one instance (and the
  middleware itself) for the rest of the container's life — every later request's identity silently
  attributed to whatever the first request captured. Now registered `AddScoped` in `Extensions.cs`,
  matching `Benzene.Auth.Basic.BasicAuthMiddleware`/`Benzene.Auth.OAuth2.OAuth2BearerMiddleware`'s
  identical pattern for the identical reason. `examples/AwsMesh/Mesh/OidcDispatchIdentitySink.cs`'s doc
  comment (which asserted "both sides are scoped" while the gate side actually wasn't) now documents the
  historical bug and the fix explicitly. New test:
  `OidcSessionGateMiddlewareDiScopeTest.ResolvingFromTwoDifferentScopes_EachGetsItsOwnMiddlewareAndSessionSinkInstance`
  resolves the real `UseMeshOidcAuth` registration from two separate DI scopes (a real
  `MicrosoftBenzeneServiceContainer`) and proves each scope gets its own middleware instance and its own
  sink instance, and that one scope's `Authenticated` call never touches the other's sink.
- **[RESOLVED] #173 — `MeshOidcOptions.Validate()` accepted a non-HTTPS `Issuer` with
  `RequireHttpsMetadata` still `true`, crashing OIDC discovery as an unhandled 500 at request time.**
  This is round 1's #20, previously fixed only in `deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs` and
  never carried into this package. `Validate()` now runs the identical check (mirrored, not
  reinvented). Also added: a `try`/`catch` around OIDC discovery in both `OidcLoginMiddleware` and
  `OidcCallbackMiddleware` (previously absent entirely on login, and not covering discovery specifically
  on callback), denying with a new shared `OidcDiscoveryFailureResponse` (`503`, generic body) instead of
  an unhandled 500 — covers both a lingering misconfiguration and a transient IdP outage. Tests:
  `MeshOidcOptionsValidateTest` (non-https issuer throws; `RequireHttpsMetadata: false` escape hatch
  still works; https issuer unaffected), `OidcLoginMiddlewareTest.DiscoveryFailure_DeniesCleanly_NeverThrows`
  and `OidcCallbackMiddlewareTest.DiscoveryFailure_DeniesWithServiceUnavailable_NeverThrows` (both against
  a genuinely unreachable loopback endpoint, not a mocked exception).
- **[RESOLVED] #175 — `OidcLogoutMiddleware` handled logout on a bare GET with no CSRF defense**, so a
  cross-site GET (e.g. an `<img>` tag) could sign a victim out, since `SameSite=Lax` still sends the
  cookie on a top-level GET navigation. This directly contradicted round 1's #4 ruling, already
  implemented correctly in `MeshAuthGate.HandleLogoutAsync` (405 on GET, require POST + the
  `X-Benzene-Logout` header) — mirrored here exactly, including the header name. The response contract
  also changed from a 302 redirect to JSON (`{"redirect":null}`, this package resolves no IdP
  end-session endpoint), because `Benzene.Mesh.Ui`'s shared Sign-out control already `fetch()`es this
  endpoint expecting exactly that JSON shape (it was written against `MeshAuthGate`'s contract first, and
  is shared by both this package's hosts and `MeshAuthGate`-gated ones) — the old 302 would have been
  followed transparently by `fetch()` and failed `.json()` parsing. This package's own `CLAUDE.md` is
  corrected (it documented the old, superseded "no CSRF protection on /logout, deliberately" position).
  Tests in `OidcLogoutMiddlewareTest`: GET on the logout path → 405; POST without the header → 403; POST
  with the header → clears the session cookie and returns 200 + the JSON body; header-name lookup is
  case-insensitive; a present-but-blank header value is still rejected.
- **[RESOLVED] #177 — `MeshOidcOptions.SigningKey` was checked for byte length only (≥32 bytes), so a
  32-character repeated character passed** — and that key signs both the CSRF state token and a session
  cookie that is a deterministic function of `{Email, Exp}` with no randomness of its own, making a
  low-entropy key a full session-forgery vector. `Validate()` now also rejects a key with fewer than 8
  distinct byte values across its whole length (a pragmatic floor, not a real entropy estimator — see its
  own remarks for exactly what it does and does not catch: it catches a repeated/near-constant string,
  not a guessable-but-diverse dictionary phrase). Tests: `LowEntropyRepeatedCharacterSigningKey_Throws`,
  `LowEntropyShortAlternatingPatternSigningKey_Throws`, and `SigningKeyWithEnoughDistinctBytes_IsAccepted`
  (hex-shaped and passphrase-shaped keys both still pass). The pre-existing
  `SigningKeyExactly32Bytes_IsAccepted` test's fixture (`new string('k', 32)`) was itself an example of
  the vulnerability and has been changed to a genuinely high-entropy key; the old shape now has its own
  dedicated "must throw" test instead.
- **[RESOLVED] #178 (minor) — OIDC logout was client-side only with no per-session identifier, and this
  was not documented as a deliberate decision anywhere.** Added a random `Jti` to `OidcSessionPayload`
  (defaults to `""` so a two-argument construction/deserialization of a pre-existing payload still works)
  so a future deny-list could revoke one specific session without a cookie-format break — nothing reads
  or checks it yet, by design; building the store itself is a real feature, not folded into this fix.
  Added an explicit "Stateless logout (deliberate) - and its consequence" section to this package's
  `CLAUDE.md` naming the tradeoff plainly: a leaked/stolen cookie stays valid until its own `Exp`,
  regardless of the original holder logging out. `MeshOidcOptions.SessionDuration`'s doc comment now
  explains why its lack of an enforced upper bound matters more here than usual, given stateless logout —
  scoped down to a documentation fix rather than an enforced cap (see `[DECISION]` below).
- **[RESOLVED] #180 (minor) — the post-login `returnTo` path was built from the LOWERCASED request**
  (`HttpRequest.AsLowerCase()` lowercases `Path` as well as header names), breaking a case-sensitive deep
  link (an S3 object key, a service-cased JSON route) with a 404 after an otherwise-successful login.
  `OidcSessionGateMiddleware.HandleAsync` now keeps the original-cased request alongside the lowercased
  one and feeds ONLY the original-cased `Path` into `BuildReturnTo`, while header lookups (cookie,
  accept) keep using the lowercased copy. New test:
  `OidcSessionGateMiddlewareTest.NoSessionCookie_HtmlRequest_RedirectsToLogin_PreservingOriginalPathCasing`.

`[DECISION]` (recorded, not deferred — see `work/archive/bug-fix-designs-round11-2026-08.md`'s scope note for
this round): `MeshOidcOptions.SessionDuration` is NOT given an enforced upper bound. A hard cap (e.g. 30
days) would reduce the blast radius of a leaked cookie further, but risks breaking a deployment that
genuinely wants a longer "remember me" duration, and this package has no way to know what's genuinely
needed for a given deployment. Documented instead (property remarks + `CLAUDE.md`) so the tradeoff is
explicit rather than silent; revisit if a real deployment's incident shows the uncapped default causing
harm in practice.
### Tracked findings round 11, §6 — spec/descriptor/CloudService/Probe pipeline (task board #166–#171, done)
Ruled in [`bug-fix-designs-round11-2026-08.md`](archive/bug-fix-designs-round11-2026-08.md) §6 (now
archived; every other round-11 work package, #121–#165 and #172–#182, has also landed — see the
top-of-file summary blockquote).

- **[RESOLVED] #166 — generated typed clients turned every enum property into an empty C# class with
  no members**, so a real generated client sent `"status":{}` on the wire and got HTTP 400 even with
  `JsonStringEnumConverter` applied. Root cause: `OpenApiSchemaCSharpTypeBuilder.BuildSimpleType`
  emitted a class for every catalogue schema with no branch on `schema.Enum`. Fixed: a schema with
  `enum` entries now emits a real C# `enum` (`OpenApiSchemaCSharpTypeBuilder.BuildEnumType`) — a
  string enum (Swashbuckle's shape for a `[JsonConverter(typeof(JsonStringEnumConverter))]` C# enum)
  gets that same converter applied and each enum value used verbatim as the member name; an integer
  enum gets each numeric value as an explicit member value (System.Text.Json already serializes an
  int enum as its number by default, and the schema carries no name metadata to recover for it
  anyway). `CSharpTypeName.GetName`'s existing `$ref` handling (`Reference.Id`) already resolves to
  the enum's own name correctly since a class and an enum share the same catalogue name — verified by
  a new regression test rather than changed. New `EnumClientGenerationTest` drives the real
  `SchemaBuilder` → `OpenApiSchemaCSharpTypeBuilder` pipeline against a request DTO with both enum
  shapes, compiles the generated code with Roslyn, loads the compiled assembly, and actually
  serializes an instance to confirm the wire shape is a real value (not `{}`).
- **[RESOLVED] #167 — `CloudServiceProfileReport` reported R8 (trace context propagation) satisfied
  whenever `MeshEnabled` was true, but `UseMeshTrace` is only actually wired in `Extensions.cs` when a
  trace exporter is *also* resolved (`TraceExporter` or `CollectorEnvelopeUrl`)** — the default wiring
  (mesh on, no collector) falsely claimed R8 satisfied while `MeshSpan.Current` was genuinely null.
  Fixed by hoisting the `traceExporter` resolution in `Extensions.cs` above the
  `CloudServiceProfileReport.Evaluate` call and passing that same resolved value in, so the pipeline
  wiring and the report read one shared value instead of duplicating (and drifting from) the
  condition. `UseBenzeneCloudServiceTest.ProfileReport_EvaluatesTheWiringHonestly`'s `noCollector`
  case — previously asserting the buggy `{ "R6" }` — now asserts `{ "R6", "R8" }`; two new focused
  tests (`ProfileReport_MeshEnabledWithNoCollectorOrExporter_R8IsNotSatisfied`,
  `ProfileReport_MeshEnabledWithAnExplicitTraceExporter_R8IsSatisfied_EvenWithNoCollector`) cover both
  sides of the corrected condition.
- **[RESOLVED] #168 — `benzene diff` (`SchemaCompatibilityComparer.CompareSchemas`) never recursed
  into `additionalProperties`, so a breaking change entirely inside a `Dictionary<string, T>`-shaped
  schema's value type passed the CI gate as "No changes"** — distinct from the existing `[DECISION]`
  entry above about `.Enum`/`.Nullable`/facet classification gaps, which needs new `SchemaChangeKind`
  values and policy calls; this needed only the missing recursive call the `Items` branch already
  modelled. Fixed: `CompareSchemas` now recurses into `AdditionalProperties` when both sides have one,
  and reports a breaking `TypeChanged` when the map's value schema appears/disappears entirely on one
  side, mirroring the `Items` branch exactly. New tests in `SchemaCompatibilityComparerTest`: a
  `Dictionary<string, Address>`-shaped property where `Address` gains a breaking type change and a new
  required property (`AdditionalPropertiesValueSchema_BreakingChangeInside_IsDetected`, asserts
  `report.Overall == Breaking`), the value-schema-appears/disappears case, and an unchanged-map guard.
- **[RESOLVED] #169 — the derived spec's schema property names were PascalCase
  (`SchemaBuilder` passed a bare `new JsonSerializerOptions()` to `JsonSerializerDataContractResolver`)
  while the wire, the spec's own `example` block, and the sibling `.service.json` from the same build
  are all camelCase** — one `benzene-descriptor --emit both` run produced self-contradictory casing.
  Fixed: `SchemaBuilder` now passes `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`. This is a
  wire-format-output change with broad golden-file churn — see the design doc's §6 and this session's
  final report for the touched test files; the three downstream camelCase-patching consumers named in
  the design doc (`ExamplePayloadBuilder.CamelCase`, `MeshSchemaGenerator`'s reflection-based naming,
  `CodeGenHelpers.Camelcase`) were reviewed and left as-is: none of them consumes `SchemaBuilder`'s
  output exclusively (`MeshSchemaGenerator` derives its own schema from CLR types via reflection,
  independent of `SchemaBuilder` entirely; `ExamplePayloadBuilder`/`CodeGenHelpers` are general-purpose
  string utilities also exercised directly by tests with hand-built, non-camelCase input), so removing
  them risked breaking call sites this fix doesn't touch for no behavioural gain now that the root is
  fixed (redundant-but-harmless). Two genuine ripples surfaced by the full-suite run and fixed in the
  same pass (not just test-fixture churn — real behaviour regressions the casing fix would otherwise
  have introduced): (1) `OpenApiValidationSchemaBuilder.AddSchema` matched a validation rule's own
  reflected (PascalCase) member name straight against `schema.Properties` — now camelCase — so every
  lookup missed and validation facets (`Required`/`MinLength`/`Pattern`/...) silently stopped being
  applied to any schema at all; fixed by camelCasing the rule's key before the lookup. (2)
  `JsonOpenApiSchemaBuilder.CreateObjectSchema` (the JSON-literal inferrer behind `AddJsonEvent`) used
  a nested object's raw JSON property key verbatim as its component schema id — now camelCase, where
  every other builder in this codebase registers component ids as PascalCase type names — so a nested
  inferred schema's id no longer matched the reflection path's for the exact same data; fixed by
  capitalizing the derived id. Golden-file/test-expectation updates: 4 `MessageClientSdkBuilderTest`
  contract-hash fixtures (`LambdaService_{UserGet,UserCreate,UserFull,TenantFull}.txt`); property-key
  casing assertions in `OpenApiValidationSchemaBuilderTest`, `SchemaBuilderPolymorphismTest`,
  `EventServiceDocumentDeserializerTest`; and the raw JSON literals in
  `EventServiceDocumentBuilderJsonTest`/`AsyncApiDocumentBuilderJsonTest` (Newtonsoft-serialized with
  no naming policy, so they no longer matched the now-camelCase reflected schema - camelCased via
  `CamelCasePropertyNamesContractResolver` to represent what a real event body actually looks like).
  Full-suite result after all of the above: `Benzene.Test` 3067 passed/2 skipped/0 failed (3069 total),
  `Benzene.Conformance.Test` 236/236, full `Benzene.sln` build 0 errors.
- **[RESOLVED] #170 (minor) — topic-scoped client generation (`benzene build -output client -topics
  ...`, `MessageClientSdkBuilder`) emitted DTOs (and hashed) the entire service catalogue instead of
  narrowing via `SchemaClosure.Reachable`, unlike `AtomicClientSdkBuilder`, which does it correctly.**
  Fixed: `MessageClientSdkBuilder.BuildCodeFiles` now narrows the scoped document's
  `Components.Schemas` via `SchemaClosure.Reachable` over the surviving requests' request/response
  roots, mirroring `AtomicClientSdkBuilder.ReachableSchemas` — this also fixes the contract hash
  instability the design doc noted (the hash is computed from the same narrowed document). New test:
  `MessageClientSdkBuilderTest.Topics_NarrowsGeneratedSchemas_ToOnlyTheSurvivingTopicsReachableTypes`.
- **[DECISION] #171 (minor, scoped down this round) — `benzene-descriptor --version-scheme` is
  validated at build time (`EmitOptions.ValidateVersion`) then discarded: it never reaches the emitted
  descriptor, because `MeshServiceInfo`/`MeshServiceDescriptor` (`Benzene.Mesh.Wire`) have no scheme
  field.** Carrying the scheme onto the wire descriptor is a spec-adjacent change (touches
  `docs/specification/mesh.md`'s `ServiceDescriptor` shape, its conformance fixtures, and every
  language port's descriptor type) and was judged too large for this fix round. Scoped down to:
  fixed the misleading `Program.cs` comment ("after here the value travels", which was false for the
  scheme specifically) to record this explicitly as a `[DECISION]` in place, rather than leaving it
  silently misleading. Left for a future round to decide whether/how to carry the scheme onto the wire.

- **[RESOLVED] #226 — `CasterFuncBuilder.CreateCasterFunc` memoized a compiled caster delegate only
  *after* `Expression.Lambda(...).Compile()` returned, so a self-referential or mutually-recursive
  versioned DTO shape (`Node.Child: Node`, or two types referencing each other) re-entered
  `CreateCasterFunc` for the same `(TFrom,TTo)` pair before anything was memoized — the guard never
  tripped, recursion was unbounded, and the process died with an uncatchable, unloggable
  `StackOverflowException` (verified exit code 134) reachable through the documented, "fail eagerly at
  registration" `Upcast<TFrom,TTo>()`/`CasterFactory` API on ordinary tree/graph-shaped payloads
  (parent/child categories, org charts, comment threads).** Took the plan's primary ruling — **support
  recursion properly via a lazy indirection cell**, not the cycle-detection-exception fallback: before
  building the mapping expression for `(fromType, toType)`, `CreateCasterFunc` now installs a
  `RecursionCell<TFrom,TTo>` in `_funcs` — a mutable holder plus a stable `Func<TFrom,TTo>` forwarding
  delegate (`cell.Invoke`) bound to it. A recursive lookup for the same pair during expression building
  (via `MapDelegate` from `CreateClassExpression`/`CreateEnumerableExpression`/`CreateListExpression`)
  resolves to that forwarder and the generated expression embeds it as a constant, calling through the
  cell rather than recursing into the builder again. Once `Expression.Lambda(...).Compile()` returns,
  the cell's `Func` is filled with the real compiled delegate and the memoized `_funcs` entry is
  replaced with the direct delegate for the non-recursive fast path — any expression already built
  against the forwarder keeps working (it now resolves through to the real delegate). This is safe
  because `Compile()` only builds the delegate, it never invokes the lambda body, and casters are built
  eagerly at `Upcast`/registration time, so every cell reachable from an outer `CreateCasterFunc` call
  is filled in before any caster is ever actually run. A build/compile failure removes the dangling
  entry (`catch`/`_funcs.Remove(key)`) rather than leaving an unfillable forwarder behind. Null
  termination was already correct and needed no change: `CreateClassExpression`'s existing null-guard
  short-circuits to `default`/`null` without invoking the (possibly-recursive) delegate at all.
  New test `test/Benzene.Core.Test/Core/Versioning/CasterRecursionTest.cs` covers: a self-referential
  type builds and casts correctly (null child, and a 3-level-deep tree with value assertions at every
  level); a mutually-recursive `A`↔`B` pair builds and casts correctly from either side as the root
  type (exercising both orderings of which pair installs the indirection cell first). The crash itself
  (pre-fix) was verified by building an equivalent probe as a separate console app referencing
  `Benzene.Core.Versioning` and running it in a child `dotnet` process: pre-fix it aborted with exit
  code 134 (uncaught `StackOverflowException`, "Stack overflow." dumped by the CLR, unwinding through
  `MapDelegate`→`CreateClassExpression`→`CreatePropertyExpressions`→`BuildClassMappingExpression`→
  `CreateCasterFunc` repeatedly); post-fix the same probe returns exit code 0. That probe was a scratch
  file, not committed — the permanent in-proc regression coverage above is what ships.

## Resolved in round 15, WP-B (AWS trigger family gaps)

Findings from the round-15 review pass (task board #227–#229, `work/archive/bug-fix-plan-round15-2026-08.md`
WP-B; rationale and rejected alternatives in `work/archive/bug-fix-designs-round15-2026-08.md` §2). All three
fixed in `src/Benzene.Aws.Lambda.S3`, `src/Benzene.Aws.Lambda.DynamoDb`,
`src/Benzene.Aws.Lambda.Kafka`, `src/Benzene.Aws.Lambda.Core`, and
`src/Benzene.Core.MessageHandlers/Extensions.cs`.

- **[RESOLVED] #227 — `.UsePresetTopic()`/`.UseTopicFrom()` crashed forever (a
  `BenzeneResolutionException` on every single message) on S3, DynamoDB Streams, and Kafka pipelines,
  because those packages' `AddS3`/`AddDynamoDb`/`AddKafka` DI extensions never registered
  `PresetTopicHolder` or wrapped their topic getter in `PresetTopicMessageTopicGetter<TContext>`, unlike
  Sns/Sqs/EventBridge.** Fixed by registering `PresetTopicHolder` and wrapping
  `S3MessageTopicGetter`/`DynamoDbMessageTopicGetter`/`KafkaMessageTopicGetter` in
  `PresetTopicMessageTopicGetter<TContext>`, copying the shape from
  `Benzene.Aws.Lambda.Sns/DependencyInjectionExtensions.cs`. `UseTopicFrom`'s doc comment
  (`Benzene.Core.MessageHandlers/Extensions.cs`) now lists all three among the supported transports.
  **Scope correction from the plan: Kinesis (the plan's fourth named transport) is not fixed and will
  not be** — investigation found it structurally different from the other three, not merely missing the
  same wiring. `Benzene.Aws.Lambda.Kinesis` has no `IMessageTopicGetter<TContext>`, no `MessageRouter`,
  and no `.UseMessageHandlers()` call site at all: it fans a batch *in* to one
  `StreamContext<KinesisEventRecord>` (its own `CLAUDE.md`: "unlike the SQS/SNS/S3 adapters there are no
  topic/body/header getters to register"), so there is no topic getter to wrap and no
  `PresetTopicMiddleware<TContext>` for a preset to feed. Forcing in an unused topic-getter registration
  would be dead code implying a routing capability the package cannot exercise. `UseTopicFrom`'s doc
  comment and the capability matrix's "Message routing" row both now say so explicitly, so this isn't
  silently re-litigated as a missed spot in a future pass. New tests:
  `S3MessagePipelineTest.Send_UnknownEventName_WithPresetTopic_RoutesToPresetTopic`,
  `DynamoDbMessagePipelineTest.Send_UnknownTopic_WithPresetTopic_RoutesToPresetTopic`,
  `KafkaMessagePipelineTest.Send_UnknownTopic_WithPresetTopic_RoutesToPresetTopic` — each builds an event
  whose native topic/event-name matches no handler and asserts `.UsePresetTopic()` still routes it
  successfully (red before the DI fix: `BenzeneResolutionException` resolving `PresetTopicHolder`).
- **[RESOLVED] #228 — SNS/S3/EventBridge's shared `SingleContextEscalatingApplicationBase.ProcessAsync`
  swallowed infrastructure/DI-wiring failures (e.g. `BenzeneResolutionException`) under
  `CatchExceptions=true`, reporting the invocation healthy while every message failed the same way,
  forever.** Fixed by adding an unconditional rethrow carve-out for `BenzeneFailure.IsInfrastructure(ex)`
  inside the `catch (Exception ex) when (_catchExceptions)` block, mirroring `SqsApplication.cs`'s
  existing carve-out and its stated reasoning (an infra failure isn't the message's fault, isn't
  retryable per-message, and these transports have no partial-failure channel to report it on one record
  at a time). The existing log line is kept and extended with "Infrastructure failure — rethrowing
  despite CatchExceptions" wording so operators see why the invocation failed instead of being logged
  and swallowed. New tests (one per concrete application, since all three share the base class):
  `SnsFailureHandlingTest`/`S3FailureHandlingTest`/`EventBridgeFailureHandlingTest`
  `.HandleAsync_CatchExceptionsTrue_InfrastructureFailure_RethrowsDespiteCatchExceptions` — a pipeline
  mock throwing `BenzeneResolutionException` with `CatchExceptions=true` now rethrows instead of
  completing silently (red before the fix).
- **[RESOLVED] #229 (minor) — SNS/S3/EventBridge treated a null/unset `MessageResult` (the pipeline
  completed without any middleware setting an outcome) as an accepted message, while SQS/DynamoDb
  explicitly treat the same case as a failure ("err toward redelivery, never toward loss").** Fixed by
  changing `SingleContextEscalatingApplicationBase.ProcessAsync`'s escalation check from
  `context.MessageResult?.IsSuccessful == false` to `!= true`, aligning the shared base class on
  SQS/DynamoDb's convention; only an explicit success is now exempt from escalation. The null-result
  semantics are now documented on the base class's `raiseOnFailureStatus` constructor-parameter doc
  comment. Impact is deliberately narrow: normal wiring's `MessageRouter` always sets a non-null result,
  so only a non-standard pipeline that omits it (or short-circuits before it runs) changes behavior — and
  it changes toward failure-visibility, matching `RaiseOnFailureStatus`'s safe-by-default intent. Kafka's
  own null-skip choice (separately documented and justified in `Benzene.Aws.Lambda.Kafka/CLAUDE.md`) was
  deliberately left untouched, per the plan's ruling. New tests: `SnsFailureHandlingTest`/
  `S3FailureHandlingTest`/`EventBridgeFailureHandlingTest`
  `.HandleAsync_DefaultOptions_HandlerNeverSetsMessageResult_Throws*MessageProcessingException` — a
  pipeline mock that never sets `MessageResult` now escalates (throws, with default
  `RaiseOnFailureStatus=true`) instead of completing silently (red before the fix).

### Round 15, WP-C — Azure cancellation + Timer escalation (#230–#232, done)
- **[RESOLVED] #230 — `BoundedFanOut.WhenAllAsync`'s concurrency-limiting semaphore took no
  `CancellationToken`, so an item still queued behind `MaxDegreeOfParallelism` never observed
  cancellation and simply waited for a free slot** (verified: cap of 1, 3 items, item 0 sleeps 300ms,
  cancel at 50ms — all three ran to completion, ~300ms+, zero `OperationCanceledException`). Fixed:
  both `WhenAllAsync` overloads (`src/Benzene.Core.Middleware/BoundedFanOut.cs`) now take an optional
  `CancellationToken cancellationToken = default`, threaded into `semaphore.WaitAsync(cancellationToken)`;
  a queued item cancelled while waiting throws `OperationCanceledException` out of `WhenAllAsync`, which
  every Azure batch trigger already treats as a failed invocation → redelivery (the correct
  drain-abort behavior). `Task.WhenAll` already awaits every started task to completion before
  returning/throwing, so an already-running item is never abandoned un-awaited — confirmed by a
  dedicated regression test, no extra aggregation logic needed. Audited **every** call site repo-wide
  (`grep BoundedFanOut.WhenAllAsync` across `src/`): `AzureFunctionBatchApplicationBase.HandleBatchAsync`,
  `MiddlewareMultiApplication<TEvent,TContext,TResult>`/`<TEvent,TContext>` (×2), `SqsConsumerApplication`,
  and `JaegerTraceSource.SearchAcrossServicesAsync` had a real ambient token in scope and now pass it;
  `KafkaApplication`, `SqsApplication` (Lambda), `S3Application`, `SnsApplication`,
  `ParallelOutboundMiddleware`, and `ScatterGatherExtensions.ScatterGatherAsync` have no
  `CancellationToken` reaching their `HandleAsync`/public signature today, so each now passes `default`
  explicitly with a one-line comment recording why — no unaudited caller left. New tests:
  `BoundedFanOutTest.WhenAllAsync_Bounded_ItemQueuedBehindTheSemaphore_ObservesCancellation`,
  `..._Void_Bounded_ItemQueuedBehindTheSemaphore_ObservesCancellation`,
  `..._Bounded_OnCancellation_AlreadyStartedItemStillRunsToCompletion`
  (`test/Benzene.Core.Test/Core/Middleware/BoundedFanOutTest.cs`).
- **[RESOLVED] #231 — `TimerApplication` never escalated a message-handler's returned failure result,
  unlike every sibling Azure Function batch trigger** (verified: a tick whose handler returned
  `BenzeneResult.UnexpectedError()` completed without throwing — no retry, no failed-invocation
  telemetry). Fixed: new `TimerOptions` (`src/Benzene.Azure.Function.Timer/TimerOptions.cs`) with
  `RaiseOnFailureStatus` defaulting `true` and `CatchExceptions` defaulting `false`, matching every
  sibling package's safe-by-default contract; accepted as an optional third ctor parameter on
  `TimerApplication` (existing two-arg ctor call sites keep compiling unchanged). The escalation/catch
  logic itself moved into a new `TimerTickApplication` (mirroring the `EventGridApplication`/
  `EventGridBatchApplication` split) that `TimerApplication` now wraps: after the pipeline completes, if
  `RaiseOnFailureStatus` and `context.MessageResult?.IsSuccessful == false`, it throws
  `TimerMessageProcessingException`
  (`src/Benzene.Azure.Function.Timer/TimerMessageProcessingException.cs`, carrying the tick's
  `ScheduledFor` — `TimerTriggerInfo.ScheduleStatus?.Next` — mirroring the sibling `*MessageProcessingException`
  shape); `CatchExceptions` contains that throw (or the pipeline's own exception) with a logged error,
  same as every batch trigger. **Deliberate deviation from WP-B's `!= true` convention**: the
  message-routed batch triggers run every item through `MessageRouter`, which unconditionally records
  a result, so an unset result there only ever means the router never ran; Timer has no such
  guarantee — its primary, documented **direct** consumption mode (`UseTick(...)`) never touches
  `MessageResult` at all, so `!= true` would have escalated *every* plain tick by default (a real
  regression against the existing `TimerPipelineTest`/`AzureFunctionCancellationTest` coverage, caught
  by running them before committing). Using `== false` instead escalates only a message handler that
  actually ran (via `UsePresetTopic(...).UseMessageHandlers()`) and explicitly reported failure — which
  is exactly the review's probe — while leaving the direct-tick mode behaviour-preserving. Exposed
  through `UseTimerTrigger(action, Action<TimerOptions> configure)` overloads on both
  `IAzureFunctionAppBuilder` and `IBenzeneApplicationBuilder`. Package `CLAUDE.md`'s "Failure handling"
  section rewritten to document the new default and flags. New tests:
  `test/Benzene.Core.Test/Azure/TimerFailureHandlingTest.cs` (defaults, exception cascade,
  failure-result escalation, `RaiseOnFailureStatus=false` opt-out, `CatchExceptions=true` containment
  of both an exception and an escalated failure result).
- **[RESOLVED] #232 (minor) — three stale "(both flags off)" doc comments left over from the
  `RaiseOnFailureStatus` safe-by-default flip, describing a default that no longer matched reality.**
  Reworded to the sibling packages' "safe-by-default: `RaiseOnFailureStatus` on, `CatchExceptions` off"
  phrasing in `src/Benzene.Azure.Function.EventGrid/EventGridApplication.cs:29`,
  `src/Benzene.Azure.Function.EventHub/Function/DependencyInjectionExtensions.cs:84`, and
  `src/Benzene.Azure.Function.EventHub/Function/EventHubOptions.cs:18` (keeping the latter's
  ordering-tradeoff remark otherwise intact). Doc-only, no test.
### Round 15 — WP-D: mesh exporter flush, collector null-tolerance, schema generator, dead tag (2026-08-29)
- **[RESOLVED] #233 — `HttpMeshTraceExporter.PumpAsync` (`src/Benzene.Mesh.Wire/IMeshTraceExporter.cs`)
  recreated its wait-timeout deadline from a fresh relative `CancelAfter(_flushInterval)` every loop
  iteration, so any channel activity before the timeout fired reset the effective countdown — a
  steady, moderate trickle below `batchSize` never reached a time-based flush at all, only process
  shutdown did (verified: one event/sec for 20s against the default `batchSize=64, flushInterval=5s`
  produced zero POSTs during the whole run).** Fixed by tracking an absolute next-flush deadline
  (`Environment.TickCount64 + flushIntervalMs`), computed once and reset only after an actual flush
  (batch-full or deadline flush); each wait is bounded by however much of the deadline remains, so
  activity can no longer push it back, and an elapsed deadline flushes the buffer even if it's below
  `batchSize`. The shutdown tail-flush is unchanged. New `HttpMeshTraceExporterTest` (3 tests): a
  steady trickle below `batchSize` still produces a POST well before `DisposeAsync` (red before the
  fix — zero POSTs within the wait window; green after), the batch-full path still flushes early
  without waiting for a long deadline, and `DisposeAsync` still tail-flushes a short, unflushed
  remainder on shutdown.
- **[RESOLVED] #234 — `MeshCollectorStore.Register`/`AddEvents`/`AddIssues`
  (`src/Benzene.Mesh.Collector/MeshCollectorStore.cs`) threw `NullReferenceException` on an
  explicit-null wire list (`descriptor.Topics`/`Produces`, `MeshTraceBatch.Events`,
  `MeshIssueBatch.Issues`), violating the mesh spec's "no missing feed ever fails ingestion" collector
  contract — the same defect class already fixed once for a null `Status`/`TopicVersion` field,
  recurring one level up for whole collections.** Fixed: `Register` now coalesces
  `descriptor.Topics`/`descriptor.Produces` to an empty list on the descriptor itself (not just a
  local variable), so every later read of the stored descriptor is safe too; `AddEvents` coalesces its
  `events` parameter; `AddIssues` coalesces `batch.Issues` and, one level down, each issue's
  `ExemplarTraceIds` — the one further unguarded wire-list iteration the sweep turned up. New tests in
  `MeshCollectorStoreTest`: `Register_NullTopicsAndProduces_IsAcceptedAsAnEmptyDeclaredGraph`,
  `AddEvents_NullEventsList_IsAcceptedAsANoOpBatch`,
  `AddIssues_NullIssuesList_IsAcceptedAsALivenessOnlyBatch_AndMarksTheFeedWired` — all deserialize the
  review's exact null-list payloads via `MeshJson.Options` (red before the fix — NRE on each; green
  after). This brings the collector into conformance with the spec's existing text; no fixture edit
  (spec change) was needed or made, per the repo's own rule against changing a fixture to match an
  implementation.
- **[RESOLVED] #235 — `MeshSchemaGenerator.TryGetDictionaryValueType`
  (`src/Benzene.Mesh.Wire/MeshSchemaGenerator.cs`) only recognized string-keyed
  `IDictionary`/`IReadOnlyDictionary`; any other key type (int/enum/Guid-keyed, etc.) fell through to
  the enumerable fallback, deriving a wrong "array of {key,value}" schema shape — but
  System.Text.Json actually serializes any dictionary as a JSON object with string-converted keys
  regardless of key type, so the descriptor misdescribed the real wire format for any handler contract
  using a non-string-keyed dictionary.** Fixed by dropping the `x.GetGenericArguments()[0] ==
  typeof(string)` key-type restriction from the interface match (checked before the enumerable
  fallback, unchanged ordering) — a dictionary of any key type now emits `{"type":"object",
  "additionalProperties":<value schema>}`, matching the real wire shape. The string-keyed path's own
  output is unchanged: `Derive_StringKeyedDictionary_SchemaIsByteIdentical_NoDescriptorHashChurnFromTheFix`
  pins its exact JSON verbatim, byte-identical to before (no descriptor-hash churn for an
  already-correct contract). New `Derive_NonStringKeyedDictionary_StillMapsToObjectWithAdditionalProperties_NotAnArray`
  theory covers `Dictionary<int,string>`, an enum-keyed dictionary, and `Dictionary<Guid,string>` (red
  before the fix — each derived the array-of-{key,value} shape; green after).
- **[RESOLVED] #236 (minor) — `AwsLambdaDiscoveryProvider`'s `benzene:mesh-path` tag was read into
  `SourceOptions["meshPath"]` (with test coverage asserting exactly that), but the only consumer of
  `AwsLambdaInvoke` mesh sources, `LambdaMeshServiceSource`, never read a `meshPath` option at all — a
  known incomplete item from the original self-discovery design doc
  (`work/archive/mesh-self-discovery-design-2026-07.md` §6 item 1: "aligning `LambdaMeshServiceSource`
  to ask for `mesh`" was never finished; `LambdaMeshServiceSource` still sends the fixed
  `benzene:spec`/`benzene:healthcheck` topics through the `BenzeneMessage` envelope, which has no path
  concept). Ruling: remove the dead tag rather than wire it — there is nothing meaningful for
  `meshPath` to do against the envelope-only interrogation this adapter actually performs.** Removed
  the `MeshPathTag` constant, the `options["meshPath"]` write, and the doc remarks
  (`AwsLambdaDiscoveryProvider.cs`'s class remark, `Benzene.Mesh.Discovery.Aws/CLAUDE.md`'s Key-types
  and Tests sections) that described it; deleted
  `AwsLambdaDiscoveryProviderTest.Discover_CarriesMeshPathHintTag`, the test asserting the tag's
  carry-through. This closes the paper trail the design doc's §6 item 1 left open.
### Round 15, WP-E — Polly cancellation + Xml serializer contract (#237, #238, done)
Design/rationale in [`bug-fix-designs-round15-2026-08.md`](archive/bug-fix-designs-round15-2026-08.md)
§5. Plan in [`bug-fix-plan-round15-2026-08.md`](archive/bug-fix-plan-round15-2026-08.md) WP-E.
- **[RESOLVED] #237** — `PollyResilienceMiddleware<TContext>.HandleAsync` discarded the
  `CancellationToken` Polly passes its `ExecuteAsync` callback, silently defeating every
  cancellation-driven Polly strategy (Timeout, Hedging, RateLimiter); the published cookbook
  (`docs/cookbooks/polly-resilience.md`) additionally claimed the token was passed through, which was
  false against the actual source (verified: its own "Testing" sample threw no exception). **Ruling
  applied: fixed it for real, not a doc retreat.** The middleware now exposes Polly's per-attempt token
  to the downstream pipeline via the ambient `CancellationTokenAccessor` — exactly the pattern the
  sibling `Benzene.Resilience.TimeoutMiddleware<TContext>` already uses: for the duration of each Polly
  attempt it links the attempt's token with whatever ambient token was already set
  (`CancellationTokenSource.CreateLinkedTokenSource`, so an outer `UseTimeout` or any host-seeded token
  is never lost), sets the accessor to the linked token before invoking `next()`, and restores the
  prior token in a `finally` once the attempt finishes. `PollyResilienceMiddleware<TContext>` gained an
  optional `CancellationTokenAccessor? accessor` constructor parameter (a private one is created when
  omitted, so direct construction without DI still works); the four `.UseResiliencePipeline(...)`
  overloads now resolve it from the same DI scope as the rest of the pipeline (mirroring
  `.UseTimeout`'s `resolver.GetService<CancellationTokenAccessor>()`), so real usage shares one
  instance with everything else in the scope. This resolves the open design question flagged in
  `work/archive/polly-resilience-plan-2026-08.md` (ship unresolved, "resolve via
  `ICancellationTokenAccessor`, the pattern `TimeoutMiddleware` already uses correctly"). The cookbook's
  "Testing" sample, cancellation section, and the package `CLAUDE.md` are corrected to describe the
  real mechanism and — matching `TimeoutMiddleware`'s own documented caveat — state plainly that this
  can only cancel work that *observes* the ambient token: a `next()` that ignores it still runs to
  completion, and Polly (like .NET cancellation generally) cannot forcibly abort a running `Task`, so
  no `TimeoutRejectedException` is raised either in that case. Tests (`PollyResilienceMiddlewareTest.cs`):
  a Polly timeout strategy actually throws `TimeoutRejectedException` when `next` observes the ambient
  accessor's token (the corrected cookbook sample, run verbatim); the accessor is restored after each
  attempt; an outer ambient token's cancellation survives being linked with Polly's own per-attempt
  token; a `next` that ignores the token runs to completion with no exception even past the deadline
  (the documented caveat, both with and without an explicitly-supplied accessor).
- **[RESOLVED] #238** — `Benzene.Xml.XmlSerializer.Deserialize` broke its own documented null-round-trip
  contract: `Serialize(type, null)` deliberately returns `""` (matching Avro/MessagePack's null-tolerant
  pattern per its own doc comment), but `Deserialize(type, "")` threw `InvalidOperationException` and
  `Deserialize(type, null)` NRE'd outright (unguarded dereference checking for a leading BOM character).
  Fixed by guarding `Deserialize(Type, string)` with `string.IsNullOrEmpty(payload)` → return `null`
  before any parsing, mirroring `Serialize`'s own null guard and matching Avro/MessagePack's
  `string.IsNullOrEmpty(payload) ? null : ...` pattern exactly. The generic `Deserialize<T>(string)`
  overload delegates to the guarded untyped overload, so both are covered by one guard. Malformed
  non-empty XML still throws (unchanged; the guard only short-circuits null/empty). Tests
  (`XmlSerializerTest.cs`): `Serialize(null)` → `Deserialize` round-trips to `null`; `Deserialize` of
  `null` and of `""` (both overloads) return `null` without throwing; a genuinely malformed non-empty
  payload still throws `InvalidOperationException`.
> **Tracked findings, 2026-08-29 (round 15, WP-F) — all six fixed; build/test verification pending
> centralized re-verification.** The round-15 review pass's CodeGen/Schema sweep (task board
> #239–#244) produced six evidence-backed findings across the discriminator-matching comparers, the
> C# client generator, the OpenAPI document builder, the JSON-example schema inferrer, the
> event-service deserializer, and the (explicitly experimental, non-packable) Terraform generator.
> All six landed in one work package, each with red-before/green-after tests. Design rulings remain
> in **[`bug-fix-designs-round15-2026-08.md`](archive/bug-fix-designs-round15-2026-08.md)** §6 (once
> archived); consult it before touching any of this code again.
> **Verification note:** this round landed alongside ~15 other work-package sessions all building the
> full solution concurrently on one shared, resource-constrained host (load averages 100–270 on 4
> cores, confirmed OOM kills) — every WP hit the same wall. Rather than 16 agents fighting the same
> contended host in parallel, the round coordinator is running one centralized
> `dotnet build`/`dotnet test` pass after all 16 work packages merge and the host quiets down. Before
> that centralized pass: every project this WP touched (`Benzene.Schema.OpenApi`,
> `Benzene.Schema.Compatibility`, `Benzene.CodeGen.Client`, `Benzene.CodeGen.Terraform`) was observed
> compiling cleanly (0 errors) during a partial build that reached ~90% of the ~150-project solution
> before being abandoned for time; the full-solution build and the `test/Benzene.Core.Test` run were
> not completed end-to-end under this WP's own session.
- **[RESOLVED] #239 — `SchemaCompatibilityComparer.VariantKey` and its twin
  `JsonSchemaComparer.VariantKey` had dead discriminator-mapping-fallback code: reached only when the
  member has no `$ref` (so `refId` is guaranteed `null` there), it then compared
  `RefTargetName(entry.Value) == refId` — i.e. against `null` — which a mapping value string can never
  equal, so it could never match.** Every inline (non-`$ref`) discriminated-union member fell through
  to purely positional matching regardless of any discriminator mapping — a `oneOf` of two inline
  discriminator-mapped members, purely reordered with byte-identical content, was reported as spurious
  property changes and `HasBreakingChanges == True`, which would fail the `EnsureBackwardCompatible` CI
  gate on a pure no-op reorder. Fixed in both twins identically: `IndexVariants` now precomputes the
  discriminator-mapping entries that don't already name one of the union's `$ref` members
  (`UnclaimedMappingKeys`/its JSON-walker twin) — the entries that, if they identify anything at all,
  must be identifying one of the *inline* members — in the mapping's own declaration order, and pairs
  the *n*-th such inline member with the *n*-th unclaimed entry, giving it a stable `disc:` identity
  that survives the whole union being reordered (member array and mapping moving together). `$ref`-named
  matching (round 11, #152/#53) is untouched — a `$ref` member still keys on its own target name
  unconditionally, before the mapping fallback is ever consulted. New tests in both
  `SchemaCompatibilityComparerTest` and `JsonSchemaComparerTest`: two inline discriminator-mapped
  members reordered (mapping reordered along with them) now produce zero changes, and a genuine
  property removal on one inline member is still caught and attributed to exactly that variant.
- **[RESOLVED] #240 — `CSharpTypeName.GetName`/`GetArrayType` returned a `$ref`'s raw, unsanitized
  `Reference.Id` as a C# type name, while the referenced type's own class declaration
  (`OpenApiSchemaCSharpTypeBuilder`) correctly ran the same id through `CSharpNameFormatter.Format`.**
  Reachable via the documented bring-your-own-schema `SuppliedSchemaCatalog` feature, whose schema ids
  are arbitrary caller strings: a catalogue id `orderItem` generated a correctly Pascal-cased class
  `OrderItem` but a property of the never-generated raw type `orderItem` (CS0246); a hyphenated id
  (`order-item`) produced a hard C# syntax error. Fixed: `CSharpTypeName` now owns a
  `CSharpNameFormatter` instance and routes every `Reference.Id` read (the direct `$ref` case, the
  `oneOf`/`anyOf` shared-`allOf`-base case, and the array-of-`$ref` case in `GetArrayType`) through it,
  so a property/parameter type name and the class it names can never diverge again.
  `MessageClientSdkBuilder.AddMethod`'s method-signature path needed no separate fix — it already calls
  through `_typeName.GetName`, so it picked the fix up transitively. New tests:
  `CSharpTypeNameTest` unit-asserts the formatted-name match (and the hyphenated case's validity)
  directly, and a new `CodegenOutputCompilesTest.GeneratedClient_WithArbitraryCatalogueSchemaId_Compiles`
  theory drives the real builder pipeline for both `orderItem` and `order-item` catalogue ids and
  compiles the generated output with Roslyn.
- **[RESOLVED] #241 — `OpenApiDocumentBuilder.MapOperationType` indexed a fixed 8-verb dictionary
  directly with `HttpEndpointAttribute.Method` (an unvalidated free-form string), so a real but
  unsupported verb (`CONNECT`) or a plain typo (`Gett`) crashed the whole spec build with an opaque
  `KeyNotFoundException` naming neither the bad verb nor which handler/topic/path it came from.**
  Ruling: kept the 8-verb table (it is exactly OpenAPI's supported operation-object set — `CONNECT` has
  no OpenAPI representation, so widening it would be wrong), replaced the raw dictionary index with a
  case-insensitive `TryGetValue`, and on a miss throw a descriptive `InvalidOperationException` naming
  the invalid method *and* the topic and path of the endpoint being mapped (threaded in from
  `CreateOpenApiOperation`, which already has that context). New tests in
  `OpenApiDocumentBuilderTest`: `Gett` and `CONNECT` both throw with the verb, topic, and path all
  present in the message; `get`/`GET`/`Get` all map successfully to the same operation.
- **[RESOLVED] #242 — `JsonOpenApiSchemaBuilder.CreateArraySchema` called `jToken.First()`
  unconditionally when inferring a schema from an example JSON payload (the documented
  `AddJsonEvent(topic, typeName, json)` extension), so an ordinary empty example array anywhere in the
  payload (`{"id":"abc","tags":[]}`) crashed with `InvalidOperationException: Sequence contains no
  elements`.** Fixed: guarded the empty-`JArray` case before calling `.First()`, emitting an array
  schema with an untyped items placeholder (`new OpenApiSchema()` — no `type` keyword, matching
  anything) rather than guessing a type with nothing in the example to infer it from; a non-empty array
  still infers its item schema from the first element exactly as before. New test:
  `CreateSchema("OrderCreated", "{\"id\":\"abc\",\"tags\":[]}")` (the exact review probe) now returns a
  schema instead of throwing.
- **[RESOLVED] #243 (minor) — `EventServiceDocumentDeserializer.GetEvents`/`GetRequests` read the
  `"events"`/`"requests"` array with a null-forgiving `!` and no null-coalescing, unlike the adjacent
  `GetTransports`/`GetTags`, which both null-coalesce to empty** — a document missing either key
  (reachable via an externally-sourced or older-shape baseline passed to
  `SchemaCompatibility.EnsureBackwardCompatible`; Benzene's own emitted documents always include both
  arrays) crashed with `ArgumentNullException` instead of deserializing to an empty array like every
  other missing optional collection here does. Fixed by null-coalescing both to
  `Array.Empty<T>()`, matching the sibling getters exactly. New test:
  `EventServiceDocumentDeserializerTest.Deserialize_DocumentMissingEventsAndRequests_...` deserializes
  a minimal document with neither key to empty `Events`/`Requests` arrays.
- **[RESOLVED] #244 (minor, experimental/non-packable package) — `Benzene.CodeGen.Terraform`'s HCL
  generation (`TerraformEventBridgeRuleBuilder.QuoteList` and other interpolated fields across the
  package) didn't escape `"`/`\` before embedding caller-supplied values (topic names, Lambda names,
  entry points, domains) into generated `.tf` string literals** — the same unescaped-interpolation bug
  class round 14 found in `CodeGen.ApiGateway`/Markdown, now confirmed in a third generator; a value
  containing `"` produced invalid HCL (an early-terminated string literal). Fixed: added one
  `NameFormatter.EscapeHclString` helper (backslash first, then double quote; null/empty-tolerant to
  match the interpolation it replaces) and routed every interpolated string-literal value across
  `TerraformEventBridgeRuleBuilder`, `TerraformLambdaBuilder`, and
  `TerraformLambdaEventBusPermissionsBuilder` through it — `QuoteList`, the rule/target/lambda/role tag
  and name attributes, and the SNS subscription `filter_policy` topic list. Resource *labels* (the
  second quoted string in `resource "type" "label"`, already derived through `NameFormatter.UnderScoreCase`)
  were deliberately left alone — that's a separate identifier-validity concern, not the string-literal-
  escaping bug this fixes. New tests assert a topic containing `"` and one containing `\` each produce
  correctly-escaped output through `QuoteList`.
## Rounds 12–14 fixes (2026-08-29)

- **[RESOLVED] #47 — `MeshAnnouncer.EnsureStarted` permanently disabled the announcer (and could fail
  the triggering invocation) if descriptor derivation threw, rather than only if it returned null.**
  This was the oldest open item on the board; the null-descriptor half had already been fixed since
  #47 was originally filed — `EnsureStarted` correctly reset `_started` to 0 and let the next
  invocation retry when `_descriptorSource.TryGet()`/`.Get(resolver)` returned null. The residual gap
  this closes: if that call **threw** instead — e.g. the invocation's registry (lazy path) genuinely
  isn't ready yet — the exception propagated straight out of `EnsureStarted`, failing whatever
  invocation happened to trigger the lazy start, and left `_started` stuck at 1 forever with the
  announce loop never starting on any later invocation either. Both halves of the class's own
  documented contract (spec §6 — every failure here is swallowed and retried on the next invocation,
  and nothing here ever fails an invocation) were violated. Fixed: wrapped the descriptor-derivation
  call in `MeshAnnouncer.cs` in try/catch; on any exception, reset `_started` to 0 and return without
  throwing — identical reset to the existing null-descriptor path, so a later invocation (with a
  ready registry) retries and starts the announce loop normally. New test
  `MeshAnnouncerTest.EnsureStarted_WhenDescriptorDerivationThrows_SwallowsAndRetriesOnNextInvocation`
  (`test/Benzene.Core.Test/CloudService/MeshAnnouncerTest.cs`) white-box tests `MeshAnnouncer` directly
  against a resolver stub whose `TryGetService<IMessageHandlerDefinitionLookUp>()` throws once then
  succeeds (added `InternalsVisibleTo` from `Benzene.CloudService` to the test assembly, since a
  throwing lazy-path derivation is otherwise unreachable through the public `UseBenzeneCloudService`
  builder API); it confirmed red against the prior source (first call threw to the caller and the
  second call was a permanent no-op — the collector never saw a register POST) and green after the fix
  (first call returns quietly, second call's announce loop registers, observed via a stub
  `HttpMessageHandler` receiving the `benzene:mesh:register` envelope). Full CloudService suite
  (`Benzene.Test`, `FullyQualifiedName~CloudService`): 34/34 passed; full `Benzene.sln` build (incl.
  every test project): 0 errors.
### Tracked findings round 12, WP-H — Mesh.Dispatch: cancellation, audit trail, limiter charging (#185–#187, done)
Ruled in [`bug-fix-designs-round12-2026-08.md`](archive/bug-fix-designs-round12-2026-08.md) §1 (see
`work/archive/bug-fix-plan-rounds12-14-2026-08.md` for the WP-H task text). Files:
`src/Benzene.Mesh.Dispatch/MeshDispatchMessageHandler.cs`,
`src/Benzene.Mesh.Dispatch/MeshDispatchRateLimiter.cs` (no code change needed there — see #187).

- **[RESOLVED] #185 — `MeshDispatchMessageHandler.HandleAsync` hardcoded `CancellationToken.None` into
  the dispatch call**, so `UseTimeout(...)` wrapping `UseMeshDispatch()` gave zero real protection: the
  real, side-effecting dispatch ran to completion regardless of the configured deadline. Fixed by
  resolving the ambient token via an optional `ICancellationTokenAccessor` constructor parameter,
  read at the point of use — the exact idiom `HttpBenzeneMessageClient` already uses
  (`_cancellation?.CancellationToken ?? CancellationToken.None`). No new DI registration was needed:
  `Benzene.Core.MessageHandlers`'s DI extensions already register a scoped `ICancellationTokenAccessor`,
  and `MeshDispatchMessageHandler` is itself registered scoped, so the container resolves it
  automatically. New test `MeshDispatchMessageHandlerTest.ResolvesCancellationTokenFromTheAccessor_AndPassesItToTheDispatcher`
  asserts the exact token flows through to the dispatcher (not `CancellationToken.None`); the review's
  own probe — `UseTimeout(...)` wrapping the handler with a slow mock dispatcher —
  is `UseTimeout_AroundTheDispatchHandler_ActuallyBoundsTheRealDispatchCall`: pre-fix, the dispatch runs
  the mock's full simulated delay regardless of a 50ms deadline; post-fix, it observes cancellation
  well short of that (the assertion bound is deliberately generous — a fraction of the mock's
  simulated work — so it is a mechanism check, not a scheduler-precision check).
- **[RESOLVED] #186 — a thrown dispatch exception (target unreachable, DNS failure, malformed URL)
  left zero audit trail**, unlike every other exit path in the handler, which calls `Audit(...)` first.
  Ruling: audit-then-fail-as-result, never a silent raw throw. Fixed by wrapping the
  `dispatcher.DispatchAsync(...)` call in try/catch; on exception, `Audit(...)` now takes an optional
  `Exception?` parameter (added to the existing private method, same log-call shape every other exit
  path uses — outcome/service/topic/caller-identity, now also carrying the exception when there is
  one) recording outcome `"dispatch-failed"`, then the handler returns
  `BenzeneResult.ServiceUnavailable<RawStringMessage>(ex.Message)` — the same status this codebase's
  other outbound-call boundaries (`HttpBenzeneMessageClient`, `GrpcBenzeneMessageClient`) already use
  for a transport-level send failure, not a new status. New test
  `MeshDispatchMessageHandlerTest.DispatcherThrows_AuditsTheFailure_AndReturnsServiceUnavailable_InsteadOfThrowing`:
  pre-fix, a throwing mock dispatcher's exception propagated raw out of `HandleAsync` with zero logger
  invocations; post-fix, exactly one `LogInformation` call carrying the failure and a
  `service-unavailable` result returned instead.
- **[RESOLVED] #187 (minor) — `MeshDispatchRateLimiter` charged/created a per-target window for
  arbitrary/unregistered service names before the registry validated the service exists**, leaking a
  permanent dictionary entry per distinct garbage name (500 nonexistent names = 500 permanent entries),
  never pruned from within `Benzene.Mesh.Dispatch` itself. Ruling: validate before charging. Fixed by
  reordering `HandleAsync` so the registry existence check (`_registry.Services.FirstOrDefault(...)`)
  now runs *before* `_limiter.TryAcquire(...)` — no change needed inside
  `MeshDispatchRateLimiter` itself, since the fix is entirely about when the handler calls it. An
  unregistered service name is now rejected (`not-found`) without the limiter ever creating an entry
  for it, and a legitimately rate-limited *registered* target is unaffected (the limiter still runs,
  just one step later). New test
  `MeshDispatchMessageHandlerTest.UnregisteredServiceNames_AreRejected_WithoutEverChargingTheRateLimiterWindow`:
  drives 500 distinct nonexistent service names through the handler (each asserted `not-found`, never
  `rate-limited`), then reflects into the limiter's private `_windows` dictionary and asserts a count
  of 0 — pre-fix this was 500.

Full `test/Benzene.Mesh.Test` run after the fix: 560 passed, 0 failed. (One test,
`JaegerTraceSourceTest.GetRecentFlowsAsync_QueriesServicesConcurrently_NotSequentially`, failed on a
single run under an exceptionally loaded shared build host — a pre-existing, WP-H-unrelated
concurrency-timing assertion — and passed cleanly on immediate re-run with no code changes; not a
regression from this work package.)
### Tracked findings round 12–14, WP-I — Mesh Fleet: Tempo correlation fetch + Jaeger fan-out isolation (done)
Decisions, rationale, and the shared-file overlap check against round-15 WP-C are ruled in
`work/archive/bug-fix-plan-rounds12-14-2026-08.md` §"WP-I" and `work/archive/bug-fix-designs-round12-2026-08.md` §2.
- **[RESOLVED] #188 — `TempoTraceSource.GetCorrelationAsync` fetched up to 100 matched traces fully
  sequentially with zero per-trace fetch isolation, unlike `Benzene.Mesh.Fleet.Aws.XRay`'s correct
  pattern; one trace's transient HTTP failure mid-loop discarded the entire correlation search,
  including every trace already fetched successfully.** Per-trace fetches now run through
  `Benzene.Core.Middleware.BoundedFanOut`, each wrapped in its own try/catch (a failing fetch is logged
  via the new optional `ILogger?` constructor parameter and skipped; `OperationCanceledException` still
  propagates), so a mid-loop failure degrades to "the healthy traces," never the whole search. Bounded
  by a new `TempoTraceSourceOptions.SearchConcurrency` (default 8, matching Jaeger's
  `SearchConcurrency` default) rather than one-at-a-time. See WP-I.
- **[RESOLVED] #189 (minor) — Jaeger's per-service search fan-out (over the shared
  `Benzene.Core.Middleware.BoundedFanOut`) capped concurrency but had no per-item failure isolation; one
  faulted per-service task discarded every other service's completed results via `Task.WhenAll`'s fault
  semantics.** Fixed at the `JaegerTraceSource.SearchAcrossServicesAsync` call site (not in
  `BoundedFanOut.cs` itself — see the shared-file note below): each per-service body now catches its own
  exception, logs it (new optional `ILogger?` constructor parameter) with the failed service name, and
  contributes an empty result for that service instead of faulting the fan-out, so the healthy services'
  results still return. See WP-I.
- **[RESOLVED] #190 (minor) — Tempo's correlation search limit was hardcoded to 100 with no override
  and no warning when hit, unlike Jaeger's `SearchLimitPerService` or X-Ray's #77-fixed logged-warning
  pattern.** Lifted into `TempoTraceSourceOptions.SearchLimit` (default 100, preserving prior behavior);
  `GetCorrelationAsync` now logs a warning via the same optional `ILogger?` when the search returns a
  full page at the configured limit (the result may not cover every matching trace), rather than
  truncating silently. See WP-I.
- **Shared-file note (BoundedFanOut):** `src/Benzene.Core.Middleware/BoundedFanOut.cs` is confirmed
  shared (Jaeger, MapReduce's `ScatterGatherExtensions`, `Benzene.Clients`' `OutboundParallelExtensions`/
  `ParallelOutboundMiddleware`, several Azure/AWS Lambda batch applications all reference it) — **this
  WP did not modify `BoundedFanOut.cs` at all.** Both #188's and #189's per-item isolation are
  implemented entirely at the call site (the body lambda passed into `BoundedFanOut.WhenAllAsync`
  catches its own exception and returns a sentinel/empty result instead of letting it fault the
  `Task.WhenAll`), so there is **no file-level overlap and no expected merge conflict** with round-15
  WP-C's `CancellationToken`-parameter addition to `BoundedFanOut.cs` — that file is untouched by this
  commit. (Tempo's fetch loop newly takes a dependency on `BoundedFanOut` via a new
  `Benzene.Core.Middleware` project reference in `Benzene.Mesh.Fleet.Tempo.csproj`, but does not touch
  its source.)
### Tracked findings round 12–14, WP-K — Saga: rollback on state-store failure + multi-failure surfacing (done)
Ruling and rationale are recorded in
[`bug-fix-plan-rounds12-14-2026-08.md`](archive/bug-fix-plan-rounds12-14-2026-08.md) §"WP-K — Saga: rollback
on state-store failure + multi-failure surfacing (#208, #209)" and
[`bug-fix-designs-round14-2026-08.md`](archive/bug-fix-designs-round14-2026-08.md) §2.
- **[RESOLVED] #208 — a saga-state-store failure aborted the run with zero rollback attempt, silently
  breaking `Saga`'s own documented "all-or-nothing" guarantee.** A state-store exception thrown right
  after a real, effect-producing stage completed (`ISagaStateStore.RecordStageCompletedAsync`, and the
  final `RecordFinishedAsync` call on an all-succeeded run) propagated raw out of `RunAsync`, so the
  registered `Compensate` for the completed stage(s) never ran. Fixed: `Saga.RunOnceAsync` now catches
  a store exception at both of those call sites, compensates every completed stage exactly as a step
  failure would (new `RollBackCompletedStagesAsync`/`HandleStateStoreFailureAsync` helpers), and
  returns `SagaOutcome.RolledBack` (or `PartiallyRolledBack` if a compensation itself also fails,
  populating the existing `CompensationFailures` list) with the store's exception attached via a new
  `SagaResult.StateStoreException` property — never a raw throw. The result is still (best-effort)
  persisted via one more `RecordFinishedAsync` call, swallowing a second store failure there so it
  cannot mask the already-computed rollback outcome. **Documented edge case (deliberate):** a failure
  from `ISagaStateStore.RecordStartedAsync` - before any stage has run - is left to propagate raw,
  since there is nothing yet to compensate; this is called out explicitly in the `Saga` class remarks
  and in the `RunOnceAsync` call site. A store failure on `RecordFinishedAsync` *after* a step-failure
  rollback has already run is likewise left as a swallowed best-effort persist (rollback already
  happened; nothing further to compensate), so a second store hiccup there can't discard the
  already-correct, already-computed result. New tests in `SagaRetryAndStateStoreTest`:
  `StateStore_ThrowsRightAfterARealStageCompletes_TriggersCompensation_InsteadOfRawThrow`,
  `StateStore_CompensationItselfFails_ReturnsPartiallyRolledBack`,
  `StateStore_ThrowsOnFinalFinish_AfterEveryStageSucceeded_TriggersFullRollback`, and (for the
  documented edge case) `StateStore_ThrowsOnRecordStarted_BeforeAnyStageRuns_PropagatesRaw`.
- **[RESOLVED] #209 — when two steps in the same stage failed concurrently, `SagaResult` surfaced only
  one of them via `Failure`/`FailureException`; the other had no representation anywhere on the public
  result.** Fixed: added `SagaResult.Failures` (`IReadOnlyList<SagaStepOutcome>`), populated with
  *every* failed step's outcome in the failing stage - the data was already there in
  `RollBackAsync`'s per-stage outcome list, just filtered down to `FirstOrDefault` before. `Failure`
  and `FailureException` are now convenience views over `Failures[0]`, kept for backward
  compatibility and documented as such, mirroring how `CompensationFailures` is already the full list
  on this same class. New test in `SagaTest`:
  `RunAsync_TwoStepsFailConcurrentlyInSameStage_SurfacesBothInFailures` (asserts `Failures.Count == 2`,
  both step identities/exceptions present, and that `Failure`/`FailureException` still match the first
  entry). Round-1's #15 concurrency fix (`SagaStep<T>`/`Stage` outcome now run-scoped, not stored on
  the shared instance) was re-run unchanged and remains green -
  `RunAsync_ManyConcurrentRunsOnOneBuiltSaga_NeverCrossContaminate` (300 concurrent runs, 0
  cross-contaminated) - this WP's `RunOnceAsync` changes added only local/parameter state, no new
  shared mutable state.
### Tracked findings rounds 12–14, WP-L — Autofac closed-generic routing (done)
Ruled in [`bug-fix-plan-rounds12-14-2026-08.md`](archive/bug-fix-plan-rounds12-14-2026-08.md) WP-L, from the
round-14 finding in [`bug-fix-designs-round14-2026-08.md`](archive/bug-fix-designs-round14-2026-08.md) §3. Read
the round-9 #82–#85 `[RESOLVED]` entries above (WP-Q) before touching this file again — these are the
same six methods those fixes touched, and their regression tests (32-way concurrent resolution,
`TryAdd*` idempotency, `CreateServiceResolverFactory()` repeat-call safety) must stay green.

- **[RESOLVED] #210 — `AutofacBenzeneServiceContainer` threw on a CLOSED generic `Type` where the
  Microsoft DI adapter succeeds, because the generic-routing check in six methods
  (`AddScoped(Type)`, `AddScoped(Type, Type)`, `AddTransient(Type)`, `AddTransient(Type, Type)`,
  `AddSingleton(Type)`, `AddSingleton(Type, Type)`) tested `Type.IsGenericType` — true for both an open
  generic definition (`typeof(Handler<>)`) and a closed generic (`typeof(Handler<Widget>)`) — instead of
  `Type.IsGenericTypeDefinition`, true only for the open definition. A closed generic `Type` failed the
  check into Autofac's `RegisterGeneric`, which requires an open generic type definition and throws
  `ArgumentException: The type ... is not an open generic type definition` on a closed one — so a
  discovered handler class that happened to be a closed generic (e.g. `Handler<SomeConcreteMessage>`
  rather than an open `Handler<>`) worked under `MicrosoftBenzeneServiceContainer` (which has no generic
  branching at all - it forwards every `Type` straight to `IServiceCollection`, which handles open and
  closed generics uniformly) and threw under Autofac.** Fixed: all six checks now test
  `IsGenericTypeDefinition`, routing a closed generic `Type` through the ordinary `RegisterType`/`As`
  path exactly like the Microsoft adapter, while an open generic definition still takes
  `RegisterGeneric`. New `AutofacClosedGenericRoutingTest` (alongside the existing
  `AutofacDIParityTest`/`AutofacDITest`): a red run against the pre-fix `IsGenericType` check reproduced
  the exact `ArgumentException` on all six methods (verified by reverting the fix locally and re-running
  the new test file — 6 failed, 3 passed); post-fix all 9 tests pass, including a Microsoft-adapter
  control test (`MicrosoftAdapter_AddScoped_ClosedGenericType_Succeeds`) resolving the identical closed
  generic type side-by-side, and an open-generic regression test
  (`AutofacAdapter_AddScoped_OpenGenericType_StillResolvesPerClosedRequest`) confirming the
  generic-definition path is untouched. Full re-run of `test/Benzene.Core.Test` (1413 tests, including
  every #82–#85 regression test and all pre-existing open-generic registrations exercised transitively
  by `AddMessageHandlers`): 1411 passed, 0 failed, 2 skipped. No capability-matrix change: the matrix
  carries no DI-container-adapter row describing generic-registration behavior, so there is no stale
  capability statement to correct — parity with the Microsoft adapter is exactly what the adapter is
  meant to provide and was never claimed otherwise.
### Tracked findings rounds 12-14, WP-N — S3 TestHelpers key encoding + ServiceBus client logger guard (#191, #192, done)
Ruling and rationale are in [`bug-fix-plan-rounds12-14-2026-08.md`](archive/bug-fix-plan-rounds12-14-2026-08.md) WP-N and
[`bug-fix-designs-round12-2026-08.md`](archive/bug-fix-designs-round12-2026-08.md) §3.
- **[RESOLVED] #191 — `Benzene.Aws.Lambda.S3.TestHelpers`'s `AsS3` builder produced a fake object key
  that was never URL-encoded, so the real `S3ObjectKeyCodec.Decode` step (added by #158's fix) silently
  corrupted any test-constructed key containing `+`, `%`, or other S3-reserved characters** — verified:
  `"invoice+2024-08-27.pdf"` through `AsS3` and the real production getters came back as
  `"invoice 2024-08-27.pdf"`. Fixed by adding `S3ObjectKeyCodec.Encode` — the exact inverse of
  `Decode` (`WebUtility.UrlEncode` alongside `Decode`'s `WebUtility.UrlDecode`) — and using it in
  `MessageBuilderExtensions.AsS3` to encode the caller's real (decoded) key before storing it on the
  fake record, so the codec pair round-trips by construction: `Decode(Encode(key)) == key` for any
  key, including one containing `+`, `%`, or non-ASCII characters. Regression tests in
  `S3TestHelpersTest.AsS3_ReservedOrUnicodeCharactersInKey_RoundTripThroughTheRealProductionGetters`
  (a `[Theory]` covering the review's exact `+` probe plus a `%`-containing key and a unicode key),
  asserting the round-trip through both the raw record's `Decode` and the real
  `S3MessageBodyGetter`/`S3MessageHeadersGetter`.
- **[RESOLVED] #192 (minor) — `ServiceBusBenzeneMessageClient`'s failure-handling catch block itself
  threw if constructed with a null logger (its own `LogError` call null-guards), and every other
  `*BenzeneMessageClient` in the codebase shared the same constructor shape.** Fixed by adding
  `ArgumentNullException.ThrowIfNull(logger)` as the first statement in every constructor that takes a
  required (non-optional, non-nullable) `ILogger`/`ILogger<T>` across the client family, so a null
  logger fails fast at construction instead of inside the catch block at the worst possible moment.
  `HttpBenzeneMessageClient` was deliberately left unchanged — its `ILogger? logger = null` parameter
  is optional by design and every use is already null-conditional (`_logger?.LogError(...)`).
  Classes touched (both constructor overloads on each, where two exist):
  `Benzene.Clients.Azure.ServiceBus.ServiceBusBenzeneMessageClient`,
  `Benzene.Clients.Aws.EventBridge.EventBridgeBenzeneMessageClient`,
  `Benzene.Clients.Aws.Lambda.AwsLambdaBenzeneMessageClient` (single constructor),
  `Benzene.Clients.Aws.Sns.SnsBenzeneMessageClient`, `Benzene.Clients.Aws.Sqs.SqsBenzeneMessageClient`,
  `Benzene.Clients.Azure.EventGrid.EventGridBenzeneMessageClient`,
  `Benzene.Clients.Azure.EventHub.EventHubBenzeneMessageClient`,
  `Benzene.Clients.Azure.QueueStorage.QueueStorageBenzeneMessageClient`,
  `Benzene.Grpc.Client.GrpcBenzeneMessageClient`, `Benzene.Kafka.Core.Kafka.KafkaBenzeneMessageClient`,
  `Benzene.RabbitMq.RabbitMqSendMessage.RabbitMqBenzeneMessageClient`. Regression tests: a
  `Constructor_NullLogger_ThrowsImmediately`/`Constructor_PrebuiltPipelineOverload_NullLogger_ThrowsImmediately`
  fact per class (new test files for ServiceBus/EventGrid/EventHub/QueueStorage/EventBridge; added to
  the existing Kafka/RabbitMq/Sns/Sqs/AwsLambda/Grpc client test files), plus a normal-construction,
  failing-send test using `FakeLogger`/`FakeLogCollector` (or the existing `RecordingLogger` for gRPC)
  confirming the catch block still logs the real exception and returns a failure result without
  throwing.
### Tracked findings round 12–14, WP-O — Mesh UI vendoring doc + upstream items (#204–#207)
Round 14's client-side review of `src/Benzene.Mesh.Ui` (`work/archive/bug-fix-designs-round14-2026-08.md`
§1) found that `mesh-ui.html`/`mesh-spec-ui.html` are not hand-written vanilla JS as
`src/Benzene.Mesh.Ui/CLAUDE.md` extensively described — they are a minified React + Redux Toolkit
build vendored verbatim from the external `benzene-ui` repo, kept honest by
`.github/workflows/mesh-ui-drift-check.yml` ("never hand-edit"). The doc's mismatch with reality was
itself the headline finding (#204); three further findings (#205–#207) are real client-side behavior
gaps inside that vendored bundle.
- **[RESOLVED] #204 — `src/Benzene.Mesh.Ui/CLAUDE.md` documented features/conventions absent from
  the real shipped bundle, and never mentioned the vendoring relationship at all.** An agent trusting
  the doc verbatim could plausibly hand-edit the generated `.html` files directly — exactly what the
  drift check exists to prevent. Fixed by rewriting the doc: a prominent vendoring notice now leads
  the file (never hand-edit; changes go upstream in `benzene-ui` then get re-vendored; the drift-check
  workflow enforces it), the large dated changelog of hand-written-vanilla-JS implementation history
  was removed rather than patched (it no longer describes the real bundle and there is no reliable way
  to tell which parts, if any, still applied), and the accurate server-side/deployment material — the
  `MeshUiPage`/`MeshUiMiddleware`/`MeshUiExtensions`/`MeshSpecUiPage`/`MeshSpecUiMiddleware` C# API
  surface, the opt-in-parameter rules, and the static-file-host deployment guidance, all verified
  against current source — was kept and, where the doc had fallen behind the actual method signature
  (the undocumented `environment` parameter), corrected. No code changed; the vendored `.html` files
  were not touched.
- **[UPSTREAM] #205 — the Refresh control has no confirmation step, despite the package's own doc
  calling it "real money per click" (fans out to every service in the mesh on every press).** The
  sibling Test Console Send action requires an explicit checkbox before submitting; Refresh does not.
  This is inside the vendored `benzene-ui` bundle, so it cannot be fixed by editing the `.html` files
  in this repo — needs a fix in `benzene-ui` (add an equivalent confirmation gate to Refresh) followed
  by a re-vendor of `mesh-ui.html` here. Documented in `src/Benzene.Mesh.Ui/CLAUDE.md`'s new "Known
  upstream items" section. Out of scope for this repo's fix rounds.
- **[UPSTREAM] #206 (minor) — the Sign-out control has no pending/disabled state**, unlike Refresh
  and Send, so a rapid double-click can fire two concurrent logout requests. Same disposition as
  #205: needs a `benzene-ui` fix (disable/pending-state Sign-out while its request is in flight) plus
  a re-vendor. Documented in `src/Benzene.Mesh.Ui/CLAUDE.md`. Out of scope for this repo's fix rounds.
- **[UPSTREAM] #207 (minor) — Sign-out's `fetch()` doesn't pass `credentials: "same-origin"`
  explicitly**, unlike the other two write-action helpers (Refresh, Send). Not an active bug (the
  browser default is same-origin) but an inconsistency worth normalizing upstream for consistency and
  to avoid the omission reading as an oversight. Same disposition as #205/#206: needs a `benzene-ui`
  fix plus a re-vendor. Documented in `src/Benzene.Mesh.Ui/CLAUDE.md`. Out of scope for this repo's
  fix rounds.
### Tracked findings rounds 12/14, WP-P — Examples sweep (task board #193–#196, #214–#223, done)
Ruled in [`bug-fix-plan-rounds12-14-2026-08.md`](archive/bug-fix-plan-rounds12-14-2026-08.md) WP-P,
covering round-12 §4 and round-14 §4 (both archived). All fourteen tasks are independent,
example-local fixes. Verified per-project rather than via one whole-`Benzene.Examples.sln` build
(the review host was under extreme, unrelated contention from concurrent sessions for the whole
session — load average 100–165 — making a several-hundred-project solution build impractically
slow, up to 38 minutes wall-clock for a single example's full dependency chain even though CPU time
consumed was seconds): `examples/Cqrs/Benzene.Example.Cqrs.csproj` (a newly-added `Benzene.Examples.sln`
member, pulling in the also-newly-added `src/Benzene.Outbox`) built clean (0 warnings, 0 errors);
`examples/K8sTransports/App/Benzene.Examples.K8sTransports.App.csproj` (the other newly-added member,
pulling in `Domain`) built clean (0 errors, 17 pre-existing warnings unrelated to this WP, all in
`Benzene.Kafka.Core`/`Benzene.Aws.Sqs`); `examples/Asp/Benzene.Example.Asp.csproj` built clean and its
6 integration tests passed; `examples/GoogleCloudMesh/Benzene.Examples.GoogleCloudMesh.sln` built
clean. The Cloudflare worker's `npm install` + `npx wrangler deploy --dry-run` both pass with no
deprecated-config warning.

- **[RESOLVED] #214 — `examples/GoogleCloudMesh/Mesh/Startup.cs:48` called
  `MeshServiceRegistry.FromEnvironment()`, which doesn't exist** (a genuine build error,
  contradicting the README's "the whole solution builds" claim). Fixed: corrected to
  `MeshRegistry.FromEnvironment()` — the example's own static registry-builder class
  (`Mesh/MeshRegistry.cs`). `dotnet build examples/GoogleCloudMesh/Benzene.Examples.GoogleCloudMesh.sln`
  now succeeds (0 errors).
- **[RESOLVED] #193 + #215 — `examples/Cqrs`, `examples/K8sTransports`, and `examples/Outbox` were not
  members of `Benzene.Examples.sln`**, so the documented build gate silently skipped all three. Added
  all three (and their sub-projects — `K8sTransports` has `App`+`Domain`) as solution items, nested
  under matching solution folders; `Cqrs`'s `ProjectReference` to `src/Benzene.Outbox` turned out to be
  missing from the solution entirely too (a `src/` gap, not an `examples/` one), so it was added
  alongside, nested under the existing `src` solution folder to match convention. `Cqrs` and
  `K8sTransports/App` were each confirmed building clean as standalone projects (see this section's
  intro); `Outbox`'s own `ProjectReference`s all point at `src/` projects already present in the
  solution before this change (no further gaps like `Cqrs`'s), so it needs no additional fix — its
  standalone build was still in flight, under the same host contention, when this entry was written.
- **[RESOLVED] #216 — `examples/GoogleCloudMesh` was entirely undocumented in `examples/CLAUDE.md`**,
  unlike every sibling mesh example. Added a `GoogleCloudMesh/` bullet to the Layout section (topology,
  the two-functions-per-service Gen2 split, static discovery, GCS-backed catalog, its own `.sln` and
  the fact that it is *not* a member of the root `Benzene.Examples.sln`), and noted the same in the
  "How these build" per-folder-solution list.
- **[RESOLVED] #194 — the Cloudflare worker's `@cloudflare/containers@^0.0.15` was the one npm-deprecated
  version in the package's whole history** ("bundling is wrong, please use 0.0.16" per its own npm
  deprecation notice), and `npx wrangler deploy --dry-run` failed local bundling with "Could not
  resolve `@cloudflare/containers`" (the 0.0.15 tarball ships no `dist/`). Bumped to `^0.3.7` (current
  latest at review time, no deprecation notice, same `Container`/`getContainer`/`defaultPort` API the
  Worker and the docs' worked example already use). `npm install` now succeeds (0 vulnerabilities) and
  `wrangler deploy --dry-run` gets past local bundling ("Total Upload: ...") with no
  `@cloudflare/containers` resolution error; the dry-run then stops at "The Docker CLI is needed to
  build the configured image ... but could not be launched" — an environment limitation (no Docker
  daemon in this sandbox, same limitation round-14 already noted for the Kafka finding), not a defect
  in the fix, since it's past the exact failure point (local bundling) the review asked to confirm.
- **[RESOLVED] #195 — `worker/wrangler.toml`'s `[containers.configuration]` / `instance_type` block is
  a deprecated config shape current wrangler flags**, and diverges from `docs/getting-started-cloudflare.md`'s
  own worked example, which has no such block. Removed the block so the example matches its own
  documented source of truth; `wrangler deploy --dry-run` no longer emits the deprecated-shape warning.
- **[RESOLVED] #196 — `examples/K8sTransports/Domain/PlaceOrderMessageHandler.cs:23-25`'s doc comment
  pointed readers at `App/HttpStartup.cs`/`App/WorkerStartup.cs` for "how one process hosts all
  three"** — neither file exists (only `App/Startup.cs`, where that explanation actually lives).
  Corrected the reference.
- **[RESOLVED] #217 — `examples/Kafka/docker-compose.yaml` pinned `confluentinc/cp-kafka:latest` (and
  `cp-zookeeper:latest`) in a ZooKeeper topology**, and `latest` currently tracks a Confluent Platform
  line that dropped ZooKeeper support. Pinned both images to `7.4.4`, the exact tag the example's own
  test-harness compose file (`Benzene.Examples.Kafka.Test/docker-compose.yaml`) already uses as its
  last-ZK-compatible precedent.
- **[RESOLVED] #218 + #222 — `examples/Asp/Benzene.Example.Asp/Startup.cs:52` hardcoded an Application
  Insights instrumentation key in source, and `config.json` shipped a dummy DB connection string with a
  plaintext placeholder password that nothing reads** (confirmed by grep across the whole repo before
  removal — only `Program.cs`'s non-optional `AddJsonFile("config.json")` load references the file
  itself; no code anywhere reads `DB_CONNECTION_STRING`). Fixed: the AI key is now read from
  configuration (`APPINSIGHTS_INSTRUMENTATIONKEY`, defaulting to empty — telemetry simply sends nowhere
  until a real key is configured), with a comment explaining where to put a real one; `config.json`'s
  dummy connection string was deleted outright (left as `{}` so `Program.cs`'s non-optional
  `AddJsonFile` call still finds a file).
- **[RESOLVED] #219 — the demo JWT issuer's `Issuer`/`JwksUri` (`examples/Asp`) were hardcoded to
  `http://localhost:5000/` with no hint on failure if the app runs on a different port** (verified: an
  opaque 401 on every `/protected/*` request). `DemoJwtIssuer.Issuer` is now an instance property read
  from configuration (`DEMO_AUTH_ISSUER`, defaulting to `http://localhost:5000/` to preserve current
  behavior), with `JwksUri` derived from it; `Startup.cs` resolves the singleton instance via DI instead
  of the old `static const`. Added a doc comment on `DemoJwtIssuer.Issuer` (and a pointer from
  `Startup.cs`) spelling out the opaque-401 symptom and its cause.
- **[RESOLVED] #220 — `examples/App/Benzene.Examples.App.Data` was an orphaned project** (stale
  pre-split namespace `Benzene.Examples.Aws.Data`, EF Core/Npgsql 7.0.3, out of support since 2024).
  Verified zero references repo-wide before deleting: every `using Benzene.Examples.App.Data;` in the
  tree resolves to the *namespace* of the same name declared inside the live `Benzene.Examples.App`
  project's own `Data/` folder — a same-named but unrelated namespace — and grepping for the orphaned
  project's actual namespace (`Benzene.Examples.Aws.Data`) or its `.csproj` path found only the
  project's own files and its `Benzene.Examples.sln` entry, no `ProjectReference` anywhere. Deleted the
  project directory, removed its solution entry, and removed the stale mention from `examples/CLAUDE.md`.
- **[RESOLVED] #221 — a CS8632 nullable-annotation warning in
  `GoogleCloudMesh/Shared/MeshServiceWiring.cs`** (a `?` on a parameter type in a project with
  `<Nullable>disable</Nullable>`, copied from the nullable-enabled `AzureFunctionsMesh` sibling whose
  `MeshServiceWiring.cs` uses the identical `Action<...>? configureBenzene = null` shape correctly).
  Added `#nullable enable` at the top of the file (scoped to this one file rather than flipping the
  whole project) — the warning is gone and no new nullable warnings were introduced.
- **[RESOLVED] #223 — `examples/Asp/Benzene.Example.Asp/Startup.cs` emitted ASP0001 ("The call to
  UseAuthorization should appear between app.UseRouting() and app.UseEndpoints(..)")**, copied
  verbatim into every adopter's project since this file is used as a template. Root cause: the
  `app.Map("/protected", ...)`/`app.Map("/slow", ...)` branches each called their own `protectedApp`/
  `slowApp`-scoped `UseRouting()` + an empty `UseEndpoints(endpoints => { })` — dead weight, since
  neither branch ever maps an ASP.NET Core endpoint (both gate access and dispatch entirely through
  Benzene's own `UseBenzene(...)`/`UseOAuth2Bearer`/`RequireScope`, mirroring the main pipeline's
  `UseBenzene(...)` a few lines up, which has no such wrapper of its own) — but their mere presence
  was enough to make the analyzer misjudge the real, correctly-ordered top-level
  `UseRouting()`/`UseAuthorization()`/`UseEndpoints(MapControllers)` triplet as out of order (confirmed
  empirically: relocating just the `UseAuthorization()` call, leaving the extra pairs in place, did
  not clear the warning — it kept firing wherever `UseAuthorization()` was moved to, until the two dead
  pairs were removed). Fixed by deleting both branches' redundant `UseRouting()`/`UseEndpoints()` calls
  and leaving `UseAuthorization()` in its original, natural position right after the top-level
  `UseRouting()`. No behavior change: `WeatherForecastController` (the only thing ever dispatched
  through the real `UseEndpoints`) is unauthenticated either way, and the `/protected`/`/slow` branches
  never touched ASP.NET Core's own authorization/endpoint-routing machinery to begin with. Verified:
  the ASP0001 warning is gone from a clean rebuild of `Benzene.Example.Asp.csproj` (0 errors, only the
  pre-existing, unrelated `NU1510` package-pruning warning remains).

## Round 15 + rounds 12–14: two integration bugs found only by the post-merge baseline (2026-08-30)

Both of the above rounds' 16 work packages built and tested clean in isolated worktrees; the two
issues below only existed at the intersection of two independently-correct work packages, and so
were invisible until everything landed on `main` together. Caught by the first full, uncontended
`dotnet build` + `dotnet test` run against fully-merged `main` — not by any individual work package's
own (correct, at the time) verification. Recorded here as a reminder that a fix round isn't done at
the last merge commit; it's done after a real centralized baseline run.

- **[RESOLVED] `S3TestHelpersTest.AsS3_ReservedOrUnicodeCharactersInKey_RoundTripThroughTheRealProductionGetters`
  (3 theory cases) failed post-merge.** Root cause: WP-N's test (`#191`) was written against a pipeline
  with no terminal result-setting middleware, before WP-B's `#229` fix existed; `#229` changed
  SNS/S3/EventBridge to escalate (throw) on an unset `MessageResult` instead of silently treating it as
  accepted. Individually each change was correct; merged together, WP-N's test now hit WP-B's new
  escalation path and failed. Fixed by adding `configure: options => options.RaiseOnFailureStatus =
  false` to the test's `app.UseS3(...)` call, matching the sibling test immediately above it
  (`AsS3_BuildsAnEventThatRoutesThroughTheS3Pipeline`), which already opts out of that escalation for
  the same reason (the test is about key round-tripping, not message routing, so the inline middleware
  deliberately never sets a `MessageResult`). `test/Benzene.Core.Test/Aws/S3/S3TestHelpersTest.cs`.
- **[RESOLVED] `RateLimitingPipelineTest`'s two `#200`-era tests failed post-merge.**
  `InternallyOwnedRateLimiterHolder<TContext>` (WP-J's new class backing `#200`'s per-pipeline-not-
  per-container guard) implemented only `IAsyncDisposable`. The Microsoft DI adapter's synchronous
  container disposal (`ServiceProviderEngineScope.Dispose()`) throws `InvalidOperationException` when
  it needs to dispose a resolved singleton that implements only `IAsyncDisposable` — the same defect
  class as the already-fixed `#85` (`AutofacServiceResolverFactory` missing `IAsyncDisposable`), but in
  the opposite direction. Fixed by also implementing `IDisposable` on the holder, bridging to its
  existing `DisposeAsync()` the same way `MeshAnnouncer.Dispose()` already does
  (`DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5))`, best-effort on `AggregateException`).
  Separately, `SiblingPipelines_OffOneSharedContainer_EachGetTheirOwnIndependentInternallyOwnedLimiter`
  (also new in WP-J) never added a terminal middleware to its second pipeline, so a successful
  (non-rejected) rate-limit acquire never wrote a result via `IMessageHandlerResultSetter` — the
  rate-limiting middleware itself only writes a result on rejection — leaving an NRE on the
  success-path assertion; fixed by adding a terminal `.Use((resolver, context, next) => ...)` that
  records `BenzeneResult.Ok()`. `src/Benzene.RateLimiting/Extensions.cs`,
  `test/Benzene.Core.Test/Plugins/RateLimiting/RateLimitingPipelineTest.cs`.

Full baseline re-verified after both fixes: `Benzene.Core.Test` 3296 passed / 2 skipped / 0 failed,
`Benzene.Mesh.Test` 575 passed, `Benzene.Mesh.Host.Test` 150 passed, `Benzene.Examples.sln` build 0
errors. Pushed to `main` (`28473b0`).

## Round 16 fixes (2026-08-30)

- **[RESOLVED] #252 — `JaegerTraceSource`/`TempoTraceSource`'s per-service/per-trace isolation catch
  didn't survive an `HttpClient`-level timeout that wasn't the caller's own token; `XRayTraceSource`'s
  complementary bare catch swallowed genuine caller cancellation.**
  `src/Benzene.Mesh.Fleet.Jaeger/JaegerTraceSource.cs`, `src/Benzene.Mesh.Fleet.Tempo/TempoTraceSource.cs`
  isolated a per-service/per-trace fetch with `catch (Exception ex) when (ex is not
  OperationCanceledException)` — distinguishing "backend failed" from "host cancelled" purely by
  exception *type*. `HttpClient.Timeout` on one slow backend throws `TaskCanceledException` (an
  `OperationCanceledException` subclass) even when the caller's own token was never cancelled, so that
  exception escaped the isolation catch, faulted the whole `BoundedFanOut.WhenAllAsync` fan-out, and
  discarded every other service's already-fetched results — the #189 regression class reintroduced for
  one exception family. Separately, `src/Benzene.Mesh.Fleet.Aws.XRay/XRayTraceSource.cs`'s
  `EnrichRecentFlowsAsync`/`FetchBatchAsync` had a bare `catch { }` with the inverse problem — it
  swallowed genuine caller cancellation too, silently degrading a cancelled recent-flows enrichment to
  the summary plane instead of propagating.

  Fixed by replacing the type-based filters in Jaeger/Tempo with the token-verified form `catch
  (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)`
  — isolating everything EXCEPT an `OperationCanceledException` while the caller's own token is actually
  cancelled, matching `MessageHandler.cs`'s existing `ex.CancellationToken.IsCancellationRequested`
  precedent for the same timeout-vs-cancellation distinction. `XRayTraceSource.FetchBatchAsync` gained
  the complementary rethrow (`catch (Exception ex) when (ex is OperationCanceledException &&
  cancellationToken.IsCancellationRequested) { throw; }`, ahead of the existing bare `catch` that keeps
  degrading everything else).

  Verified with permanent regression tests in `test/Benzene.Mesh.Test/JaegerTraceSourceTest.cs` and
  `TempoTraceSourceTest.cs`: `GetCorrelationAsync_IsolatesAPerServiceTimeout_NotTiedToTheCallersToken`
  / `..._IsolatesAPerTraceTimeout_NotTiedToTheCallersToken` (a `TaskCanceledException` simulating an
  `HttpClient.Timeout` on one service/trace with the caller's own token uncancelled → before the fix
  the exception propagated and the healthy service's/trace's result was lost too; after, it survives,
  mirroring the adjacent `HttpRequestException` isolation test) and the complementary
  `GetCorrelationAsync_PropagatesGenuineCancellation_WhenTheCallersOwnTokenIsCancelled` (the ambient
  token is actually cancelled from within the failing service's/trace's handler → `OperationCanceledException`
  correctly propagates, both before and after the fix — confirming the fix doesn't newly break genuine
  cancellation). `test/Benzene.Mesh.Test/XRayTraceSourceTest.cs` gained
  `GetRecentFlowsAsync_PropagatesGenuineCancellation_InsteadOfDegradingToSummaryPlane` (a mocked
  `BatchGetTracesAsync` throwing `OperationCanceledException` while the caller's token is cancelled →
  before the fix no exception surfaced and the row silently degraded; after, it propagates). Full
  `Benzene.Mesh.Test` run: 582 passed / 0 failed.
## Round 16, WP-C: Azure trigger family — infra escalation, ambient cancellation, CosmosDb generator validation (2026-08-30)

- **[RESOLVED] #257 (high) — `AzureFunctionBatchApplicationBase.ProcessItemAsync` and
  `TimerTickApplication.HandleAsync` silently swallowed an infrastructure/DI-wiring failure under
  `CatchExceptions = true`.** The exact `#228` defect (fixed for AWS SNS/S3/EventBridge's
  `SingleContextEscalatingApplicationBase.ProcessAsync`), unfixed for every Azure Functions batch
  trigger sharing the base class (ServiceBus, EventHub, Kafka, QueueStorage, EventGrid) and
  independently for the Timer trigger: a `BenzeneResolutionException` (or anything with one in its
  `InnerException` chain — `BenzeneFailure.IsInfrastructure`) was logged with a differentiated message
  but never rethrown, so a mis-wired deploy silently dropped messages (the host checkpoints on a
  "successful" invocation) or, for ServiceBus `AckMode = Explicit`, looped abandon/redeliver forever
  while the service reported healthy. Fixed by mirroring
  `SingleContextEscalatingApplicationBase.ProcessAsync` exactly in both files: compute
  `isInfrastructure` once, keep the existing differentiated log line, then `if (isInfrastructure)
  throw;`. Composes cleanly with ServiceBus Explicit-ack's `OnExceptionCaughtAsync` abandon hook, which
  still runs first — the message is abandoned AND the invocation now fails loudly.
  `src/Benzene.Azure.Function.Core/AzureFunctionBatchApplicationBase.cs`,
  `src/Benzene.Azure.Function.Timer/TimerApplication.cs`.
- **[RESOLVED] #258 (minor) — the same catch also silently absorbed a genuine ambient-cancellation
  `OperationCanceledException`, inconsistent with #230's own still-queued-item behavior.** An
  already-*running* batch item (past `BoundedFanOut`'s semaphore) that observed the same ambient
  cancellation the Functions host signaled for the invocation was caught by the unqualified `catch
  (Exception ex) when (_catchExceptions)` and logged/swallowed like an ordinary business exception —
  while a sibling item still *queued* at that exact moment correctly aborted the whole invocation per
  `#230`, regardless of `CatchExceptions`. Two items hit by the identical host-cancellation event got
  opposite severity purely by scheduling luck. Fixed in `AzureFunctionBatchApplicationBase
  .ProcessItemAsync` only (Timer never reaches this scenario — a single tick never calls
  `BoundedFanOut`): after the infrastructure check, also rethrow when `ex is OperationCanceledException
  && cancellationToken.IsCancellationRequested` — the token-verified form (checked against this call's
  own ambient token, matching `MessageHandler.cs`'s existing pattern), not a bare type-based exclusion,
  so an application-produced OCE unrelated to host shutdown is not over-escalated. Regression tests:
  `EventGridFailureHandlingTest.HandleAsync_CatchExceptionsTrue_AmbientCancellation_EscapesContainmentAndRethrows`
  plus the negative `HandleAsync_CatchExceptionsTrue_UnrelatedCancellation_StaysContained`.
- **[RESOLVED] #259 — the Azure Functions source generator never validated `BenzeneCosmosDbTrigger`'s
  `DatabaseName`/`ContainerName`, unlike every sibling transport's destination field.** `#39`
  (round "WP-C") gave every other non-HTTP transport reader a required-field check for the one
  attribute value without which the binding is meaningless (`BENZ0003`-`BENZ0007`), but Cosmos DB's own
  binding-destination fields were read with an empty-string default and never checked — a declaration
  with `DocumentType` set but no `DatabaseName`/`ContainerName` compiled clean and emitted
  `CosmosDBTrigger(databaseName: "", containerName: "", ...)`, failing only at Azure host
  startup/deployment. Fixed by adding a new diagnostic, `CosmosDbTriggerMissingDestination`
  (`BENZ0010`, the next free id — verified against `DiagnosticDescriptors.cs`, which topped out at
  `BENZ0009`), reported when `database.Length == 0 || container.Length == 0`, following
  `ServiceBusTriggerMissingDestination`'s exact shape (report + emit nothing) — checked alongside, not
  instead of, the existing `DocumentType`/`BENZ0002` check (which still runs first and returns early,
  so a `DocumentType`-only omission still reports only `BENZ0002`).
  `src/Benzene.Azure.Function.SourceGenerators/DiagnosticDescriptors.cs`,
  `src/Benzene.Azure.Function.SourceGenerators/Transports/MessagingTransports.cs`.

Red-green recipes reproduced verbatim from `work/review-round16-azure-2026-08.md` and kept as permanent
regression tests: `test/Benzene.Core.Test/Azure/TimerFailureHandlingTest.cs`,
`test/Benzene.Core.Test/Azure/EventGridFailureHandlingTest.cs`,
`test/Benzene.Core.Test/Autogen/AzureFunctions/AzureFunctionTriggerGeneratorTest.cs`. Verified:
`dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Azure|FullyQualifiedName~AzureFunctionTriggerGenerator"` — 281 passed, 0 failed
(including the pre-existing `#230`/`#231` regression tests, unaffected).
## Round 16 WP-E: mesh collector/query (2026-08-30)

- **[RESOLVED] #250 — the five `mesh:query:*` handlers never resolved `ICancellationTokenAccessor`,
  so `UseTimeout(...)` around the query envelope was inert.** `FleetQueryMessageHandler`,
  `ServiceQueryMessageHandler`, `TopicQueryMessageHandler`, `TraceQueryMessageHandler`, and
  `CorrelationQueryMessageHandler` (`src/Benzene.Mesh.Collector/Handlers.cs`) took only an
  `IMeshFleetReadModel` and always called it with the default (never-cancelled) token, even though
  `IMeshFleetReadModel` and every downstream trace source (X-Ray/Jaeger/Tempo, including the
  #230-fixed `BoundedFanOut`) were built to honor one. Fixed by giving all five the same optional
  `ICancellationTokenAccessor? cancellation = null` constructor parameter `MeshDispatchMessageHandler`
  already has (#185), resolved at the point of use (`_cancellation?.CancellationToken ??
  CancellationToken.None`) and threaded into the read-model call. Purely additive (an optional
  trailing constructor parameter); no DI registration change needed — the existing message-handler
  resolution already supplies an unregistered optional collaborator as `null`, exactly as it does for
  `MeshDispatchMessageHandler` today. `test/Benzene.Mesh.Test/MeshCollectorQueryCancellationTest.cs`
  (inverted from the round-16 review's committed red evidence test to its green form, covering
  Fleet + Trace; the other three handlers are mechanical clones of the same one-line fix).
- **[RESOLVED] #251 — `MeshCollectorStore` had no catalog slot for more than one live
  `(service, serviceVersion)`, so a side-by-side canary/blue-green deployment was reported as
  contract drift.** Spec §2.4 requires the catalog key to be the pair `(service, serviceVersion)`:
  two different versions reporting different `descriptorHash`es is the expected side-by-side
  deployment state, not drift. `MeshCollectorStore._services` (`ServiceState`) held exactly one
  `Descriptor`, wholesale-replaced on every `Register` call regardless of version — so a canary's
  second version silently evicted the first's topics/produces/hash from the catalog, and every still-
  healthy instance of the evicted version was then compared against the survivor's descriptor and
  flagged as a false hash mismatch. Fixed: `ServiceState` now keys every live descriptor by
  `ServiceVersion ?? ""` (`Descriptors`, a `Dictionary<string, MeshServiceDescriptor>`); `Register`
  retracts only the specific version's OWN previously declared provider/consumer edges before
  re-adding its new ones (`RetractEdges`) — a re-registration of the SAME `(service, version)` pair
  still replaces wholesale (unchanged behavior), but a DIFFERENT version registering never touches a
  still-live sibling version's edges. `InstanceView.HashMatches` is now computed
  (`MeshCollectorStore.HashMatches`) against EVERY currently registered version's hash for the
  service, not just the single "current" row, so an instance matches whichever live version it's
  actually running, and only a hash matching none of the service's live descriptors reads as drift
  (including the case where the SAME version re-registers with a genuinely different hash — a real
  contract drift without a version bump — which still correctly flags, because the old hash is no
  longer present anywhere in the live set). **[RESOLVED] view-shape choice:**
  `MeshCollectorStore.Service(name)`/`Fleet().Services` still return exactly ONE row per service
  NAME — the most-recently-registered version's scalar Runtime/Binding/Placement/Topics/
  ServiceVersion/Descriptor fields — preserving today's shape for every existing caller/test keying
  by name alone (in particular `Reregistration_ReplacesServiceVersion_WithTheLatestDescriptors`,
  which asserts exactly one `Fleet().Services` row named "orders" after two versions register). Both
  live versions' full descriptors remain available underneath that one row for the topic catalog
  (`mesh:query:topic`, queried by topic id, naturally reports both versions' declared edges without
  needing a second service-level row) and for per-instance hash comparison. A dedicated per-version
  breakdown on `ServiceView` itself (e.g. a `Versions` list) was NOT added this round — not required
  by any of the three behaviors spec §2.4 actually mandates, and speculative until a caller needs to
  render "which versions are live" as its own list; a natural follow-up if the mesh UI wants it.
  `src/Benzene.Mesh.Collector/MeshCollectorStore.cs`.
  `test/Benzene.Mesh.Test/MeshCollectorSideBySideVersionTest.cs` (inverted from the round-16 review's
  committed red evidence test: both catalog entries now stay live, each instance hash-matches its own
  version, and a new drift-positive case proves a genuine same-version hash change still flags).
  Every pre-existing `MeshCollectorStoreTest`/conformance fixture case stayed green unmodified.
- **[RESOLVED] #253 — `MeshCollectorStore.AddEvents`/`AddIssues` threw `NullReferenceException` on a
  null ELEMENT inside a non-null list, partially corrupting a batch's ingestion.** #234 (round 15)
  fixed a null WHOLE list (`"events": null`); `MeshTraceEvent`/`MeshIssue` are reference types, so a
  wire payload can also legally deserialize `"events": [null, {...}, {...}]` — a non-null list
  containing a null element — which the loop dereferenced unconditionally, throwing mid-batch:
  everything before the null had already mutated state, everything after was silently dropped, and
  the caller never got its `Ack`. Fixed by skipping a null element in both loops, matching the file's
  existing null-tolerance conventions (the null-`Status`-field guard immediately above `AddEvents`'
  loop is the model). `AddEvents`'s returned `Accepted` count now reflects only elements actually
  processed (a skipped null is not counted), matching `AddIssues`' pre-existing convention of only
  counting entries it actually stored, not the raw batch length.
  `src/Benzene.Mesh.Collector/MeshCollectorStore.cs`.
  `test/Benzene.Mesh.Test/MeshCollectorStoreTest.cs`
  (`AddEvents_NullElementInEventsList_DoesNotThrow_AndAppliesTheOtherEvents`,
  `AddIssues_NullElementInIssuesList_DoesNotThrow_AndAppliesTheOtherIssues`).
- **[RESOLVED] #256 — `CompositeMeshFleetReadModel.TraceAsync`/`CorrelationAsync`'s bare
  `catch { return null; }` swallowed a genuine caller cancellation and misreported it as an
  authoritative "not found".** The bare catch exists for fetch isolation (a failing trace-source
  backend degrades a single lookup to "not found" rather than throwing out of the composite), which
  is correct for a real backend failure — but it also caught an `OperationCanceledException` raised
  because the CALLER'S OWN `cancellationToken` (e.g. a `mesh:query:trace`/`correlation` request
  wrapped in `UseTimeout(...)`, or a disconnected HTTP client) was cancelled, silently reporting
  "not found" instead of propagating the cancellation — a caller/UI couldn't distinguish a cancelled
  request from an authoritative "that trace doesn't exist". Fixed by narrowing the catch filter:
  rethrow when `ex is OperationCanceledException && cancellationToken.IsCancellationRequested` (the
  method's OWN token, token-verified, matching `MessageHandler.cs`'s existing pattern rather than a
  bare exception-type exclusion), keep swallowing everything else exactly as before.
  `src/Benzene.Mesh.Collector/CompositeMeshFleetReadModel.cs`.
  `test/Benzene.Mesh.Test/CompositeMeshFleetReadModelTest.cs`
  (`TraceAsync_PropagatesRealCancellation_InsteadOfReportingNotFound`,
  `CorrelationAsync_PropagatesRealCancellation_InsteadOfReportingNotFound`, plus the negative
  `TraceAsync_PlainException_StillDegradesToNull_WhenTheCallersTokenIsNotCancelled`). Out of scope
  this round (unchanged, noted for a future pass): `RecentFlowsAsync`/`TopicsFromUsageAsync`'s own
  bare catches share the same class of gap, as does `XRayTraceSource.FetchBatchAsync`'s bare
  `catch { }` — neither was in WP-E's task list (#252/XRay is WP-F's scope).

Full `dotnet test test/Benzene.Mesh.Test -c Release`: 584 passed / 0 failed (the whole project, not a
filtered subset, per the WP's own instruction given #251's behavioral surface).
`dotnet test test/Benzene.Conformance.Test -c Release --filter FullyQualifiedName~Mesh`: 84 passed / 0
failed.
- **[RESOLVED] (2026-08-30) #263 — `OpenApiSchemaCSharpTypeBuilder` interpolated
  `Discriminator.PropertyName` and every `mapping.Key` unescaped into generated
  `[JsonPolymorphic]`/`[JsonDerivedType]` C# string literals** — the fourth instance of the
  unescaped-interpolation-into-structured-output bug class this round-family (YAML #212, Markdown
  #86, HCL #244). A discriminator value containing a `"` (e.g. `12" wheel`, a realistic
  size/dimension-flavoured mapping value reachable via `SuppliedSchemaCatalog` or any hand-built
  `EventServiceDocument`, not only reflection-derived schemas) produced 7 cascading Roslyn errors
  while the CLI's `build` command reported success. Fixed by adding a small local
  `EscapeCSharpString` helper (backslash, double-quote, `\n`/`\r`/`\t`/`\0`, and every other control
  character via `\uXXXX`) mirroring the shape of `YamlValueEscaping`/`NameFormatter.EscapeHclString`
  elsewhere in this codebase — deliberately NOT a new `Microsoft.CodeAnalysis.CSharp` (Roslyn)
  dependency in `Benzene.CodeGen.Client`, which doesn't otherwise need it (Roslyn is only a
  transitive dependency of the test project). Applied to `PropertyName` and every `mapping.Key`.
  Test: `GeneratedClient_WithAdversarialDiscriminatorMappingKeyContainingAQuote_Compiles` (new
  theory case in `CodegenOutputCompilesTest.cs`, using the same Roslyn-compile oracle as the
  existing #66/#67/#240 regression tests). `src/Benzene.CodeGen.Client/OpenApiSchemaCSharpTypeBuilder.cs`.
- **[RESOLVED] (2026-08-30) #264 — `JsonOpenApiSchemaBuilder.Create`'s switch had no case for
  `JTokenType.Float` or `JTokenType.Null`**, throwing `Exception("No map for Float"/"No map for
  Null")` and aborting the whole document on an ordinary JSON decimal number (e.g. a price,
  percentage, or rating) or an ordinary JSON `null` (extremely common for an optional field in a
  captured real-world example) — reachable from the public documented API
  `EventServiceDocumentBuilder.AddJsonEvent`. Same crash-on-legitimate-input shape as
  #241/#242/#243. Fixed by adding `JTokenType.Float => CreateNumberSchema()` (mirroring
  `CreateIntegerSchema`, `Type = "number"`, `Format = "double"`) and a `JTokenType.Null` branch
  (`CreateNullPlaceholderSchema`) returning the exact untyped/`Nullable = true` placeholder
  convention `CreateArraySchema` already established for "nothing in the example to infer from"
  after the #242 fix — no `type` keyword, so it matches anything, rather than inventing a new
  placeholder shape. Tests: `CreateSchema_FloatExampleValue_DoesNotThrow_AndEmitsANumberSchema`,
  `CreateSchema_NullExampleValue_DoesNotThrow_AndEmitsAnUntypedNullablePlaceholder`
  (`JsonOpenApiSchemaBuilderTest.cs`). `src/Benzene.Schema.OpenApi/JsonOpenApiSchemaBuilder.cs`.
- **[RESOLVED] (2026-08-30) #265 (minor) — `MarkdownTypeBuilder.MapProperty`'s empty-object `else`
  branch (and the matching array-of-object branch) wrote a bare, unlabelled `"{}"`/`"{}[]"` with the
  property NAME dropped entirely** — the normal shape for a `Dictionary<string, T>`-typed property
  (`type: object` with `additionalProperties` but no own declared `properties`) rendered as an
  anonymous line a reader couldn't attribute to any field. Fixed both `else` branches to always emit
  `{CodeGenHelpers.Camelcase(name)}: ` before the placeholder, and — where `AdditionalProperties !=
  null` — render the map shape via a new `GetMapOrEmptyObjectPlaceholder` helper
  (`{[string]: <valueType>}`, resolving `<valueType>` through the same `GetPropertyTypeName`
  recursion this file already uses elsewhere, e.g. `scores: {[string]: int}`), mirroring
  `CSharpTypeName.GetName`'s `Dictionary<string, T>` handling for the C# generator (see its comment
  at `OpenApiSchemaCSharpTypeBuilder.cs:176-183`). A genuinely empty object (no `additionalProperties`
  either) still renders as `{name}: {}` — now at least attributable to its field. Tests:
  `MapProperty_AdditionalPropertiesMap_RendersTheNamedMapShape_NotABareAnonymousBraces`,
  `MapProperty_ArrayOfAdditionalPropertiesMaps_RendersTheNamedMapShape`
  (`MarkdownTypeBuilderTest.cs`). `src/Benzene.CodeGen.Markdown/MarkdownTypeBuilder.cs`.

Verified: `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Autogen|FullyQualifiedName~Schema"` — 543 passed, 2 skipped (pre-existing,
unrelated source-generator tests), 0 failed. All three fixes are additive (a new switch arm, a new
escaping helper applied at two call sites, a new-else-branch line shape) — every existing
#241/#242/#243, #212/#244, #86/#213, #66/#67/#240 regression test in this filter's scope stayed
green unmodified.
## Resolved in round 16, WP-A (Core disposal architecture) (2026-08-30)

Findings from the round-16 review pass (task board #266, #262; `work/bug-fix-plan-round16-2026-08.md`
WP-A; evidence in `work/review-round16-core-2026-08.md` §1 and
`work/review-round16-infrastructure-2026-08.md`'s Redis finding). Fixed in
`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverAdapter.cs`,
`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs`,
`src/Benzene.Cache.Redis/RedisCacheService.cs`.

- **[RESOLVED] #266 — `MicrosoftServiceResolverAdapter.Dispose()` disposed the wrapped MS DI scope
  synchronously; Microsoft.Extensions.DependencyInjection's own `ServiceProviderEngineScope.Dispose()`
  throws `InvalidOperationException` the moment it has to tear down a container-owned instance
  (scoped, transient, or singleton) that implements only `IAsyncDisposable` — an entirely ordinary
  shape for an async-native client/connection.** Every transport built on `Benzene.Core.Middleware`
  tears its per-message scope down through exactly this path
  (`MiddlewareApplication.HandleAsync`'s `using var serviceResolver = ...`), so any application
  registering such a service crashed on every single message that resolved it — and the resource's
  own `DisposeAsync` never ran (crash **and** leak). The Autofac adapter never had this defect: Autofac's
  own `ILifetimeScope.Dispose()` already bridges an `IAsyncDisposable`-only component correctly. Fixed
  generically (not per-type) in `MicrosoftServiceResolverAdapter.Dispose()` and, for the root
  provider/container, `MicrosoftServiceResolverFactory.Dispose()`: both now bridge to the wrapped
  scope/provider's own `DisposeAsync()` — with an **unbounded** wait, not the bounded-5s pattern used
  for best-effort telemetry flushes (`MeshAnnouncer`) — whenever it implements `IAsyncDisposable`,
  falling back to the plain synchronous `Dispose()` only when it doesn't. The wait is deliberately
  unbounded because it's awaiting the caller's *own* disposal code, not a network flush — abandoning it
  early would silently leak resources by design, and this now matches Autofac's own unbounded blocking
  semantics for the identical shape rather than introducing new behavior. Side effect (intentional,
  not a regression): a container-owned instance implementing **both** `IDisposable` and
  `IAsyncDisposable` now observes its `DisposeAsync`, not its `Dispose`, when torn down via
  `MicrosoftServiceResolverFactory.Dispose()`/`MicrosoftServiceResolverAdapter.Dispose()` — matching
  the preference `MicrosoftServiceResolverFactory.DisposeAsync()` already had — because the adapter has
  no way to know in advance, without attempting disposal, whether *any* other container-owned instance
  needs the async path; `MicrosoftDITest.Dispose_ProviderBuiltByFactory_DisposesSingletons` updated
  accordingly (asserts `DisposedAsync`, not `Disposed`). New tests:
  `MicrosoftDITest.Issue266_ScopedAsyncOnlyDisposable_ScopeDisposal_DoesNotThrow_AndActuallyDisposesAsync`,
  `MicrosoftDITest.Issue266_SingletonAsyncOnlyDisposable_FactoryDisposal_DoesNotThrow_AndActuallyDisposesAsync`
  (both red before the fix: `InvalidOperationException`), plus the permanent parity test
  `AsyncOnlyDisposableParityTest` (new file) asserting both the Autofac and Microsoft DI adapters
  dispose an `IAsyncDisposable`-only container-owned service — singleton and scoped — identically,
  without throwing, and actually run its `DisposeAsync`.
- **[RESOLVED] #262 — `RedisCacheService` is `IAsyncDisposable`-only, so it tripped the identical
  #266 defect whenever container-owned, including through `MicrosoftServiceResolverFactory.Dispose()`'s
  `(_serviceProvider as IDisposable)?.Dispose()` — the *only* disposal path `Benzene.Aws.Lambda.Core`
  has at all (its whole disposal chain, `IAwsLambdaEntryPoint`/`AwsLambdaHost<TStartUp>`, is
  `IDisposable`-only, with no `DisposeAsync` anywhere in that chain for a caller to prefer instead).**
  Fixed independently of #266 per the plan's explicit ruling (the #266 adapter fix alone would mask
  this one, since it covers Benzene-managed containers — but `RedisCacheService`'s own `CLAUDE.md` tells
  consumers to register it in *their own* container, which may not be Benzene's adapter at all): added
  `IDisposable` to `RedisCacheService`, bridging synchronously to its existing `DisposeAsync()` with the
  established **bounded** 5-second wait (`DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5))`,
  swallowing the resulting `AggregateException`) — the same pattern as `MeshAnnouncer.Dispose()` and
  round 15's `InternallyOwnedRateLimiterHolder<TContext>.Dispose()`. Bounded (unlike #266's adapter-level
  fix) because the work being awaited here is `RedisCacheService`'s own disposal, a prompt local
  operation (disposing an already-connected `IConnectionMultiplexer`, or abandoning a connect that never
  completed) — not arbitrary user code. New test file
  `test/Benzene.Core.Test/Cache/Redis/RedisCacheServiceContainerDisposalTest.cs`
  (`ScopedRedisCacheService_PerMessageScopeDisposal_DoesNotThrow`,
  `SingletonRedisCacheService_SyncFactoryDisposal_DoesNotThrow`), both red before the fix
  (`InvalidOperationException` via `MicrosoftServiceResolverAdapter`/`MicrosoftServiceResolverFactory`)
  and green after — reproducing both the `AddScoped` per-message-scope path (mirroring
  `AwsLambdaEntryPoint.FunctionHandlerAsync`'s per-invocation scope) and the `AddSingleton`
  container/factory-disposal path (mirroring `AwsLambdaHost<TStartUp>.Dispose()`'s only disposal route).

Scoped verification: `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Cache.Redis|FullyQualifiedName~Microsoft|FullyQualifiedName~AsyncOnlyDisposableParityTest|FullyQualifiedName~Autofac"`
— 90 passed, 0 failed. Full `Benzene.Test.Cache` namespace also re-run standalone — 67 passed, 0
failed. The centralized post-merge baseline (all round-16 work packages) is the coordinator's
responsibility per the round's execution protocol.

**[OPEN] — should `IServiceResolver` grow an async disposal contract?** #266's fix bridges the
existing purely-synchronous `IServiceResolver : IDisposable` contract to `DisposeAsync()` underneath,
inside each adapter, rather than adding `IAsyncDisposable` to `IServiceResolver` itself and switching
`MiddlewareApplication`'s per-message `using var serviceResolver = ...` to `await using`. That would be
a public-contract change rippling through every DI adapter (Microsoft, Autofac, any third-party
implementation) and every call site that constructs/disposes an `IServiceResolver` — explicitly ruled
out of scope for round 16 (`work/bug-fix-plan-round16-2026-08.md` WP-A ruling 3) because the sync
bridge already resolves the user-visible defect without it. Worth a maintainer decision before 1.0:
would a genuinely async per-message disposal path (no blocking wait at all, even bounded) be worth the
breaking-change cost across every adapter and hosting integration, or does the sync-bridge-with-
unbounded-wait resolve this permanently? If the abstraction never grows one, document that
`IServiceResolver.Dispose()` blocking on a user's `DisposeAsync()` is a deliberate, permanent design
choice (not a stopgap) so it isn't re-litigated as an oversight in a future round.
## Round 16, WP-G: Mesh.Dispatch (2026-08-30)

- **[RESOLVED] #254 — `MeshDispatchRateLimiter.Prune()` could delete a concurrently-installed
  current-minute window, silently losing an increment.** `Prune()` enumerates `_windows` and, for each
  entry whose captured `Window.Start` is stale, removed it via the unconditional two-argument
  `_windows.TryRemove(pair.Key, out _)` — a remove-by-key with no check that the dictionary still held
  the same value it decided was stale. `MeshDispatchGuardMiddleware.HandleAsync` calls `Prune()`
  immediately before every `TryAcquire`, so at a minute boundary a genuinely concurrent request for the
  same key (the same identity or target dispatching again right after the rollover) could install a
  fresh `Window(currentMinute, Count=1)` between Prune()'s enumeration reading the stale value and its
  `TryRemove` call executing; the unconditional remove then deleted that fresh window, letting more
  requests through per minute than `MaxPerMinutePerIdentity`/`MaxPerMinutePerTarget` configure. Fixed
  by switching to the conditional `TryRemove(KeyValuePair<TKey,TValue>)` overload (.NET 5+), passing
  the exact pair the enumeration produced, so a stale decision can never delete a concurrently-replaced
  value. `src/Benzene.Mesh.Dispatch/MeshDispatchRateLimiter.cs`. Test:
  `Prune_RaceAtTheMinuteBoundary_NeverDeletesAConcurrentlyInstalledFreshWindow`
  (`MeshDispatchRateLimiterTest` in `MeshDispatchTest.cs`) — reproduces the race deterministically (not
  probabilistically) via a custom `IEqualityComparer<string>` installed on a reflection-swapped
  `_windows` dictionary, which fires a real concurrent `TryAcquire` pair at the exact point the real,
  compiled `Prune()`'s `TryRemove` call looks up the key's bucket (after it has decided to remove the
  entry but before the removal executes) — exercising the actual fixed code path, not a hand
  re-implementation of it. Verified red against the pre-fix source (entry wrongly deleted, a
  `limit: 1` follow-up request wrongly succeeded) before applying the fix.
- **[RESOLVED] #255 — `MeshDispatchMessageHandler.HandleAsync`'s `NotImplemented` exit path (registered
  service, rate limit passed, but no `IMeshServiceDispatcher` matches the entry's `Source`) never
  called `Audit(...)`.** Every other termination path — gate-blocked, bad-request, not-found,
  rate-limited, dispatch-failed (#186), and the `dispatched` success path — leaves an audit record;
  this one, the routine "forgot to wire the matching `AddMeshXxxDispatcher()`" post-deploy
  misconfiguration (not a hostile input), silently vanished from the trail. Fixed by adding an
  `Audit("no-dispatcher", ...)` call on that branch before returning the `NotImplemented` result, same
  fields as every sibling exit path. `src/Benzene.Mesh.Dispatch/MeshDispatchMessageHandler.cs`. Test:
  `NoDispatcherRegisteredForSource_StillLeavesAnAuditRecord` (alongside the existing
  `NoDispatcherForSource_ReturnsNotImplemented`, which only checked the returned status).

Verified: `dotnet test test/Benzene.Mesh.Test -c Release --filter "FullyQualifiedName~MeshDispatch"` —
30 passed, 0 failed (includes the pre-existing #185/#186/#187 regression tests, unaffected).
## Round 16, WP-B — Benzene.Resilience.Polly (2026-08-30)

- **[RESOLVED] #267 — `PollyResilienceMiddleware<TContext>`'s docs overclaimed Hedging/Fallback
  support that doesn't compile, and the underlying design had no isolation between concurrent Polly
  attempts.** Two independent halves, one root: (a) the package's `.csproj` `<Description>`, XML
  `<remarks>`, `CLAUDE.md`, and `docs/cookbooks/polly-resilience.md` all listed Hedging and Fallback
  as supported strategies, but Polly.Core 8.5.0's `AddHedging`/`AddFallback` exist only on the
  *generic* `ResiliencePipelineBuilder<TResult>`, while every `.UseResiliencePipeline(...)` overload
  only hands out the non-generic builder — the advertised code shapes don't compile (`CS1929`/`CS0305`,
  verified in `work/review-round16-core-2026-08.md` §2). (b) The deeper bug:
  `PollyResilienceMiddleware<TContext>.HandleAsync` shared one mutable `CancellationTokenAccessor`,
  one `context`, and one `next` closure across every Polly attempt with zero isolation — reachable
  today via Polly's own public non-generic `AddStrategy(...)` extensibility point (a hand-rolled
  concurrent-attempt "hedge"), which ran `next()` (the entire downstream pipeline) twice for one
  message, tore the ambient token between attempts, and last-write-won the shared context. Proven
  3/3 by the xUnit repro in `work/review-round16-performance-2026-08.md` Finding 1 (all three
  assertions passed against the unmodified middleware, proving the corruption).
  - **Docs fix**: removed every Hedging/Fallback claim from the `.csproj` `Description`, the type's
    XML `<remarks>`, `CLAUDE.md`, and the cookbook (retitled "Polly Resilience Pipelines (circuit
    breaker, timeout, retry, rate limiter)"), replacing them with the accurate supported-strategy
    list (Retry, Timeout, CircuitBreaker, RateLimiter — the sequential-attempt strategies expressible
    on the non-generic builder) plus a new cookbook section,
    [Why concurrent-attempt strategies aren't supported](cookbooks/polly-resilience.md#why-concurrent-attempt-strategies-arent-supported),
    explaining both halves of the root cause. `docs/capability-matrix.md`'s Resilience row updated to
    match.
  - **Runtime fix**: added a per-`HandleAsync`-call re-entrancy guard (`Interlocked.Increment` on a
    one-element `int[]` counter allocated once per call, closed over via the existing state tuple) in
    the attempt callback — if a second attempt starts while one is still in flight, it throws
    `NotSupportedException` naming the problem ("a concurrent-attempt resilience strategy ... is not
    supported by PollyResilienceMiddleware<TContext> ... run attempts sequentially, or hedge at a
    different layer") before ever calling `next()` a second time, instead of silently corrupting the
    shared context/token. Zero added cost/behavioral change for every sequential-attempt strategy
    (Retry/Timeout/CircuitBreaker/RateLimiter never drive the counter above 1). Did **not** attempt
    per-attempt context/token isolation (option (b) from the performance review) — that is a bigger
    architecture change with no defined merge semantics for a mutable message context across
    concurrent attempts, deliberately deferred; see the `[OPEN]` entry below.
  - **Tests**: `test/Benzene.Core.Test/Resilience/PollyResilienceMiddlewareConcurrentAttemptGuardTest.cs`
    (new) — the `ConcurrentDuplicateStrategy` custom Polly strategy from the performance review's
    repro, rewritten to assert the corrected fail-fast behavior: the middleware throws
    `NotSupportedException` on the concurrent second attempt, `next()` runs at most once (no
    concurrent-execution, no shared-accessor tearing, no last-write-wins on the context observed).
    Every existing `test/Benzene.Core.Test/Resilience/PollyResilienceMiddlewareTest.cs` case (Retry/
    Timeout paths, the #237 and #63 regression tests) verified unchanged and green —
    `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~PollyResilienceMiddleware"`:
    14 passed (11 existing + 3 new), 0 failed.
## Round 16, WP-D: AWS idempotency convention + outbound client cancellation (2026-08-30)

- **[RESOLVED] `#260` `IdempotencyMiddleware.WasSuccessful`'s "null `MessageResult` == success" fall-through
  directly contradicted the "null == failure, redeliver" convention SQS/DynamoDb always had and `#229`
  extended to SNS/S3/EventBridge.** When a result-bearing (`IHasMessageResult`) pipeline completed
  without ever setting `MessageResult` (a non-standard pipeline that omits `MessageRouter` or
  short-circuits before it runs), the middleware treated that as success and permanently marked the
  idempotency claim `Completed` - even while the transport's own `#229` escalation was, in the very
  same call, throwing to demand redelivery. The redelivery SNS/S3/EventBridge/SQS/DynamoDb was just
  told to perform then hit the already-`Completed` claim and short-circuited as a duplicate success,
  without the real handler ever running again. Fixed by distinguishing the two null cases precisely:
  `context is IHasMessageResult hasResult` now returns `hasResult.MessageResult?.IsSuccessful ?? false`
  (a result-bearing transport with no result set is NOT proven successful - same release-the-claim path
  the middleware already takes for an explicit `IsSuccessful == false`), while a context type with no
  result concept at all keeps the old no-throw-as-success behaviour unchanged.
  `src/Benzene.Idempotency/IdempotencyMiddleware.cs`. Regression tests:
  `test/Benzene.Core.Test/Idempotency/IdempotencyMiddlewareTest.cs`
  (`HandlerCompletesWithoutSettingResult_TreatedAsNotSuccessful_ReleasesClaim_SoRedeliveryReprocesses`,
  plus the three genuinely-completed-message tests updated to explicitly set `MessageResult = Ok()` so
  they keep testing real success rather than the old null-fallback loophole) and
  `test/Benzene.Core.Test/Idempotency/IdempotencyMiddlewareSnsInteractionTest.cs` (the full
  `SnsApplication` + real `IdempotencyMiddleware<SnsRecordContext>` interaction: first attempt throws
  `SnsMessageProcessingException` and releases the claim; the redelivery actually re-runs the handler).
- **[RESOLVED] `#261` every outbound AWS SDK client middleware/client (SQS, SNS, EventBridge, Lambda,
  Step Functions, the three batch clients, and the standalone `Benzene.Aws.Sqs` `SqsMessageClient`)
  called its `*Async` SDK method with no `CancellationToken`, despite every one of those methods
  actually supporting one - so `UseTimeout(...)` (or any other consumer of the ambient
  `ICancellationTokenAccessor`, e.g. graceful-drain cancellation) around an outbound AWS send was a
  silent no-op; a stalled call ran until the AWS SDK's own default retry/socket timeout, not the
  configured deadline.** Fixed by giving every client the same optional
  `ICancellationTokenAccessor`-resolving constructor overload `HttpClientMiddleware` already has
  (`_cancellation?.CancellationToken ?? CancellationToken.None` at the point of use), threaded into the
  existing SDK overload at every call site - purely additive, no wire or interface change. Where a
  client is constructed via a DI extension (`UseSqsClient()`, `UseSnsClient()`,
  `UseEventBridgeClient()`, `UseAwsLambdaClient()`, `AddStepFunctionsClient()`, `AddLambdaHealthCheck()`),
  the accessor is resolved with `TryGetService` so pipelines without the registration keep working.
  Also fixed the now-false claim in `Benzene.Clients.Aws.Lambda/CLAUDE.md` (and matching stale comments
  in `AwsLambdaHealthCheck.cs`) that the Lambda invoke path "can't" forward the token into its own SDK
  call - `IAmazonLambda.InvokeAsync` (and every other AWS SDK method touched here) has always had a
  `CancellationToken` overload; `AwsLambdaHealthCheck`'s Active-mode ping now threads the accessor too.
  Files: `src/Benzene.Clients.Aws.Sqs/{SqsClientMiddleware.cs,SqsBatchMessageClient.cs,Extensions.cs}`,
  `src/Benzene.Clients.Aws.Sns/{SnsClientMiddleware.cs,SnsBatchMessageClient.cs,Extensions.cs}`,
  `src/Benzene.Clients.Aws.EventBridge/{EventBridgeClientMiddleware.cs,EventBridgeBatchMessageClient.cs,Extensions.cs}`,
  `src/Benzene.Clients.Aws.Lambda/{AwsLambdaClientMiddleware.cs,AwsLambdaClient.cs,
  AwsLambdaBenzeneMessageClient.cs,AwsLambdaHealthCheck.cs,Extensions.cs,CLAUDE.md}`,
  `src/Benzene.Clients.Aws.StepFunctions/{StepFunctionsClient.cs,StepFunctionsClientFactory.cs,Extensions.cs}`,
  `src/Benzene.Aws.Sqs/Client/SqsMessageClient.cs`. Regression tests: one per client family in the new
  `test/Benzene.Core.Test/Clients/Aws/OutboundClientCancellationTest.cs` (`TimeoutMiddleware` at a 50ms
  deadline around a mocked SDK call that runs 5s unless it observes a cancelled token - before the fix
  the call ran the full 5s regardless of the deadline; after, it aborts within a generous 2s ceiling).
  Note: `StepFunctionsClient` wraps its own SDK calls in a catch-all that converts any exception
  (including a genuine cancellation) into a `BenzeneResultStatus.ServiceUnavailable` result rather than
  rethrowing, so its observable fix is "the call actually completes near the deadline" rather than a
  propagated `TimeoutException` - documented inline on that test.

## Round 17, WP-F: Avro map + multi-branch union support (2026-08-30)

- **[RESOLVED] `#278` `AvroDatumConverter` had no `Schema.Type.Map` switch arm at all — any Avro
  `map` field crashed on serialize (complex values) or deserialize (primitive values).** Reachable
  through the package's own advertised "explicit/registered schema" use case
  (`AvroOptions.RegisterSchema<T>`), not exotic misuse: a `map` field fell through to the primitive
  `default` branch on both `ToDatum` and `FromDatum`, so a map's values never got recursively
  converted to/from the datum shape `GenericDatumWriter`/`GenericDatumReader` expect. The simplest
  case (`Dictionary<string,string>`) round-tripped through `Serialize` but threw
  `InvalidCastException` on `Deserialize` (`Convert.ChangeType` can't target a `Dictionary<,>`); a map
  of arrays-of-records threw `AvroException` on `Serialize` itself (`GenericDatumWriter` handed a raw
  `List<InnerRecord>` instead of a converted `object[]`). Fixed by adding `Schema.Type.Map` arms
  mirroring the existing `Array` handling: `ToMap` recursively converts each value against the map's
  value schema into a plain `Dictionary<string, object?>`; `FromMap` builds a
  `Dictionary<string, TValue>` sized from the target property's declared value type (supports
  `Dictionary<string,V>`, `IDictionary<string,V>`, `IReadOnlyDictionary<string,V>`), converting each
  value recursively. Avro map keys are always strings per spec — a non-string-keyed CLR dictionary
  target (checked via the value's/target type's own `IDictionary<TKey,TValue>` generic argument, not
  just per-entry, so it's caught even for an empty map) throws `NotSupportedException` naming the
  constraint, rather than silently coercing the key. `src/Benzene.Avro/AvroDatumConverter.cs`.
  Regression tests: `test/Benzene.Core.Test/Plugins/Avro/AvroMapTest.cs`
  (`RoundTrips_PrimitiveValuedMap`, `RoundTrips_RecordWithinArrayWithinMap`,
  `Serialize_NonStringKeyedDictionaryTarget_ThrowsNotSupportedException`,
  `Deserialize_NonStringKeyedDictionaryTarget_ThrowsNotSupportedException`) — all four confirmed red
  against the pre-fix code, green after.
- **[RESOLVED] `#279` `AvroDatumConverter.NonNullBranch` always picked the FIRST non-null branch of
  every union, on both serialize and deserialize — correct only for the common 2-branch
  `["null", X]` "optional field" shape, and silently type/value-corrupting for a union with 3+
  non-null branches.** A hand-authored `["null","string","long","boolean"]` union (reachable via
  `RegisterSchema<T>`, the "polymorphic value field" shape) always serialized through the `string`
  branch regardless of the value's actual type: a `bool` value round-tripped back as the *string*
  `"True"`, a `long` as the string `"42"` — not merely mis-formatted, the CLR type of the result
  changed, and for some type/value combinations (e.g. `Convert.ToBoolean(42L)`) the original value was
  lost outright rather than just its type. Fixed by resolving the branch by actual runtime type in
  both directions instead of always taking the first non-null branch: `ResolveWriteBranch` (serialize)
  matches the CLR value's actual type against each candidate branch's Avro tag (exact-width match
  first — e.g. `bool`→Boolean, `long`→Long — then a numeric-widening fallback — e.g. `int` against a
  union offering only `long` — then, for anything still unmatched such as multiple record branches of
  similar shape, the first declared branch, same as the old always-first behaviour); `ResolveReadBranch`
  (deserialize) matches the *datum's* actual runtime type — `GenericDatumReader` already resolved the
  wire's real branch by the time the datum reaches this converter (`bool`/`long`/`string`/
  `GenericRecord`/etc.), so this recovers that information from the datum's CLR shape instead of
  discarding it. Both branch-resolution helpers see exactly one non-null candidate for the common
  2-branch shape, so that path is unconditionally unchanged (byte-identical, not just tested-to-look
  unchanged). `src/Benzene.Avro/AvroDatumConverter.cs`. Regression tests:
  `test/Benzene.Core.Test/Plugins/Avro/AvroMultiBranchUnionTest.cs`
  (`RoundTrips_BooleanValue_ThroughAThreePlusBranchUnion`,
  `RoundTrips_LongValue_ThroughAThreePlusBranchUnion`,
  `RoundTrips_StringValue_ThroughAThreePlusBranchUnion` — all three confirmed red against the pre-fix
  code, green after; plus the required 2-branch-nullable-union regression pinning the unchanged
  behaviour: `TwoBranchNullableUnion_ReferenceTypeValuePresent_StillRoundTrips`,
  `TwoBranchNullableUnion_ReferenceTypeValueNull_StillRoundTrips`,
  `TwoBranchNullableUnion_ValueTypePresent_StillRoundTrips`,
  `TwoBranchNullableUnion_ValueTypeNull_StillRoundTrips`). All pre-existing `Benzene.Avro` tests
  (`AvroSerializerTest`, `AvroSchemaMismatchTest`, `AvroDepthGuardTest`, `AvroSchemaResolverTest`,
  `AvroRequestResponseRoundTripTest`, `AvroMediaFormatTest`, including the #56/#57 regression tests)
  verified unchanged and green.

## Open — maintainer decisions (the real remaining backlog)

None of these is a clean self-contained bug; each changes behaviour, a public API, or a policy.

### New from the round-10 pass (2026-08-26)
- **[DECISION] Worker self-stop leaves the process Ready and health green** — Kafka onFault, Kafka
  DLT-produce failure, and EventHub `CatchHandlerExceptions=false` all deliberately stop the worker;
  every transport health check probes broker reachability, none probes "is my worker still running",
  and `IBenzeneWorker` exposes no state to probe — in Kubernetes the pod stays Ready while the queue
  backs up. Candidate shapes: a stopped/faulted flag surfaced through a liveness-category health
  check, or self-stop optionally failing the host fast. (Round-10 kafkaworkers finding 5.)
- **[DECISION] EventHub has no poison-message escape hatch** — skip-on-failure or stop-the-worker
  only; the argument that justified Kafka's retry-then-dead-letter producer applies verbatim to
  EventHub (also a checkpoint-stream with no broker DLQ). Documented-deliberate today; feature
  candidate. (`BenzeneEventHubConfig.cs:34-74`; round-10 kafkaworkers finding 6.)
- **[DECISION] gRPC client per-call deadlines not settable by the caller** —
  `GrpcBenzeneMessageClient` only forwards the inherited inbound `ServerCallContext.Deadline`;
  `GrpcContextConverter` accepts a deadline no public path supplies. API-surface call.
  (round-10 cosmosgrpc finding 4.)
- **[DECISION] Missing-topic status asymmetry across transports** — EventGrid/QueueStorage/Timer
  report a missing topic as `ValidationError` ("Topic is missing"); sentinel-returning transports
  report `NotFound`. Same condition, two wire-visible statuses; normalizing is a behavior change.
  (The stale `MessageRouter` comment claiming uniformity is fixed under task #98.)

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
- **[RESOLVED] Cache null-payload negative caching (#140)** — superseded the stale entry this replaces
  (which described pre-WP-X behavior: "a null payload is still written back" - WP-X/#100 already made
  `LazyLoadAsync` skip writing a null `Payload` back to the cache, so that half was already wrong by
  the time this was written). The real remaining bug: `LazyLoadAsync` decided a cache hit with
  `found && cacheValue is not null` (`CacheEntry.cs`), so a reference-type `T` that a caller had
  explicitly cached as `null` (via `SetValueAsync(default)` - a genuine negative-cache entry) was
  treated as a permanent miss forever, re-running `databaseReadFunc` on every single call and defeating
  the entire point of negative-caching a known-absent value (cache-penetration amplification). Fixed by
  deciding the hit purely on `found` (presence of a stored value) — the same signal that already fixed
  the value-type miss-as-hit hazard — since JSON-serializing `null` always produces the 4-character
  string `"null"`, never an empty stored value, so "present" and "present but deserializes to null" are
  never confused with genuinely absent. `LazyLoadAsync` itself is still conservative by default: a
  successful database read whose `Payload` is `null` is still not written back automatically (avoids
  surprising every existing caller with new cache writes); a caller that wants a known-null result
  negative-cached calls `SetValueAsync(default, ...)` itself once it decides the null is cacheable.
  (`CacheEntry.cs`, `test/Benzene.Core.Test/Cache/CacheEntryTest.cs`.)
- **[DECISION] Version unknown-version passthrough** — an unknown requested version silently falls
  back to the max version (`VersionSelector.cs:21-29`). A documented per-policy behaviour.

### Tracked findings rounds 12–14, WP-J — Cache + RateLimiting round-13 residue (done)
Ruled in [`bug-fix-plan-rounds12-14-2026-08.md`](archive/bug-fix-plan-rounds12-14-2026-08.md) §"WP-J" and
[`bug-fix-designs-round13-2026-08.md`](archive/bug-fix-designs-round13-2026-08.md) — round 13's blind
re-audit of the round-11-fixed `Benzene.RateLimiting`/`Benzene.Cache.Core`/`.Redis` packages (#198–202,
3 worth-fixing + 2 minor). Read alongside the round-11 `#133`–`#147` entries above, none of which are
regressed by this work package.
- **[RESOLVED] #198 — `RedisCacheService.CreatePrefixActions` built a wildcard-invalidation pattern
  with no check that the prefix was non-empty**, so an empty/whitespace prefix (a missing tenant id, an
  unset config value) produced the literal pattern `"*"`, which `RedisWildcardActions.InvalidateEntryAsync`
  then deleted in batches — every key in the logical database, one bad string interpolation away from a
  full cache wipe. Fixed at both ends per the ruling's "fail fast, defense-in-depth" split:
  `CreatePrefixActions` now throws `ArgumentException` on a null/empty/whitespace prefix before ever
  building the glob (a loud startup/first-use error instead of a silent keyspace wipe), and
  `RedisWildcardActions.InvalidateEntryAsync` independently refuses to execute a bare or
  effectively-universal pattern (empty/whitespace, or — after trimming — composed entirely of `*`,
  since Redis glob syntax treats `"*"`/`"**"`/`" * "` identically) with `InvalidOperationException`,
  covering the still-reachable `CreateWildcardActions` escape hatch (an unescaped, caller-supplied
  pattern by design) as a second, independent line of defense. A real, non-empty prefix still produces
  the identical escaped `prefix*` pattern and invalidates exactly as before. Tests:
  `CreatePrefixActions_EmptyOrWhitespacePrefix_ThrowsRatherThanBuildingAUniversalPattern`,
  `CreatePrefixActions_RealPrefix_StillProducesTheEscapedPrefixStarPatternAndInvalidates`,
  `WildcardActions_BarePattern_RefusesToRunRatherThanDeletingTheEntireKeyspace`,
  `WildcardActions_OtherEffectivelyUniversalPatterns_AlsoRefuseToRun`,
  `WildcardActions_NonUniversalPattern_StillRunsNormally` (`RedisCacheServiceTest.cs`).
- **[RESOLVED] #199 — `CacheWriteActions.WriteThroughAsync`'s 3-arg overload ran the caller-supplied
  `getCacheAction`/`getCacheValue` delegates outside the try/catch protection `SyncCacheAfterWriteAsync`
  (#139) gives the actual cache I/O**, so a delegate throwing after a successful database write
  propagated as if the database write itself had failed — exactly the failure mode #139 closed, just
  reachable one call wider. Fixed by moving the whole decide-then-sync sequence (evaluating
  `getCacheAction`, and — for `Set` — `getCacheValue`) inside the same `SyncCacheAfterWriteAsync` call
  that already wraps the cache I/O, so a throw from either delegate degrades identically: logged and
  swallowed, the already-successful database result still returned. A caller-driven
  `OperationCanceledException` still propagates unchanged, matching #139's/#141's established
  convention. Tests: `WriteThroughAsync_GetCacheValueDelegateThrows_StillReturnsTheSuccessfulDatabaseResult`,
  `WriteThroughAsync_GetCacheActionDelegateThrows_StillReturnsTheSuccessfulDatabaseResult`,
  `WriteThroughAsync_GetCacheValueDelegateThrowsOperationCanceled_Propagates` (`CacheEntryTest.cs`).
- **[RESOLVED] #200 — the "one internally-owned rate limiter" guard round 11's `#133` fix added
  (`UseInternallyOwnedRateLimiting`) was keyed on the shared `IBenzeneServiceContainer`, but
  `MiddlewarePipelineBuilder<T>.Create<TNewContext>()` deliberately shares that same container across a
  service's sibling pipelines for different transports** (the documented multi-transport pattern — see
  `examples/AwsMesh`'s `MeshServiceWiring.Configure`, which wires ApiGateway/BenzeneMessage/Sqs/Sns/
  EventBridge, each its own context type, off one shared `IBenzeneApplicationBuilder`) — so building two
  unrelated pipelines off one container, each with its own `UseFixedWindowRateLimiting`, threw
  `InvalidOperationException` on the second even though the docs and exception text both describe the
  guard as "per pipeline." Re-keyed the guard (and the underlying DI registration) on a new internal
  `Extensions.InternallyOwnedRateLimiterHolder<TContext>` wrapper type, closed over the pipeline's own
  `TContext` — the identity this codebase already uses to distinguish sibling pipelines at registration
  time (`MiddlewarePipelineBuilder<TContext>.Build()`'s own `PipelineDescriptor` is keyed the same way).
  Two sibling pipelines (genuinely different `TContext`) now each get their own independent,
  container-owned limiter; two `UseXRateLimiting` calls sharing one pipeline builder (and so the same
  `TContext`) still collide on the same key and still fail fast exactly as before. The holder also
  implements `IAsyncDisposable` itself, forwarding to the wrapped `RateLimiter` — necessary because the
  container only disposes what it directly resolved (the holder), not a `RateLimiter` field buried
  inside it, so #133's disposal fix is preserved rather than silently regressed by the extra layer of
  indirection. Tests: `SiblingPipelines_OffOneSharedContainer_EachGetTheirOwnIndependentInternallyOwnedLimiter`
  (new) plus the existing `InternallyCreatedLimiter_IsDisposedWhenTheContainerIsDisposed` (updated to
  resolve through the new holder type — an internal type, exposed to the test assembly via
  `InternalsVisibleTo`, the same pattern this repo already uses elsewhere) and
  `StackingTwoInternallyCreatedLimiters_OnOnePipeline_FailsFast` (unchanged, still green) —
  `RateLimitingPipelineTest.cs`.
- **[RESOLVED] #201 (minor) — negative caching's presence check `!string.IsNullOrEmpty(cacheValue)`
  conflated "key absent" with "the serializer emitted an empty string"**, silently reintroducing #140's
  cache-penetration hazard for any `ISerializer` that encodes a null/default value as `""` rather than
  the stock `System.Text.Json` serializer's 4-character `"null"`. Changed `CacheEntry.TryReadEntryAsync`'s
  presence detection to `cacheValue != null` (a store miss is `null`; any real stored value — including
  `""` — is a hit). Verified both read paths genuinely distinguish nil-from-store vs. empty-value before
  relying on that: the in-memory test double's `GetEntryValueAsync` already returned a real `null` for a
  missing dictionary key; `RedisCacheEntry.GetEntryValueAsync` did too for a genuine Redis miss
  (`StringGetAsync`'s `RedisValue.Null` converts to a `null` string), **but its own error-handling catch
  block returned `""` on a thrown exception** — which the new `cacheValue != null` check would have
  misread as a hit of a genuinely-empty cached value instead of "the read failed." Fixed that catch to
  return `null` too, so a Redis-side error stays indistinguishable from a genuine store miss under the
  new presence rule. Test: `LazyLoadAsync_CustomSerializerEncodesNullAsEmptyString_ExplicitlyCachedNull_IsStillAHitWithoutCallingDb`
  (`CacheEntryTest.cs`, using a new `EmptyStringForNullSerializer` test double).
- **[RESOLVED] #202 (minor) — `RateLimitingMiddlewareBase.HandleAsync` caught `ObjectDisposedException`
  around both the cost delegate and `Acquire()` in one block (per `#143`'s deliberate fix, which moved
  the cost delegate inside this guard), always reporting "the rate limiter has already been disposed"
  even when the exception came from an unrelated disposed dependency inside the cost delegate.** Split
  into two catches: an `ObjectDisposedException` from the cost delegate is now reported as
  `"Rate limit exceeded: the permit cost delegate depends on a resource that has already been disposed"`
  (still failing CLOSED, per #134's ruling — a broken cost delegate must never silently bypass the
  limiter), while one from `Acquire()` keeps #134's original
  `"Rate limit exceeded: the rate limiter has already been disposed"` message unchanged. Every other
  aspect of #143's/#134's behavior (negative-cost rejection, non-ODE exceptions from the cost delegate
  still propagating unhandled, `Acquire()`'s `ArgumentOutOfRangeException` handling) is untouched. Test:
  `BringYourOwnCost_CostDelegateThrowsObjectDisposedException_IsReportedAsACostDelegateFailure_NotAsTheLimiterDisposed`
  (`RateLimitingPipelineTest.cs`).

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

### New from round 16, WP-B — Benzene.Resilience.Polly (2026-08-30)
- **[OPEN] Should `PollyResilienceMiddleware<TContext>` ever support concurrent-attempt Polly
  strategies (Hedging, or a hand-rolled concurrent-attempt strategy) via per-attempt isolation?**
  #267's fix (below) makes the middleware fail fast with `NotSupportedException` on a concurrent
  second attempt rather than corrupt shared state, but that's a guard, not support. The performance
  review's option (b) — redesign the ambient-token/context exposure to be attempt-scoped (e.g. an
  `AsyncLocal<CancellationToken>` per logical attempt instead of one mutable
  `CancellationTokenAccessor` field, plus a defined merge/isolation semantics for the shared,
  mutable `TContext` each attempt's `next()` closure currently writes to directly) is a bigger
  architecture question deliberately deferred this round: per-attempt context cloning has no
  obvious merge semantics for an arbitrary mutable message context (which attempt's writes win? do
  they merge? is that even meaningful for a `MessageResult`?). Needs a maintainer decision on
  whether concurrent-attempt strategies are worth that redesign at all, or whether the middleware's
  documented position should simply stay "sequential-attempt only, hedge at a different layer."
  (`work/review-round16-performance-2026-08.md` Finding 1, recommendation 2(b).)

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
