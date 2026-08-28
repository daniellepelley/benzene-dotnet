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

### Tracked findings rounds 14-15, WP-G — Serialization + gRPC (done)
Ruled in [`bug-fix-rulings-round14-15-2026-08.md`](bug-fix-rulings-round14-15-2026-08.md) §3 WP-G;
evidence in [`bug-fix-designs-round15-2026-08.md`](bug-fix-designs-round15-2026-08.md) §9.
- **[RESOLVED] #260 — `Benzene.Xml.XmlSerializer.Deserialize` had no nesting-depth guard, the identical
  bug class as Avro's #56 left unfixed here: a self-referencing/deeply-nested request DTO (a comment
  tree, category tree, org chart) drove `System.Xml.Serialization.XmlSerializer`'s generated
  deserializer into unbounded CLR recursion and an *uncatchable* `StackOverflowException`.** Added
  `XmlOptions` (new - the package had no options type at all) with `MaxDepth` (default 32) and a
  hand-rolled `DepthGuardedXmlReader` - an `XmlReader` decorator that forwards every member to the
  wrapped reader unchanged except `Read()`, which checks the wrapped reader's own (BCL-correct) `Depth`
  against `MaxDepth` whenever the current node is an element start, throwing `BenzeneException` once
  exceeded (matching the exception type the package's other error paths use, since `Benzene.Xml` had no
  custom exception type to reuse). `Deserialize` now wraps the raw `XmlReader.Create(...)` result in
  this before handing it to the BCL deserializer. `AddXml`/`AddXml<TContext>`/`UseXml<TContext>` gained
  an optional `Action<XmlOptions>? configure` parameter, wired the same shape as `Benzene.Avro`'s
  `AddAvro`/`UseAvro`. Serialization (writing a response) is deliberately not guarded - not
  attacker-controlled, out of the ruling's scope. See WP-G; tests in
  `test/Benzene.Core.Test/Plugins/Xml/XmlDepthGuardTest.cs`.
- **[RESOLVED] #261 — `ReflectionGrpcMethodFinder`'s duplicate-gRPC-method check was case-sensitive
  (default `GroupBy` equality) while `GrpcRouteFinder`'s lookup it must agree with is deliberately
  `StringComparer.OrdinalIgnoreCase` - a case-variant duplicate (`"/pkg.Service/Method"` vs
  `"/pkg.Service/METHOD"`) passed the finder's check silently and then crashed with a generic,
  far-less-actionable `ArgumentException` from inside `GrpcRouteFinder`'s
  `.ToDictionary(..., StringComparer.OrdinalIgnoreCase)` instead of the finder's intended, clearer
  `BenzeneException`.** `ReflectionGrpcMethodFinder.FindDefinitions()`'s `GroupBy` now case-folds via
  `StringComparer.OrdinalIgnoreCase`, so the case-variant pair is caught at the finder and never reaches
  the route finder. See WP-G; test:
  `GrpcRouteFinderTest.ReflectionGrpcMethodFinder_WhenTwoHandlersShareAGrpcMethod_DifferingOnlyByCase_ThrowsBenzeneException`.
- **[RESOLVED] #262 (minor) — `MessagePackSerializer`'s custom-options constructor's doc-comment
  example (`MessagePackSerializerOptions.Standard.WithResolver(...)`) suggested a pattern that, if
  followed literally, silently reintroduces the `MessagePackSecurity.TrustedData` DoS exposure the
  default constructor's `UntrustedData` setting exists to prevent, with zero warning about the
  trade-off.** Added a doc-comment warning: a caller-supplied `options` must call
  `.WithSecurity(MessagePackSecurity.UntrustedData)` itself if the payload source is untrusted. See
  WP-G.

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
