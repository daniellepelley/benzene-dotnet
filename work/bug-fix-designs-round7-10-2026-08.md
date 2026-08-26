# Tracked-findings fix designs (review rounds 7–10) — RULING + implementation plan

**Status:** ✅ **APPROVED for implementation** — design ruling, 2026-08-26. Covers the findings on the
shared task board opened by the round-7/8/9 adversarial review passes (tasks #30–#95); **round-10
findings will be folded in as they land** — this file is a *living* working doc until the review
sequence is closed, then it archives like its predecessor.

**This is a ruling document**, the successor to `work/archive/bug-fix-designs-2026-08.md` (the round-5/6
ruling, all nine of whose work packages shipped). Each item below records a *decision*, its
*rationale*, and the *rejected alternatives*. A future agent implementing, reviewing, or re-reviewing
this code must not re-litigate these decisions or "fix" them back the other way — if a decision here
proves wrong in practice, amend **this document first**, stating why, then change the code. Same
anti-flip-flop discipline as `work/benzene-result-errors-ruling.md`.

**How these findings were produced.** Rounds 7–9 were review-only passes (no fixes) that
(a) adversarially re-reviewed the round-5/6 fix round's *own newly-written code* — where new bugs are
most likely — and (b) swept ground that had had little or no dedicated scrutiny in the earlier rounds
(core pipeline/DI, validation, resilience, observability, CLI/codegen, serialization, the Autofac DI
adapter, the mesh fleet/usage/artifact-store backends, the schema registry, the testing
infrastructure itself, hosting/config, and the abstractions packages). Every tracked finding was
**verified with concrete evidence** — a live reproduction, a completed stress test, or a
compiler/generator-driven probe — before being logged; the ones that were read-confidence-only are
flagged as such in their task entries and re-confirmed where it mattered (see WP-B/#31, WP-J/#53).

**Task board mapping:** every finding keeps its task number as its stable identity. The board is the
authoritative open/closed state; this doc is the authoritative *design*.

---

## 0. Spec scoping ruling — none of these touch `docs/specification/**`

Re-checked, same conclusion as the round-5/6 ruling: **zero changes to the language-neutral spec.**
The spec covers wire contracts, the status vocabulary, mesh *wire* contracts, contract
documents/hashes, and the Cloud Service Profile. Against this batch:

- **Mesh host/UI/backends (#34–#37, #41, #72–#79)** — `deploy/Mesh/**`, `src/Benzene.Mesh.Fleet.*`,
  `.Usage.*`, `.Tracing.*`, `.Aws.S3`/`.Azure.Blob`/`.GoogleCloud.Storage` are deployables and .NET
  backend adapters, not spec surfaces; `mesh.md` line 389 explicitly excludes `benzene:mesh:query:*`.
- **Codegen/CLI (#46, #65–#67, #86, #87)**, **schema comparers (#49, #53)**, **schema registry
  (#93, #95)** — port-level tooling; the spec's `contract-document.md` closure walk covers `oneOf`
  for *client generation and hashing*, not for compatibility diffing or markdown/apigateway emission.
- **Stores/DI/serialization/resilience/hosting** — all .NET port implementation detail.

The one honest caveat, unchanged from before: **if a second language port ever ships a compatibility
comparer**, the discriminator-matching rule ruled in WP-J is the candidate for a future spec section +
fixture. Not now — the spec stays taut. Anyone who believes a fix here needs a spec change raises it
deliberately per `AGENTS.md` (fixtures updated, all ports re-verify); nobody slips one in.

---

## 1. Cross-cutting principles — the durable rules this batch establishes

The round-5/6 ruling established P1–P7 (config validated for satisfiability; every claim/lease fenced;
"no response" is legitimate on every transport; I/O on a request path accepts ambient cancellation;
query-side inputs degrade to absent, never throw; no inert options; examples state their security
posture). Those still hold and several findings below are simply *unfinished instances* of them. This
batch adds four rules, each earned by a recurring pattern across rounds 7–9:

- **P8 — A fix lands on every sibling, not just the instance that surfaced it.** The single biggest
  lesson of rounds 7–9: the round-5/6 fixes were repeatedly correct but incompletely swept. WP-6
  fixed one Lambda client shape and left the other (#43); WP-7 fixed one health check's
  cancellation-classification and left ~10 more newly-exposed (#50) plus left `IdempotencyMiddleware`
  un-threaded (#62); WP-5 validated CosmosDb's required field and left five transports (#39); WP-9
  fixed the comparer's walk but a coverage-changing discriminator edit still mis-keys (#53). **Rule:
  when a fix targets one member of a family, the same change is applied to — or explicitly ruled out
  for — every sibling in the same pass, and a test asserts the family, not the instance.**
- **P9 — Untrusted input is bounded and neutralized at the boundary.** Depth/size/character limits
  live where external data enters, before it reaches a recursive parser, a header sink, a query
  string, or a storage key. Instances: Avro nesting depth (#56), correlation-id CRLF/length (#64),
  chunked-transfer size bypass (#35), KQL interpolation (#78, config-time today), schema-registry
  null subject (#93).
- **P10 — A CI-gating tool fails loudly or it is worse than nothing.** A command sold as a gate
  (`diff`, `healthcheck`, `EnsureBackwardCompatible`) must exit non-zero / throw on the bad outcome
  it exists to catch; silently passing is the worst failure mode because it manufactures false
  confidence. Instances: `benzene diff`/`EnsureBackwardCompatible` deserializer corruption (#46),
  `benzene healthcheck` never failing (#65). (The `profile-check`/`diff` exit-code fixes already
  shipped in an earlier round set this precedent; these are the unswept siblings — see P8.)
- **P11 — Every serialization and codegen path handles `oneOf`/discriminated unions.** The polymorphism
  feature (`SchemaGenerationOptions.UseOneOfForPolymorphism`) is first-class, so every consumer of a
  schema must have a `oneOf` branch — not fall through to a null/blank/uncompilable default. Instances:
  `benzene build` C# client (#66), `CodeGen.Markdown` (#86), the schema comparer's own coverage (#49);
  the round-5/6 comparer fix (#25) and this batch's #53 are the wire/diff side of the same rule.

The individual work packages name which principle each fix serves.

---

## 2. Work packages

Each package is sized for one agent in one isolated worktree, disjoint from the others so they can run
in parallel (the round-5/6 execution model — merge one at a time, resolve the mechanical
`outstanding-bugs.md`/`capability-matrix.md` append-conflicts, build-verify, push). **Per-fix
discipline (all packages):** red→green regression test (write it, revert the fix, watch it fail,
restore, watch it pass); XML/contract docs updated in the same commit as the behavior; one logical
change per commit; update `docs/capability-matrix.md` and add a `[RESOLVED]` line to
`work/outstanding-bugs.md` pointing back here.

### WP-U — Examples build regression (URGENT, land first)
**Task #68.** `Benzene.Examples.sln` fails to build across ~18 files: every example's custom
`IHealthCheck` still has the old parameterless `ExecuteAsync()` after WP-7a changed the interface to
`ExecuteAsync(CancellationToken)`. This is **live on `main`** and is only invisible because
`Benzene.Examples.sln` isn't in the CI baseline (that gap is #81, in WP-R). Fix: update all ~18
implementers to the new signature, forwarding the token into their own I/O (mechanical, mirrors WP-7a
for `src/`). Land this first, alone, so the examples solution is green before anything else builds on
it. **This is the P8 lesson in its rawest form** — WP-7a's "every implementer (20+)" sweep counted
only `src/`.

### WP-A — RabbitMQ mandatory-publish coordinator hardening
**Tasks #30, #33, #45.** All in `RabbitMqMandatoryPublishCoordinator` / `RabbitMqClientMiddleware`
(the new WP-8 class — the riskiest new code the round-5/6 round shipped, and the review found three
issues in it).
- **#30 (leak on cancellation):** wrap the final `await tcs.Task.WaitAsync(cancellationToken)` so an
  `OperationCanceledException` also calls `Forget(tag, messageId)` before rethrowing. Confirmed leak
  via executed probe.
- **#45 (unbounded await):** the fix that made `mandatory` real also made the caller able to hang
  forever on a stalled broker. **Decision: add an optional publish-confirm timeout** to the
  coordinator's surface (defaulted, configured via `UseRabbitMqClient`'s options), threaded into the
  `WaitAsync`. This composes with #30's cancellation fix — one `WaitAsync(linkedToken)` covers both.
  **Rejected alternative:** documenting the risk only. Rejected under P6/P9 — a reliability feature
  that can hang the caller unbounded is a boundary that must be bounded, not just annotated.
- **#33 (duplicate MessageId overwrite):** `_byMessageId[id] = pending` → `TryAdd`, and reject (throw
  at publish) a duplicate in-flight `MessageId` rather than silently misattributing a return.
  Currently unreachable through the shipped surface (middleware always stamps a fresh GUID) but the
  coordinator's public contract invites it.
Tests: the round-7 leak probe (now permanent), a timeout test, a duplicate-id test.

### WP-B — DynamoDB idempotency phantom win + fencing consistency
**Tasks #31, #51.** `src/Benzene.Idempotency*`.
- **#31 (phantom win, CONFIRMED round 8):** `TryClaimAsync`'s conflict path returns
  `ClaimResult.Won(token)` on an empty read-back without ever writing — a `Won` with no durable row,
  which defeats dedup and makes the later fenced `CompleteAsync` always no-op. **Decision: never
  synthesize a `Won` from an empty read — bounded-retry the conditional `PutItem` against the
  now-observed-absent state; only return `Won` after an actual successful write.** Invariant to
  enforce and test: *every `Won` corresponds to a durable write.* **Rejected alternative:** returning
  a distinct "won-but-unverified" status — rejected, it just relocates the same hazard the fencing
  exists to close.
- **#51 (InMemory stricter than siblings):** `InMemoryIdempotencyStore.IsLiveClaim` ANDs an
  `ExpiresAt > now` check the DynamoDb/Outbox fences deliberately omit, so a holder that merely
  outraces its own TTL gets a misleading "reclaimed by another worker" and a discarded outcome.
  Drop the `ExpiresAt > now` conjunct — token match alone, matching every sibling (P8).

### WP-C — Azure source generator: crash + diagnostics coverage
**Tasks #38, #32, #39, #40, #42.** `src/Benzene.Azure.Function.SourceGenerators` (the new WP-5 code).
- **#38 (build-crash, worth-fixing high):** `TriggerInfo.Location` is excluded from equality (for
  cache hits), but `Execute` builds the `BENZ0001` diagnostic from a possibly-stale cached `Location`
  whose `SyntaxTree` is no longer in the compilation → Roslyn throws `ArgumentException` during
  suppression-checking, **crashing the whole incremental build on an ordinary edit** (reproduced
  twice). **Decision: don't feed a cache-boundary-crossed `Location` into `Diagnostic.Create` —
  re-resolve the location freshly at report time** (or restore per-transport `RegisterSourceOutput`,
  which also fixes the incrementality regression the same review flagged — see below). The CosmosDb
  `BENZ0002` path shares the hazard; fix both.
  - *Incrementality regression (same root, note in the same commit):* WP-5's merge-all-transports-
    into-one-`RegisterSourceOutput` means any single trigger edit re-emits every trigger class.
    Restoring per-transport registration fixes #38's blast radius **and** the lost incremental
    granularity together — preferred over re-resolving `Location` alone.
- **#32 (collision masked by a sibling diagnostic):** the name-collision `GroupBy` runs *after*
  filtering out triggers that carry their own `PendingDiagnostic`, so a collision where one side is a
  broken CosmosDb trigger reports only `BENZ0002` and ships the other under the shared name. Check
  collisions across the *full* declared set before filtering, so both fire together.
- **#39 (five transports unvalidated):** only CosmosDb got `BENZ0002`; ServiceBus/EventHub/Kafka/
  QueueStorage/BlobStorage silently emit `Trigger("")` for a missing required field. **Extend the
  `DiagnosticDescriptors` table (BENZ0003+) to every transport's required field** — this is the
  P8 completion of WP-5.
- **#40 (empty/whitespace Name):** `AttributeReading.NamedString` accepts an explicitly-set `""`/`"   "`
  Name across all 9 transports → `[Function("")]`. Reject an explicitly-empty name (distinct from the
  absent case, which correctly defaults).
- **#42 (ServiceBus queue-vs-topic):** silently prefers `QueueName` when both queue and topic are set;
  emit a diagnostic rather than discard the topic. Minor.

### WP-D — Mesh host & UI robustness
**Tasks #34, #35, #36, #37, #72.**
- **#34 (P5, second overflow path):** `MeshTimeRangeResolver.ParseBound`'s `now ± span` throws
  `ArgumentOutOfRangeException` (`DateTimeOffset` range) for a count valid for `TimeSpan` but too large
  for the date — a *different* code path from the already-fixed `ParseDuration` overflow (#22), crashing
  `mesh:query:fleet`/`correlation` unconditionally. Wrap in the same "absent, never throw" contract
  (clamp or catch). Live-verified.
- **#35 (chunked-transfer size bypass, security-relevant, P9):** `MeshDispatchGuardMiddleware`'s
  `ContentLength()` returns 0 when the header is absent, so a chunked `Transfer-Encoding` request sails
  past the 128 KiB cap into the dispatch handler — the guard's own threat model (a compromised session)
  defeated on the bare-Kestrel host. **Decision: bound the body while reading it** (a bounded stream
  read / `IHttpMaxRequestBodySizeFeature`), not by trusting `Content-Length`; additionally set Kestrel's
  `MaxRequestBodySize` on the host as defence-in-depth. Live-verified (413 with CL vs 404 bypass with
  chunked).
- **#36 (logout swallows failures):** the mesh UI's sign-out `fetch` has no `response.ok` check — a
  failed logout looks identical to success. Add the status check the sibling refresh/dispatch actions
  already have, surface an error state. (Fix ships with WP-1's logout feature it belongs to.)
- **#72 (unhandled store read, worth-fixing):** `MeshArtifactMiddleware.HandleAsync` calls
  `_store.TryReadAsync` with no try/catch, but all three cloud stores *deliberately* re-throw on
  non-404 — so a transient S3/Blob/GCS hiccup becomes a raw 500 on the dashboard's primary read path.
  Wrap in try/catch → clean 503 + generic body + server log, matching the convention the same package
  already uses in `MeshRefreshGuardMiddleware`/`MeshAnnotationPublisher`. Live-verified.
- **#37 (inert `DispatchRole`, P6):** `DispatchRole` settable while `dispatch.enabled == false` is
  inert; reject at startup or warn. Minor.

### WP-E — Mesh example parity + resolved-note correction
**Tasks #41, #73.**
- **#41:** `examples/AzureFunctionsMesh` never wires `UseMeshRefreshGuard` (unauthenticated ARM
  discovery + Blob write on an anonymous POST), and — importantly — `work/outstanding-bugs.md`'s #21
  resolution note *claims* AzureFunctionsMesh already matches the guarded posture. **Wire the guard,
  add the README disclosure, and correct the false resolved-note.** Re-verify K8sMesh/GoogleCloudMesh
  (the note names them too) in the same pass.
- **#73 (AwsMesh cross-invocation race):** the one mesh example whose two aggregation drivers
  (EventBridge schedule + on-demand refresh) run in *separate Lambda execution environments*, so the
  in-process `SemaphoreSlim` the other four rely on can't serialize them, and `S3MeshArtifactStore`
  does an unconditional `PutObject`. **Decision: `reserved_concurrent_executions = 1` on the mesh
  Lambda** (cheap platform-level serializer) **plus a documented residual-risk note** matching every
  other accepted trade-off in the codebase; an S3 conditional-write/lease is the fuller fix if the
  maintainer wants true single-flight. Adopting `MeshAggregationPass` for consistency is fine but is
  *not sufficient alone* (it wouldn't cross the invocation boundary) — record that explicitly so a
  future agent doesn't "fix" it by just adding the semaphore.

### WP-F — Mesh fleet/usage backend fetch-isolation & bounds
**Tasks #74, #75, #76, #77, #78, #79.** First dedicated review of these six backend adapters; the theme
is P5 (never throw on a query) applied to external tracing/monitoring systems.
- **#74 (worth-fixing):** `CompositeMeshFleetReadModel.TraceAsync`/`CorrelationAsync` forward to the
  trace source with no try/catch, unlike the sibling `RecentFlowsAsync`/`TopicsFromUsageAsync` which
  degrade-to-empty — and the trace-source docs *promise* the composite catches. Add the same
  catch-to-null (P8). Live-verified.
- **#75 (worth-fixing):** `TempoServiceGraphTopologyBuilder.BuildAsync`'s 5 sequential PromQL calls
  have no fetch isolation; a Prometheus hiccup takes down `mesh:topology`. Catch per-query (degrade
  that edge) or once (partial topology). Live-verified.
- **#76 (worth-fixing, verify API limit):** `XRayTraceSource` never chunks a correlation window against
  `GetTraceSummaries`' time-range cap, and the *default* `CorrelationLookback` (24h) likely exceeds the
  commonly-cited 6h ceiling. Verify the real limit against a live account/authoritative source, then cap
  the default or chunk the window (as `BatchGetTraces` already chunks its id batch).
- **#77 (needs verification):** the early-stop pagination heuristic (`>= limit*4`) plus client-side
  top-N could surface stale traces as "recent" *if* `GetTraceSummaries` returns ascending-time pages.
  Confirm page ordering against a live account before acting; if confirmed, page to window exhaustion
  (bounded) or rely on a documented order guarantee.
- **#78 (minor, P9):** `LogsQueryUsageQuery` interpolates config into KQL with no escaping — config-time
  only today (no caller input reaches it), same class as the open `[DECISION]` CRLF item. Note for
  symmetry; escape when touched.
- **#79 (minor/perf):** `JaegerTraceSource` fans out one sequential GET per discovered service,
  uncapped — parallelize with a cap.

### WP-G — AWS client / health-check consistency (P8)
**Tasks #43, #44.**
- **#43 (worth-fixing, live-confirmed):** `AwsLambdaBenzeneMessageClient`'s fire-and-forget (`Event`)
  path unconditionally returns `Accepted` regardless of the invoke's actual `StatusCode` — the *same*
  bug WP-6 fixed in the sibling `UseAwsLambda<T>()` pipeline, left unswept. Mirror WP-6(a): classify a
  non-2xx `Event` invoke (and a `FunctionError` on a request-response invoke) as a failure. Either throw
  from `AwsLambdaClient.SendMessageAsync` on non-2xx `Event` (symmetric with its existing
  `FunctionError` throw) or surface the status so the client classifies — pick the throw, it flows
  through the existing catch. Test confirmed the bug live.
- **#44 (minor):** `AwsLambdaHealthCheck`/`StepFunctionsHealthCheck` keep the internal
  `Task.WhenAny`+`Task.Delay` guard WP-7(b) removed from `SqsHealthCheck`; delete it, rely on the
  processor's uniform timeout (P8 completion of WP-7b).

### WP-H — CI-gating tools & codegen correctness (P10, P11)
**Tasks #46, #65, #66, #67, #86, #87.**
- **#46 (high, P10):** `EventServiceDocumentDeserializer` reuse corrupts the "current" schema when two
  documents share a schema name — so `benzene diff` **and** `SchemaCompatibility.EnsureBackwardCompatible
  (string,string)` report zero changes on a real breaking change. Root cause: the builder/repository
  isn't reset per `Deserialize()` and `AddSchema` is first-write-wins. **Decision: make `Deserialize()`
  build a fresh `EventServiceDocumentBuilder`/`SchemaBuilder`/`SchemaRepository` per call** — safe to
  call more than once on one instance (fixes both callers at the source). Live-verified via the real CLI.
- **#65 (high, P10):** `benzene healthcheck` prints the body and never inspects `isHealthy` — never
  fails CI on an unhealthy target, unlike its already-fixed `diff`/`profile-check` siblings (P8+P10).
  Parse `isHealthy`, throw a `HealthCheckFailedException` on false (gated by a default-on `--fail-on`).
- **#66 (P11):** `benzene build` emits `Task<IBenzeneResult<>>` (uncompilable, CS1031) for a top-level
  `oneOf` response — `CSharpTypeName.GetName` has no `oneOf` branch though the property-level
  `OpenApiSchemaCSharpTypeBuilder.GetTypeName` does. Add the branch / share the logic. Live-verified.
- **#67:** `benzene build` emits `Order-id` (uncompilable) for a non-identifier property name —
  `CSharpNameFormatter.Format` never calls the existing `CodeGenHelpers.RemoveNonIdentifierCharacters()`
  that topic-name formatting uses. Wire it in. Live-verified; reachable from cross-language specs.
- **#86 (P11):** `CodeGen.Markdown`'s `MarkdownTypeBuilder` renders a `oneOf` property as blank (same
  gap as #66, different generator). Add a `oneOf` branch. Live-verified.
- **#87:** `CodeGen.ApiGateway` emits duplicate-key YAML + a `'GET,GET,OPTIONS'` CORS header when two
  topics share a method+path — group/dedupe by method (and `.Distinct()` the CORS verbs), or fail
  loudly like `ReflectionHttpEndpointFinder` does. Live-verified.

### WP-I — gRPC null-response diagnostic symmetry
**Task #48 (minor).** WP-4's null-payload `Activator.CreateInstance<TResponse>()` skips the "is this a
real protobuf message" check the non-null branch has, so a non-protobuf `TResponse` gives an opaque
`MissingMethodException` instead of the clear `BenzeneException`. Unreachable via generated code (always
has a parameterless ctor) but route the null branch through the same checked path for symmetry.

### WP-J — Schema comparer discriminator matching + coverage
**Tasks #53, #49.**
- **#53 (high, CONFIRMED round 8, both comparers):** when a discriminator mapping's *coverage* of a
  `$ref` variant changes between baseline and current, `VariantKey` produces `disc:X` on one side and
  `ref:X` on the other for the same logical variant → a spurious `UnionVariantRemoved`+`Added` pair,
  classified **Breaking** in either direction — a harmless additive mapping edit fails a compatibility
  gate. **Decision: prioritize `$ref` target name for matching whenever a `$ref` is present**; reserve
  the discriminator-value key for inline (non-`$ref`) variants only. Apply to *both* twin comparers
  (`SchemaCompatibilityComparer` + `JsonSchemaComparer`), shared-corpus test enforces parity.
- **#49 (minor):** `oneOf`+`allOf`-both-present and nested-union cases are correct today but untested;
  add two corpus cases so a future edit can't silently break them.

### WP-K — Health-check cancellation classification (P8 completion of WP-7)
**Task #50 (worth-fixing).** WP-7a's own requirement that every `IHealthCheck` forward the token made
the cancellation-swallowing bug — which a prior round fixed *only* in `TcpHealthCheck` because it was
then "the only check that both accepts and forwards a token" — newly reachable in ~10 other backend
checks (`Sns`/`Sqs`/`EventBridge`/`ServiceBus`/`EventHub`/`QueueStorage`/`Kafka`/`RabbitMq`/`Grpc`/`Http`/
`DynamoDb`). Each now catches the generic `Exception` (incl. `OperationCanceledException`) and
misclassifies a timeout/shutdown as a transient dependency failure. **Decision: fix the class once —
add the `OperationCanceledException` re-throw inside `HealthCheckError.Classify` (or a shared decorator)
so every caller gets it for free**, rather than N per-check edits. Live-verified against the real
processor.

### WP-L — Serialization: Avro DoS + evolution, XML BOM, Newtonsoft divergence
**Tasks #56, #57, #58, #59.**
- **#56 (high, security/DoS, P9, confirmed by two independent agents):** Avro serialize/deserialize
  recurses unbounded on a self-referencing/deeply-nested schema → an *uncatchable* CLR stack overflow
  that kills the whole process from a crafted <100 KB body; `BoundedBinaryDecoder` guards heap
  allocation but not the call stack. **Decision: track a depth counter at the `BoundedBinaryDecoder`
  layer** (the crash is inside Apache.Avro's reader, *before* Benzene's converter runs, so the guard
  must live at the decoder, not `AvroDatumConverter`) **and throw a catchable exception past a
  configurable max depth**, mirroring `MessagePackSecurity.UntrustedData`'s depth-500 cap.
- **#57 (high, silent data corruption):** Avro has zero schema-evolution support — a removed middle
  field silently reads the *next* field's bytes into the wrong property (no error), a reordered field
  throws an opaque `IndexOutOfRangeException`, yet `CLAUDE.md` markets the package on Avro's
  schema-evolution reputation. **Decision (two parts): (1) immediately correct the `CLAUDE.md`
  over-claim** to state plainly "no schema evolution — reader and writer must share the exact
  reflected/registered schema" (this half is urgent — it actively misleads); **(2) reject
  field-count/order mismatches loudly** in the reflection path rather than silently mis-reading.
- **#58 (minor):** `XmlSerializer` rejects a valid UTF-8-BOM-prefixed body that ASP.NET Core's
  `StreamReader` path accepts — strip a leading U+FEFF in `Deserialize(Type,string)` (one guard, fixes
  every transport).
- **#59 (minor):** Newtonsoft (`{"d":"NaN"}`) and the default STJ serializer (throws) diverge on
  `NaN`/`Infinity` doubles. **Decision: configure `JsonNumberHandling.AllowNamedFloatingPointLiterals`
  on the STJ default so it doesn't throw, and document the cross-engine divergence** in
  `NewtonsoftJson/CLAUDE.md`.

### WP-M — Validation: JsonSchema parity
**Task #60 (worth-fixing).** `Benzene.JsonSchema`'s default provider silently ignores
`System.ComponentModel.DataAnnotations` attributes (`[Required]`/`[Range]`/`[MinLength]`), so a DTO that
validates under `DataAnnotations`/`FluentValidation` gets a type-shape-only check under JsonSchema — much
weaker, with no warning. **Decision: at minimum document the gap prominently in the package README**;
the fuller fix projects common DataAnnotations attributes into schema keywords during generation. (Also
informs WP-P: all three validation adapters share the short-circuit *contract* but not the *coverage* —
the abstraction shouldn't imply interchangeability it can't deliver.)

### WP-N — Resilience & correlation
**Tasks #61, #62, #63, #64.**
- **#61 (worth-fixing):** nested `UseTimeout` — an *outer* deadline firing while inside an *inner* wrap
  escapes as a raw `OperationCanceledException`, not the `TimeoutException` the docs promise, because
  the catch filter compares only against *this* layer's `cts.Token`. **Decision: recognize "a timer in
  this nesting fired and it wasn't the true host token"** — drop the exact-token match, gate on
  `!original.IsCancellationRequested` (or compare against the deepest live token captured at entry).
  Live-verified; distinct from the known flaky test (which was confirmed pure timing noise).
- **#62 (worth-fixing, P8):** `IdempotencyMiddleware` never threads the ambient token into
  `IIdempotencyStore` — the same gap WP-7(c) fixed for ClaimCheck, missed for this sibling middleware.
  Resolve `ICancellationTokenAccessor`, pass `.CancellationToken` into all three store calls.
  Live-verified.
- **#63 (worth-fixing, doc/footgun):** the Polly circuit breaker is safe with Polly's default
  `ShouldHandle`, but the repo's own test/doc pattern (`Handle<Exception>()`) trips it on caller
  cancellation. Add a `PredicateBuilder` helper (or doc callout) that excludes
  `OperationCanceledException`, mirroring `RetryMiddleware`'s documented default.
- **#64 (worth-fixing, security, P9):** correlation-id from an inbound header flows unsanitized (no
  length cap, no control-char strip) into log scopes and outbound headers — a caller-controlled
  CRLF/log-injection + unbounded-length vector, and the concrete reachability case the open `[DECISION]
  CR/LF header injection` item said wasn't yet confirmed. **Decision: cap length and strip/reject
  control chars in `InboundCorrelationIdMiddleware` (or `CorrelationId.Set`), falling back to the
  self-generated GUID** — and close the `[DECISION]` item with this as the confirming instance.

### WP-O — Observability & privacy-doc accuracy
**Tasks #54, #55.**
- **#54 (worth-fixing):** `UseW3CTraceContext`'s manually-started `"W3CTraceContext.Root"` server span
  is never marked `Error` on a thrown exception (bare `using`, no try/catch) — the highest-visibility
  span, the one OTel backends key error-rate metrics off. Apply the same `AddException`+`SetStatus(Error)`
  pattern the earlier `UseTimer` fix used. Live-verified. (Note the related, lower-confidence
  `ActivityMiddlewareDecorator.Tag()`-before-try gap in the same commit.)
- **#55 (worth-fixing, privacy-doc):** `docs/privacy-and-data-handling.md` falsely claims Benzene emits
  no framework log lines without opt-in middleware — `MessageRouter` logs unconditionally, and the
  unsuccessful-result warning interpolates handler-authored `result.Errors` text. Correct the doc to
  match `diagnosing-failures.md` and flag the `result.Errors` content. Live-verified.

### WP-P — Core version-blindness (shared root cause)
**Tasks #69, #70.** Both are the same root cause: consumers resolve the handler via a bare
version-less `GetTopic(context)` + `FindHandler(topic)`, never consulting `IMessageVersionGetter`, so
for a topic with 2+ handler versions they pick the wrong (max-ordinal) version.
- **#69 (worth-fixing, functional):** `Default`/`SuppliedJsonSchemaProvider` validate a valid v1 request
  against v2's schema → reject valid traffic.
- **#70 (worth-fixing, observability):** `ActivityMiddlewareDecorator`/`EnrichmentExtensions`/
  `XRayMiddlewareDecorator` stamp `benzene.handler` = the wrong (never-run) handler and `benzene.version`
  = blank on every multi-version topic — undermining the mesh trace-backed flow reconstruction.
**Decision: give these consumers the same version-augmentation `MessageRouter` already does (inject
`IMessageVersionGetter<TContext>`, combine with the topic before `FindHandler`), centralized behind
`IMessageGetter<TContext>` so every consumer gets it for free** — one fix, both findings. WP-P is where
the round-10 abstractions review (#96) may confirm the root cause is the abstraction *shape* inviting
the mistake; if so, the centralized fix is exactly the structural correction, and that agent's finding
folds in here.

### WP-Q — Autofac DI adapter parity (P8 — the alternate container must match the reference)
**Tasks #82, #83, #84, #85.** First-ever review of `Benzene.Autofac`; three high-severity confirmed bugs
where it diverges from the reference `Benzene.Microsoft.Dependencies`.
- **#82 (high):** `IsTypeRegistered` reads `ComponentRegistryBuilder` before `Build()`, so it always
  returns false → **every `TryAdd*` silently becomes last-write-wins `Add*`**, breaking the idempotency
  contract `AddMessageHandlers`' finder-lock-in fix depends on (start-up checks run twice). **Decision:
  track registered types in an explicit `HashSet<Type>` maintained by every `AddXxx`**, mirroring
  Microsoft's live-collection check.
- **#83 (high/critical):** `CreateServiceResolverFactory()` calls `ContainerBuilder.Build()`, which
  throws on a second call — and `GrpcMethodHandlerFactory.Create()` calls it on *every* gRPC request →
  **gRPC is unusable with Autofac past the first request.** **Decision: build the `IContainer` once
  (lazily); `CreateServiceResolverFactory()` returns cheap repeatable factories over it**, matching
  Microsoft's model.
- **#84 (medium/high):** a constructor-injected `IServiceResolver` can't produce its own
  `IServiceResolverFactory` (null field) → opaque `InvalidOperationException` instead of
  `BenzeneResolutionException`. Give the adapter a lazy fallback (Microsoft's `??=` pattern).
- **#85 (minor):** `AutofacServiceResolverFactory` doesn't implement `IAsyncDisposable` — add it for
  symmetry/`await using`.

### WP-R — Testing infrastructure & the coverage blind spot
**Tasks #80, #81.**
- **#81 (worth-fixing, process — the meta-finding):** seven `*.TestHelpers` packages have **zero
  coverage from `Benzene.sln`'s own test suite** — they're exercised only by `templates/**` (not in the
  solution) against *published NuGet packages*, not current `main`. This is the blind spot that let #80
  (and #68, WP-U) land unnoticed, and it silently weakens every prior round's "clean" verdict for
  anything in those packages. **Decision: bring these packages into the baseline** — either add the
  templates' consuming tests to `Benzene.sln` against source, or add a minimal smoke-test project
  referencing all seven, so future rounds/CI actually cover them.
- **#80 (worth-fixing):** `AsQueueStorageBenzeneMessage(serializer)` serializes the *whole envelope*
  with the caller's serializer (crashes for XML/non-JSON), unlike the correct sibling
  `AsEventHubBenzeneMessage`. Match the sibling: envelope via fixed JSON, `serializer` on `Body` only.
  Live-verified — lives exactly in #81's blind spot.

### WP-S — Hosting / HTTP adapters
**Tasks #88, #89, #90, #91.**
- **#88 (worth-fixing):** `BenzeneHostedServiceAdapter` never observes whether the wrapped worker's task
  faulted — a dead consumer loop leaves the process "up" with zero signal, unlike
  `BackgroundService`'s `BackgroundServiceExceptionBehavior`. **Decision: observe the executing task's
  exception (log at minimum; consider `IHostApplicationLifetime.StopApplication()` to match modern
  `BackgroundService` default).** Live-verified.
- **#89 (worth-fixing, security-adjacent):** `ApiGatewayHttpRequestAdapter` (v1) never normalizes header
  casing (raw case-sensitive AWS dict), unlike AspNet/Azure/ApiGateway-v2 — so `authorization`/`origin`/
  `cookie` lookups by auth/CORS middleware silently miss on a v1-triggered request unless `.AsLowerCase()`
  was remembered. Normalize at the adapter (P8), like the other three. Zero test coverage today.
- **#90 (worth-fixing):** repeated query-string keys bind first-value on AspNet vs last-value on API
  Gateway — pick one policy and apply uniformly.
- **#91 (minor):** `ReflectionHttpEndpointFinder`'s duplicate-route check is case-sensitive while the
  router matches case-insensitively → a case-differing duplicate is silent dead code instead of the
  documented fail-fast. Case-fold before grouping.

### WP-T — MapReduce / SchemaRegistry / ResponseEvents
**Tasks #92, #93, #94, #95.**
- **#92 (worth-fixing):** `ScatterGatherAsync` discards all per-shard exception detail (`Outcome.Failed`
  carries only the shard; the thrown exception has null inner) → "which shard failed and why" is
  undiagnosable. Carry `Exception?`/reason per failed shard.
- **#93 (minor, P9):** `InMemorySchemaRegistryClient.RegisterAsync` throws a raw `ArgumentNullException`
  on a null `Subject`; guard `Subject`/`Schema` non-null/empty in `SchemaDefinition`'s ctor.
- **#94 (minor, doc):** `MapCrudConvention()` overlapping an explicit `Map` double-publishes the same
  event topic — documented fan-out semantics, but add a `MapCrudConvention()` doc callout warning
  against overlap.
- **#95 (minor, doc):** `SchemaRegistrySerializer.Deserialize` discards the embedded Confluent schema id
  without validating it against the caller's type — consistent with its framing-only scope; add a
  one-line doc callout.

---

## 3. Implementation plan

**Preconditions.** Base: `origin/main` (currently `4657c9d`). Reconfirm baselines before/after:
`Benzene.Test.dll` ~2900 passed / 2 skipped / 0 failed; `Benzene.Mesh.Test` 512; `Benzene.Mesh.Host.Test`
136; **`Benzene.Examples.sln` builds clean** (this is #68 — currently RED; WP-U turns it green and it
then joins the baseline per #81). Known flake: `TimeoutMiddlewareTest.HandleAsync_NestedUseTimeout_…` —
re-run in isolation before treating as a regression (confirmed pure host-contention noise, round 8).

**Sequencing.**
1. **WP-U first, alone** — it fixes the live build regression in `Benzene.Examples.sln`; land it before
   anything else so the examples solution is a usable baseline.
2. **WP-K, WP-B, WP-P** early — each touches a shared seam multiple packages depend on
   (health-check classification; the idempotency store family; the version-augmentation seam). Landing
   them first keeps the other packages' worktrees conflict-free, same reasoning as WP-7 in the prior
   round.
3. **The rest (WP-A, C–J, L–T) in parallel worktrees**, one agent each, disjoint projects. Cap
   concurrency to what the shared 4-core host tolerates (2–3 building at once; contention, not code, is
   the usual cause of stalls — every prior round saw this). Merge order among the parallel set is
   unconstrained; each merges to `main` when green, resolving the mechanical
   `outstanding-bugs.md`/`capability-matrix.md` append-conflicts by keeping both sections.

**Per-package definition of done** (docs lifecycle is part of done, per `AGENTS.md`): revert-verified
red→green test for every code fix; XML/contract docs + the named `docs/*.md` pages +
`docs/capability-matrix.md` updated in the same package; `[RESOLVED]` line per finding in
`outstanding-bugs.md` pointing here; `TaskUpdate` each covered task → `completed`; commits scoped
one-logical-change; push to `origin/main` (retry w/ backoff per repo convention).

**Doc-only / doc-first items** that should NOT wait for a full code fix (land as small doc commits
promptly, they actively mislead today): the Avro `CLAUDE.md` over-claim (#57 part 1), the privacy-doc
correction (#55), the `outstanding-bugs.md` #21 resolved-note correction (#41), the JsonSchema-coverage
README note (#60).

**Round completion:** full-suite + mesh + templates + **examples** baselines green; docs-archivist
moves this file to `work/archive/` (stamped, with the landing commits); capability-scribe pass;
`outstanding-bugs.md`'s open `[DECISION] CR/LF header injection` item closed by #64 (WP-N).

**Amendment rule (repeat):** an implementing agent that finds a design here doesn't survive contact
with the code amends this document's section in the same commit as the divergent implementation — the
record and the code never disagree.

---

## 4. Task-number index

| Task | WP | Decision in one line |
|---|---|---|
| #68 | U | Update ~18 example `IHealthCheck` impls to `ExecuteAsync(CancellationToken)` — land first (build is RED) |
| #30 | A | Cancellation while awaiting → `Forget()` the pending publish |
| #33 | A | `TryAdd` + reject duplicate in-flight MessageId |
| #45 | A | Add a publish-confirm timeout; don't await unbounded |
| #31 | B | Never synthesize `Won` from an empty read — bounded-retry the PutItem |
| #51 | B | Drop the extra `ExpiresAt>now` conjunct; token-only, like siblings |
| #38 | C | Don't feed a cache-stale `Location` to `Diagnostic.Create` (restore per-transport registration) |
| #32 | C | Check name collisions before filtering out pending-diagnostic triggers |
| #39 | C | BENZ0003+ required-field validation for the other 5 transports |
| #40 | C | Reject explicitly-empty/whitespace trigger `Name` |
| #42 | C | Diagnostic when both QueueName and TopicName set |
| #34 | D | `ParseBound` DateTimeOffset overflow → absent, never throw |
| #35 | D | Bound the dispatch body while reading; don't trust Content-Length |
| #36 | D | Logout `fetch` checks `response.ok`, surfaces error |
| #72 | D | Wrap artifact-store read in try/catch → clean 503 |
| #37 | D | Reject/warn inert `DispatchRole` when dispatch disabled |
| #41 | E | Wire AzureFunctionsMesh refresh guard + correct the false resolved-note |
| #73 | E | AwsMesh: reserved_concurrent_executions=1 + documented residual risk |
| #74 | F | `TraceAsync`/`CorrelationAsync` catch-to-null like siblings |
| #75 | F | Fetch-isolate Tempo topology's 5 PromQL calls |
| #76 | F | Chunk/cap X-Ray correlation window (verify the real limit) |
| #77 | F | Verify X-Ray page order; page to exhaustion if ascending |
| #78 | F | Escape KQL interpolation (config-time today) |
| #79 | F | Parallelize+cap Jaeger per-service fan-out |
| #43 | G | Classify Lambda `Event` invoke outcome — sweep WP-6's sibling |
| #44 | G | Delete redundant internal timeout guard — sweep WP-7b's siblings |
| #46 | H | Fresh deserializer per `Deserialize()` call — fixes diff + EnsureBackwardCompatible |
| #65 | H | `benzene healthcheck` throws on `isHealthy:false` |
| #66 | H | C# client generator handles top-level `oneOf` |
| #67 | H | C# client generator sanitizes non-identifier property names |
| #86 | H | Markdown generator handles `oneOf` |
| #87 | H | ApiGateway generator dedupes method+path / CORS verbs |
| #48 | I | Route gRPC null branch through the checked path for a clear error |
| #53 | J | Match `$ref` variants by ref-name, not discriminator coverage |
| #49 | J | Add oneOf+allOf / nested-union corpus cases |
| #50 | K | Re-throw `OperationCanceledException` in `HealthCheckError.Classify` — sweep the family |
| #56 | L | Avro depth cap at the decoder → catchable exception (DoS) |
| #57 | L | Correct the Avro schema-evolution over-claim; reject shape mismatches |
| #58 | L | Strip leading U+FEFF in XmlSerializer |
| #59 | L | STJ `AllowNamedFloatingPointLiterals`; document NaN/Infinity divergence |
| #60 | M | Document JsonSchema's DataAnnotations gap (project attributes as fuller fix) |
| #61 | N | Nested-timeout catch filter recognizes any in-nesting timer |
| #62 | N | Thread ambient token into IIdempotencyStore — sweep WP-7c's sibling |
| #63 | N | Cancellation-safe `ShouldHandle` helper/doc for the circuit breaker |
| #64 | N | Sanitize/bound inbound correlation id; closes the CRLF `[DECISION]` |
| #54 | O | Mark `W3CTraceContext.Root` span Error on throw |
| #55 | O | Correct the privacy doc's "no framework logging" claim |
| #69 | P | Version-augment JsonSchema providers |
| #70 | P | Version-augment tracing/log diagnostics — shared seam with #69 |
| #82 | Q | Autofac `IsTypeRegistered` via explicit HashSet (fixes TryAdd*) |
| #83 | Q | Build Autofac container once; repeatable factories (fixes gRPC) |
| #84 | Q | Lazy `IServiceResolverFactory` fallback on the Autofac adapter |
| #85 | Q | `IAsyncDisposable` on AutofacServiceResolverFactory |
| #81 | R | Bring the 7 uncovered TestHelpers packages into the baseline |
| #80 | R | Fix `AsQueueStorageBenzeneMessage` to match its sibling |
| #88 | S | Observe the hosted worker's faulted task |
| #89 | S | Normalize header casing in ApiGateway v1 adapter |
| #90 | S | Uniform repeated-query-key policy across transports |
| #91 | S | Case-fold the duplicate-route check |
| #92 | T | Carry per-shard failure reason in ScatterGather |
| #93 | T | Guard null Subject in SchemaDefinition |
| #94 | T | Doc callout on MapCrudConvention overlap |
| #95 | T | Doc callout on SchemaRegistrySerializer id non-validation |

*(Round-10 findings, task #96's agents, will be appended to §2 and this index as they report.)*
