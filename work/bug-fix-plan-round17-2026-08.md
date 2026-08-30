# Bug-fix plan — round 17 (2026-08)

**Status: READY FOR EXECUTION — not yet started.** Covers task board **#271–#291** (21 findings from
the round-17 review pass, all evidence-backed). Source review docs, all at `work/`:

- `review-round17-reliability-2026-08.md` (#271, #272)
- `review-round17-aws-deep-2026-08.md` (#273, #274, #275)
- `review-round17-azure-deep-2026-08.md` (#276, #277)
- `review-round17-validation-serialization-2026-08.md` (#278, #279)
- `review-round17-grpc-healthchecks-2026-08.md` (#280, #281, plus one doc-only gap folded into WP-G)
- `review-round17-cli-registry-2026-08.md` (#282)
- `review-round17-mesh-composition-2026-08.md` (#283, #284, #285)
- `review-round17-auth-security-2026-08.md` (#286, #287)
- `review-round17-performance-2026-08.md` (#288, #289, #290, #291)

Every finding carries an executed reproduction in its review doc (a red test run against the real
assemblies at `4389bfb`, reproduced inline where the test file was deleted). Several review docs also
contain copy-paste-ready test code — the recipes below point at them rather than restating everything.

**Round-17-specific context the fixer must internalize:** four of these findings (#288, #289, #290,
#283/#284's sibling gaps) are regressions or omissions **in round 16's own fixes**. When fixing them,
the round-16 regression tests for the original fixes (#251, #256, #266, #267) must all stay green —
these WPs repair the fix without undoing what it fixed.

## Task board mapping

| WP | Tasks | Area |
|----|-------|------|
| A | #288, #289 | Round-16 fix regressions: Polly guard counter leak + disposal-bridge deadlock |
| B | #283, #284, #290 | Mesh collector: version-aware drift detector, RecentFlows cancellation, descriptor eviction |
| C | #285 | Benzene.Http: thread the real request token into the nested BenzeneMessage envelope |
| D | #276, #277 | Azure workers: Cosmos skip-mode checkpoint guard, ServiceBus settlement isolation |
| E | #273, #274, #275 | AWS/GCP transports: Kinesis checkpoint prefix-watermark, XRay dedup, Pub/Sub null-result |
| F | #278, #279 | Avro: map support + runtime-typed union branch resolution |
| G | #280, #281 (+doc gap) | gRPC: mid-stream error classification + health-bridge IsNonCritical parity |
| H | #271, #272 | DynamoDB stores: transact-item accounting, expiry-boundary agreement |
| I | #291 | SelfHost: CompositeBenzeneWorker fault-racing startup |
| J | #282 | CLI: benzene healthcheck non-JSON-body tolerance |
| K | #286, #287 | Auth hardening: repeated-block signing-key check, topic-anchored dispatch-role gate |

## Execution protocol (standard — same as rounds 11–16)

1. **One isolated git worktree per work package**, all detached from the same base commit on `main`
   (record it at kickoff). `git worktree add --detach <path> <commit>`. **Never `git stash`.**
2. **Red first**: reproduce with the recipe (most are copy-paste from the review doc), confirm it
   fails/reproduces, then fix, then green. Keep tests as permanent regression tests.
3. **Scoped builds only** — build/test the specific test project, never the whole solution, while
   other WPs run in parallel (the host OOM-kills concurrent full-solution builds; verified every
   round). The coordinator runs ONE centralized full baseline (full `Benzene.sln` build,
   `Benzene.Core.Test`, `Benzene.Mesh.Test`, `Benzene.Mesh.Host.Test`, `Benzene.Examples.sln` build)
   after the last merge — rounds 15 and 16 each caught real integration bugs only at that step.
4. **Subagents cannot receive background-task notifications.** Run every build/test as a single plain
   foreground Bash call; never use run_in_background or Monitor-style polling.
5. **Definition of done per WP**: fix + regression tests green + dated `[RESOLVED]` entries appended
   to `work/outstanding-bugs.md` (immediately before `## Open — maintainer decisions`; the
   coordinator resolves the identical-shaped merge conflicts mechanically) + the relevant
   `docs/capability-matrix.md` row(s) updated. Commit with a clear message citing the task numbers.
6. **Coordinator merges sequentially**, hand-reconciling `capability-matrix.md` conflicts (WP-B/C
   both touch the "Mesh — collector" row's neighborhood — see coordination notes), then runs the
   centralized baseline, then pushes.

---

## WP-A — Round-16 fix regressions: Polly + disposal bridge (#288 high, #289 high)

**Files:** `src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs`,
`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverAdapter.cs`,
`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs`, tests in
`test/Benzene.Core.Test/Resilience/` and `test/Benzene.Core.Test/Core/Core/DI/`.

**The findings.** (#288) The round-16 #267 re-entrancy guard does `Interlocked.Increment` *before*
the `try`/`finally` that decrements; a guard-rejected concurrent attempt throws without ever pairing
its increment, permanently poisoning the per-call counter — a later, fully sequential attempt in the
same `HandleAsync` (e.g. an outer `Retry` retrying after the rejected race) is then wrongly rejected
with the same "concurrent-attempt" `NotSupportedException`. (#289) The round-16 #266
deliberately-unbounded `.AsTask().GetAwaiter().GetResult()` disposal bridge deadlocks the calling
thread **forever** when a user's `DisposeAsync()` awaits without `ConfigureAwait(false)` under an
ambient single-thread-affinity `SynchronizationContext` (WinForms/WPF/Blazor-Server-shaped) — the
blocked thread is the only one allowed to pump the continuation. The factory variant blocks whole-app
shutdown. Both proven with reproductions given in full in `review-round17-performance-2026-08.md`.

**Rulings:**

1. (#288) Pair every increment with exactly one decrement. Minimal correct fix: when the guard
   fires, `Interlocked.Decrement` **before** throwing (the rejected attempt undoes its own count; the
   in-flight attempt's count is untouched). Do not restructure beyond that. Regression test: the
   review's `ConcurrentOnFirstRoundStrategy` (Retry wrapping a first-round-concurrent strategy) —
   green form asserts round 2's sequential attempt RUNS and the overall call succeeds (or fails only
   with round 1's own rejection, per Retry semantics — assert `sequentialAttemptRan == true` and no
   `NotSupportedException` on round 2). All 14 existing `PollyResilienceMiddleware` tests unchanged.
2. (#289) Do NOT switch to the bounded-5s pattern (that reopens #266's own "never abandon a user's
   cleanup" reasoning). Instead, prevent the blocking call from observing an ambient
   `SynchronizationContext`: in both `Dispose()` methods,
   ```csharp
   var previous = SynchronizationContext.Current;
   SynchronizationContext.SetSynchronizationContext(null);
   try { asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
   finally { SynchronizationContext.SetSynchronizationContext(previous); }
   ```
   — the standard sync-over-async mitigation; it keeps the wait unbounded (matching Autofac) while
   removing the deadlock vector. Update the surrounding comment to name the tradeoff. Regression
   test: the review's `SingleThreadAffinitySynchronizationContext` + `FakeAsyncDisposableScope`
   repro, inverted — `Dispose()` on that thread must now RETURN (join within a few seconds), and the
   async disposal must actually have run. Round-16's #266/#262 tests (async-only scoped/singleton
   disposal, DI parity) must stay green.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~PollyResilienceMiddleware|FullyQualifiedName~Microsoft|FullyQualifiedName~Cache.Redis"`.

---

## WP-B — Mesh collector: drift detector, RecentFlows, descriptor eviction (#283, #284, #290)

**Files:** `src/Benzene.Mesh.Collector/MeshCollectorStore.cs`,
`src/Benzene.Mesh.Collector/CompositeMeshFleetReadModel.cs`, tests in `test/Benzene.Mesh.Test/`.

**The findings.** (#283) `RecordObservedActivityAndDrift` (~line 591) still reads only the single
headline `ServiceState.Descriptor` (`Descriptors[CurrentVersionKey]`) — so the moment a second
version registers, every message an older-but-still-live version legitimately handles is misfiled as
`contract-drift`, reintroducing exactly the false positive #251 fixed in `HashMatches` 90 lines
above, in the same file, in the same commit. (#284) `RecentFlowsAsync` (and `TopicsFromUsageAsync`)
kept their bare `catch` — #256's token-verified filter only reached `TraceAsync`/`CorrelationAsync` —
so on `mesh:query:fleet` (the exact call #250 wired a real token into) a genuine caller cancellation
is converted into a normal empty-flows success, and `TimeoutMiddleware` (which only converts a
*thrown* OCE) reports nothing. (#290) `ServiceState.Descriptors` (new in #251) has no eviction at
all — one permanent entry per historical `ServiceVersion` forever (5,000 synthetic deploys → 5,000
retained entries, proven), degrading `HashMatches` from O(1) to unboundedly-growing O(v). All three
proven with red tests in `review-round17-mesh-composition-2026-08.md` /
`review-round17-performance-2026-08.md`.

**Rulings:**

1. (#283) `MeshTraceEvent` carries no `ServiceVersion` (a wire-shape/spec question — see the [OPEN]
   entry below), so take the review's option (b): a topic counts as **declared** if it appears in
   ANY live version's `Topics`/`Produces` for the service — matching `HashMatches`'s any-live-version
   rule exactly. Document the accepted approximation in a code comment (a real single-version drift
   on an edge another live version also declares goes undetected until that other version retires) —
   a documented trade-off, never a silent false positive. Record an `[OPEN]` maintainer-decision
   entry in `outstanding-bugs.md`: should the mesh spec's `MeshTraceEvent` wire shape grow a
   `serviceVersion` field so drift can be attributed per-version? (Cross-language spec change; not
   this repo's unilateral call.)
2. (#284) Apply #256's exact filter to `RecentFlowsAsync` AND `TopicsFromUsageAsync`:
   `catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))`.
   Backend-failure degradation to empty results stays byte-identical.
3. (#290) Adopt a bounded max-versions-per-service cap mirroring `_issues`'s existing
   bounded-with-eviction precedent in the same file (do NOT invent a heartbeat-TTL policy this
   round — that's a bigger design surface). Default cap: 8 retained versions per service
   (side-by-side deployments realistically hold 2–3 live; 8 gives generous headroom), constructor-
   configurable alongside the existing bounds. Eviction rule on inserting a NEW version key when at
   cap: evict the least-recently-registered version that (a) is not `CurrentVersionKey` and (b) has
   no live instance currently reporting its hash (check `state.Instances`); if every retained
   version has a live instance, evict the least-recently-registered non-current version anyway (the
   cap wins — log/count nothing, this is an in-memory diagnostic store). Track registration order
   (e.g. a monotonic counter per entry) to make "least-recently-registered" well-defined. **The
   evicted descriptor's edges must be retracted** (`RetractEdges`) exactly as a same-key
   re-registration does today, so topic edges don't leak. Verify `HashMatches` and the WP's own
   #283 fix then scan only the retained set (this is what actually bounds the O(v)).

**Red-green recipes.**
- #283: the review's red test verbatim (register v1 topics=[topic-a], v2 topics=[topic-b]; a v1
  event for topic-a → today files a contract-drift issue; green: `Assert.Empty(store.Fleet().Issues)`).
  Plus the positive: an event for a topic NO live version declares still files drift.
- #284: the review's red test (`CancellingTraceSource` — the fake `CompositeMeshFleetReadModelTest`
  already defines — with a cancelled token: `FleetAsync` must throw `OperationCanceledException`).
  Plus negative: a plain-exception source still degrades to empty flows.
- #290: the review's growth test inverted — register 5,000 versions, assert the retained count equals
  the cap; plus a targeted eviction test (live-instance versions survive over dead ones; current
  version never evicted; evicted version's topic edges gone from the catalog).
- Round-16 regression guard: `MeshCollectorSideBySideVersionTest` and the #253/#256 tests must pass
  unmodified.

**Verify:** `dotnet test test/Benzene.Mesh.Test -c Release` (whole project — this WP touches the
collector's core state model).

---

## WP-C — Benzene.Http: real cancellation into the nested envelope (#285 — highest priority)

**Files:** `src/Benzene.Http/BenzeneMessage/BenzeneMessageHttpMiddleware.cs` (and its DI/registration
site in the same package if constructor wiring changes), tests in `test/Benzene.Core.Test/` (or the
Http package's test home) plus an integration-shaped test; do NOT modify
`src/Benzene.Core.Middleware/MiddlewareApplication.cs`'s overloads.

**The finding.** `DispatchAsync` calls the 2-argument
`_application.HandleAsync(request, factory)` overload, which hardcodes `CancellationToken.None` and
creates a fresh inner DI scope seeded with that dead token — so `FleetQueryMessageHandler`/
`MeshDispatchMessageHandler` (and any handler on any `UseBenzeneMessage` HTTP envelope), despite
correctly resolving `ICancellationTokenAccessor` per #250/#185, never observe the real HTTP request's
cancellation. This makes #250 (and #185 before it) inert in `deploy/Mesh/Benzene.Mesh.Host` and every
AwsMesh/AzureMesh/GoogleCloudMesh example. The committed #250 test can't catch it because it
hand-shares one accessor instance instead of going through the real scope-creation path. Full trace,
including why the outer pipeline's `SeedCancellationToken` middleware doesn't help the inner scope, in
`review-round17-mesh-composition-2026-08.md` Finding 3 (red test included, currently PASSES proving
the bug).

**Ruling.** The review notes `IHttpContext` exposes no generic cancellation signal — but the fix
does not need one: `BenzeneMessageHttpMiddleware` already holds the OUTER request scope's
`_serviceResolver` (it resolves `IServiceResolverFactory` from it today), and the outer scope's
`CancellationTokenAccessor` was already seeded from `HttpContext.RequestAborted` by
`BuildHttpPipeline`'s `SeedCancellationToken` middleware. So: resolve
`ICancellationTokenAccessor` from `_serviceResolver` via `TryGetService` (null-tolerant — pipelines
without the registration keep working), and call the **3-argument**
`HandleAsync(request, factory, accessor?.CancellationToken ?? CancellationToken.None)` overload,
which already exists and already seeds the inner scope correctly. First VERIFY that claim against
`MiddlewareApplication`'s 3-arg overload source (the review asserts the 2-arg overload merely
forwards `CancellationToken.None` to it) — if the 3-arg path doesn't seed the inner accessor, fix the
seeding there too, minimally, without changing the 2-arg overload's public shape. No change to
`IHttpContext`, no change to the 2-arg overload (other transports call it legitimately).

**Red-green recipe.** Red: the review's Finding-3 test verbatim (the `MeshQueriesRoutingTest`-style
harness: outer scope with a cancelled seeded accessor → dispatch a `benzene:mesh:query:fleet`
through the real `BenzeneMessageApplication` + scoped factory → today the handler observes
`IsCancellationRequested == false`). Green: invert — the handler observes the cancelled token
(assert `IsCancellationRequested == true`, and the query is genuinely bounded, mirroring
`MeshCollectorQueryCancellationTest`'s green shape but through the REAL scope-creation path this
time). Also add the equivalent through `BenzeneMessageHttpMiddleware.DispatchAsync` itself if a
lightweight harness exists (the middleware over a stubbed `IHttpContext`); if none does, the
application-level test above is the acceptance bar. Existing `Benzene.Http`, mesh query, and dispatch
suites must stay green.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~Http"` plus
`dotnet test test/Benzene.Mesh.Test -c Release --filter "FullyQualifiedName~MeshCollectorQuery|FullyQualifiedName~MeshDispatch"`,
then `dotnet test deploy/Mesh/Benzene.Mesh.Host.Test -c Release` (this WP exists for that host).

---

## WP-D — Azure workers: Cosmos + ServiceBus (#276, #277)

**Files:** `src/Benzene.Azure.CosmosDb/BenzeneCosmosChangeFeedWorker.cs` (~line 111),
`src/Benzene.Azure.ServiceBus/BenzeneServiceBusWorker.cs` (~lines 176–214), tests in
`test/Benzene.Core.Test/Azure/CosmosDbWorker/` and `test/Benzene.Core.Test/Azure/ServiceBusWorker/`.

**The findings.** (#276) Skip mode's "checkpoint the failed batch anyway" call has no try/catch — a
lease-container failure (429 throttle) escapes `OnChangesAsync` unhandled and unlogged as a
checkpoint failure; the only prior log line blames the handler. This is the residual half of #108
that the shipped fix implemented only for the success path. (#277) Under `AckMode = Explicit`, the
handler call and `SettleAsync` share one try/catch: a settlement failure after a *successful* handler
run is logged with the handler-failure template and the catch then abandons an already-succeeded
message — the misattribution class #108/#116 fixed for Cosmos/EventHub, never applied to this third
sibling. Both proven with executed probes in `review-round17-azure-deep-2026-08.md` (which also notes
the existing similarly-named skip-mode test never actually reaches line 111 — its pipeline mock never
throws).

**Rulings:**

1. (#276) Wrap the skip-mode `await checkpointAsync();` in its own try/catch mirroring the success
   path exactly: log a distinct "checkpointing the skipped batch failed" message naming the lease
   container (not the handler), swallow (don't rethrow) — the batch stays un-checkpointed and is
   redelivered next scan, the at-least-once outcome #108's ruling already established.
2. (#277) Move `SettleAsync(settler, decision)` outside the handler's try/catch into its own: on
   settlement failure log a distinct "settling Service Bus message {messageId} failed" message, and
   for the successful-handler case do NOT additionally call `AbandonMessageAsync()` — let the lock's
   natural expiry drive redelivery. A settlement failure for a message whose handler had FAILED
   (i.e. `SettleAsync`'s own abandon/dead-letter throwing) keeps today's log-and-propagate-to-
   `OnProcessErrorAsync` behavior — only the successful-handler path changes, per the review's own
   scoping.

**Red-green recipes.** Both probes are described precisely in the review doc: (#276)
`CatchHandlerExceptions = true`, pipeline throws, `CheckpointAsyncImpl` throws → today the checkpoint
exception escapes `OnChangesAsync`; green: swallowed, distinct log message asserted via the mock
logger. (#277) handler succeeds (`MessageResult = Ok()`), `CompleteMessageAsync` throws a
lock-lost-shaped exception → today: handler-failure log template + an abandon call on the succeeded
message; green: distinct settlement-failure log, zero abandon calls. Keep the #108/#116/#117
regression tests green, and REPLACE (don't merely supplement) the misleadingly-named existing
skip-mode test's coverage with one whose pipeline mock actually throws.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~CosmosDbWorker|FullyQualifiedName~ServiceBusWorker"`.

---

## WP-E — AWS/GCP transports: Kinesis, XRay, Pub/Sub (#273, #274, #275)

**Files:** `src/Benzene.Aws.Lambda.Kinesis/KinesisStreamCheckpointer.cs` (+ its class docs and
`KinesisStreamApplication`'s doc comment if wording changes),
`src/Benzene.Mesh.Fleet.Aws.XRay/XRayTraceSource.cs`,
`src/Benzene.GoogleCloud.Functions.PubSub/PubSubMiddlewareApplication.cs` (+ `PubSubOptions` doc),
tests in the respective existing test homes.

**The findings.** (#273) The checkpointer's single monotonic index watermark lies under
`PartitionBy` (the pattern the package's own doc recommends): checkpointing A-1, A-2 (indices 0, 2)
before B's group runs advances the watermark past B-1 (index 1); when B-1 then fails,
`FirstUncheckpointedSequenceNumber` reports B-2 — B-1 is reported to AWS as handled and **never
retried** (silent data loss, proven: expected `"B-1"`, got `"B-2"`). (#274)
`GetCorrelationAsync`/`GetRecentFlowsAsync` never dedupe trace-summary ids across the 6h window
chunks whose boundaries the code's own tests prove touch exactly — a duplicated id yields two
identical `TraceView`s (proven), and in recent-flows would displace a genuinely different trace from
the top-N. (#275) `RaiseOnFailureStatus` checks `context.MessageResult?.IsSuccessful == false` — a
null result never escalates, so a message whose pipeline sets no result is silently acked and
permanently lost (worse than the AWS analog: Pub/Sub delivers one message per invocation with no
batch fallback). All proven in `review-round17-aws-deep-2026-08.md`.

**Rulings:**

1. (#273) Implement a **contiguous-prefix watermark**: track the set of confirmed original indices
   (a `bool[]`/bitset sized to the batch); `FirstUncheckpointedSequenceNumber` reports the record at
   the first UNconfirmed index (i.e. resume after the longest fully-confirmed prefix), not after the
   max confirmed index. Consequences to accept and document: under `PartitionBy`, records confirmed
   ahead of an earlier failure (A-2 in the repro) will be redelivered — the safe over-retry the
   design doc's §4 already blesses — while the genuinely-failed record is never silently skipped.
   For a plain sequential handler that checkpoints in order the behavior is byte-identical to today.
   The existing foreign-record/rewind guard semantics must be preserved (`IndexOf` reference
   matching unchanged; a confirmed index can never become unconfirmed). Update the class doc and the
   `PartitionBy` recommendation's wording to describe the prefix semantics. Red: the review's
   `PartitionByCheckpointRedTest` scenario; green: `BatchItemFailures[0].ItemIdentifier == "B-1"`.
   Existing Kinesis checkpoint tests must stay green (verify each still matches prefix semantics —
   any that asserted max-index behavior for an out-of-order confirmation pattern is asserting the
   bug and should be inverted with a comment, but expect most to be order-preserving already).
2. (#274) Dedupe by `Id` once, immediately after `FetchTraceSummariesAsync` returns, in BOTH
   `GetCorrelationAsync` and `GetRecentFlowsAsync` (first-occurrence-wins; `DistinctBy(s => s.Id)`
   or equivalent). Red/green: the review's `BoundaryDuplicateRedTest` (`Assert.Single`), plus a
   recent-flows variant asserting a duplicated id occupies one top-N slot.
3. (#275) Change the check to `context.MessageResult?.IsSuccessful != true`, matching
   `SingleContextEscalatingApplicationBase`/#229 and #260's convention. `PubSubOptions.
   RaiseOnFailureStatus`'s doc already promises exactly this ("safe-by-default") — update its
   wording only if it describes the explicit-failure-only behavior. Red: the review's
   `NullResultRedTest` (pipeline never touches `MessageResult` → must throw
   `PubSubMessageProcessingException`); keep the existing explicit-failure and success tests green.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Kinesis|FullyQualifiedName~PubSub"` and `dotnet test test/Benzene.Mesh.Test -c
Release --filter "FullyQualifiedName~XRay"`.

---

## WP-F — Avro: map support + union branch resolution (#278, #279)

**Files:** `src/Benzene.Avro/AvroDatumConverter.cs` (single file — both findings share the root
cause), `src/Benzene.Avro/CLAUDE.md` (capability wording), tests in
`test/Benzene.Core.Test/Plugins/Avro/`.

**The findings.** (#278) `ToDatum`/`FromDatum` have no `Schema.Type.Map` arm — any `map` field falls
through to the primitive default: `map<string,string>` serializes but throws `InvalidCastException`
on deserialize; a map of arrays-of-records throws `AvroException` on serialize. (#279)
`NonNullBranch` always picks the FIRST non-null branch of every union on both write and read —
correct only for 2-branch `["null", X]`; a `["null","string","long","boolean"]` union silently
coerces a `bool`/`long` through the string branch (type AND value corruption, proven — and some
combinations "round-trip" to the same wrong value, making it drift silently rather than crash). Full
repro code inline in `review-round17-validation-serialization-2026-08.md`.

**Rulings:**

1. (#278) Add `Schema.Type.Map` arms mirroring the existing Array handling: on write, convert each
   value recursively against the map's value schema (keys are strings — Avro maps are string-keyed
   by spec); on read, target `Dictionary<string, TValue>` (support `Dictionary<string,V>` and
   interface-typed `IDictionary<string,V>`/`IReadOnlyDictionary<string,V>` CLR properties),
   converting each value recursively. A non-string-keyed CLR dictionary target gets a descriptive
   `NotSupportedException` naming the Avro constraint (don't silently coerce keys).
2. (#279) Resolve the union branch by **runtime shape**, both directions. Serialize: pick the branch
   whose schema matches the value's actual CLR type (null → Null branch; then match primitives by
   CLR type against the branch's schema tag; records by the registered/target type's schema name;
   map/array by shape) — first match wins in branch-declaration order for genuinely ambiguous cases
   (e.g. int vs long branches both viable for an int value: prefer the exact-width match, else the
   wider). Deserialize: `GenericDatumReader` already decoded the wire's actual branch — select the
   branch schema by inspecting the DATUM's runtime type (GenericRecord → the record branch with the
   matching schema name; IDictionary → map branch; array → array branch; primitive → the branch whose
   tag matches), instead of discarding that information through `NonNullBranch`. The 2-branch
   nullable path must remain byte-identical — every existing Avro test (including #56/#57's
   regression tests) green unchanged.
3. Update `CLAUDE.md`'s capability description (maps now supported; multi-branch unions resolved by
   runtime type) and the capability-matrix Serialization row.

**Red-green recipes.** Re-create the review's four tests as permanent regressions:
`RoundTrips_PrimitiveValuedMap`, `RoundTrips_RecordWithinArrayWithinMap`,
`RoundTrips_BooleanValue_ThroughAThreePlusBranchUnion`, `RoundTrips_LongValue_ThroughAThreePlusBranchUnion`
(exact schemas/types given inline in the review doc). All red today; green after.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~Avro"`.

---

## WP-G — gRPC: mid-stream errors + health-bridge parity (#280, #281, + doc gap)

**Files:** `src/Benzene.Grpc/GrpcMethodHandler.cs`, `src/Benzene.Grpc/Streaming/GrpcStreamAdapter.cs`,
`src/Benzene.Grpc.AspNet/BenzeneHealthCheckBridge.cs`; doc-only:
`src/Benzene.HealthChecks.Core/IHealthCheckProcessor.cs` (XML remark) + `docs/health-checks.md`;
tests in `test/Benzene.Grpc.Test/`.

**The findings.** (#280) A server-streaming/duplex handler's async-iterator body runs during
`GrpcStreamAdapter.WriteAll` — AFTER `RunPipelineAsync` has already written the `benzene-status: ok`
trailer and returned. A mid-stream throw therefore bypasses `MessageHandler`'s classification,
`DefaultGrpcStatusCodeMapper`, and `AddRichErrorDetails` entirely, surfacing as
`RpcException(Unknown, "Exception was thrown by handler.")` **with a stale `benzene-status: ok`
trailer still attached** — actively contradicting the outcome. Proven end-to-end via TestServer for
both Subscribe and Chat shapes. (#281) `BenzeneHealthCheckBridge` reads raw `Status == Failed` with
zero `IsNonCritical` awareness, while `HealthCheckProcessor` downgrades a non-critical Failed to
Warning — the same check/state reports "serving" over HTTP and `NOT_SERVING` over grpc.health.v1
(proven), including for the always-non-critical `DependencyHealthCheck` category. Plus a doc-only
gap: `HealthCheckProcessor`'s deliberate "no ambient CancellationToken" behavior is documented only
in a private `//` comment. All in `review-round17-grpc-healthchecks-2026-08.md`.

**Rulings:**

1. (#280) For the two streaming shapes ONLY (unary/client-streaming untouched): move the
   `benzene-status` trailer write to AFTER the stream is fully drained (gRPC trailers are sent once
   at call end, so this is safe), and wrap the drain (`WriteAll` or its call sites in
   `GrpcMethodHandler`) in a try/catch that runs the SAME classification the pipeline path uses —
   map the exception the way `MessageHandler` does (`ArgumentException` → ValidationError,
   `TimeoutException` → Timeout, `OperationCanceledException` → the existing
   Cancelled/DeadlineExceeded translation, else ServiceUnavailable), then apply
   `DefaultGrpcStatusCodeMapper` + `AddRichErrorDetails` + write the FAILURE status trailer, and
   throw the resulting classified `RpcException`. Read `RunPipelineAsync`'s current status/trailer
   flow first and restructure minimally — the acceptance bar is: mid-stream throw → classified
   status code, rich details, and a truthful `benzene-status` trailer; happy-path streaming and the
   WP-4-era null-stream `RpcException(Internal)` behavior unchanged.
2. (#281) Apply `HealthCheckProcessor.RunTimedAsync`'s downgrade rule in the bridge before the
   aggregate decision (read the processor's exact rule first — including its `IsPersistent`
   qualifier — and mirror it faithfully). Duplicate the rule locally with a cross-referencing
   comment, per this package's documented no-`Benzene.HealthChecks`-reference design (precedent:
   `DuplicateTypeSuffixer`). A non-critical Failed should surface as Degraded, not Unhealthy.
3. (Doc gap, no task number) Add the `///` remark on `IHealthCheckProcessor.PerformHealthChecksAsync`
   and the one-line note in `docs/health-checks.md`'s TimeOutHealthCheck section describing the
   no-ambient-token behavior — verbatim from the review's finding 4.

**Red-green recipes.** (#280) the review's `SubscribeThrowingMidStreamMessageHandler`/
`ChatThrowingMidStreamMessageHandler` TestServer tests — red today
(`StatusCode=Unknown`, `benzene-status=ok`); green: a classified status (ServiceUnavailable-mapped
code for `InvalidOperationException`) and a failure `benzene-status` trailer. (#281) the review's
`NonCriticalFailingCheck` dual-path test — green form asserts the bridge reports NOT Unhealthy
(Degraded) while a critical Failed still reports Unhealthy; keep the throwing-check green test
(review finding 3) as a permanent regression too since it documents load-bearing framework behavior.

**Verify:** `dotnet test test/Benzene.Grpc.Test -c Release` (whole project) and the HealthChecks
filter in Core.Test.

---

## WP-H — DynamoDB stores: transact accounting + expiry boundary (#271, #272)

**Files:** `src/Benzene.EventSourcing.DynamoDb/DynamoDbEventStore.cs`,
`src/Benzene.EventSourcing/InMemoryEventStore.cs` (contract parity — see ruling),
`src/Benzene.Idempotency.DynamoDb/DynamoDbIdempotencyStore.cs`, tests in
`test/Benzene.Core.Test/EventSourcing*/` and `test/Benzene.Core.Test/Idempotency/DynamoDb/`.

**The findings.** (#271) `MaxEventsPerAppend` (100) counts only `events.Count`, but any
`expectedVersion > 0` append prepends the #121 `ConditionCheck` item — a 100-event append onto an
existing stream sends 101 transact items, over AWS's hard 100 limit; the caller gets AWS's raw
validation error instead of the library's friendly guard. The existing limit test only exercises 101
events at `expectedVersion: 0`. (#272) `TryClaimAsync`'s write condition is strict
(`expiresAt < :now`) while `ReadRecordAsync` is inclusive (`epoch <= now`): at the exact
expiry second the write refuses while the read-back reports absent, and under the documented
fixed-clock seam every retry repeats the disagreement until
`IdempotencyClaimContentionException` — contradicting the class's own "reclaimable the instant it
lapses" contract. Both proven in `review-round17-reliability-2026-08.md`.

**Rulings:**

1. (#271) Pre-flight the ACTUAL item count: when `expectedVersion > 0` the effective per-call cap is
   `MaxEventsPerAppend - 1`; throw the friendly `ArgumentException` explaining the reserved
   condition-check slot ("appending to an existing stream reserves one of DynamoDB's 100
   transaction items for the version check; split appends of 100 events at 99"). Do NOT attempt to
   fold the version assertion into the first Put's own condition (DynamoDB conditions reference only
   the item being written — the review already flagged this as infeasible; don't relitigate). Keep
   100 for `expectedVersion == 0` (genuinely 100 items). **Contract parity** (per #131's own
   principle): `InMemoryEventStore` enforces the same observable contract (99 on an existing stream)
   even though it has no physical condition-check item — with a comment saying the limit mirrors the
   DynamoDB store's real constraint so code is portable across stores. Update both stores' docs.
2. (#272) Make both sides inclusive: change the write condition to `expiresAt <= :now`, matching
   `ReadRecordAsync` and the documented "reclaimable the instant it lapses" intent (the review's
   preferred direction). `ReadRecordAsync` unchanged.

**Red-green recipes.** (#271) the review's captured-request test inverted: a 100-event append at
`expectedVersion: 5` must now throw the friendly `ArgumentException` pre-flight (no SDK call made);
a 99-event append at `expectedVersion: 5` produces exactly 100 transact items and succeeds; a
100-event append at `expectedVersion: 0` still succeeds with 100 items. Mirror the cap test on
`InMemoryEventStore`. (#272) the review's fixed-clock boundary test inverted: with `expiresAt ==
now` exactly, `TryClaimAsync` must WIN the reclaim (conditional put's condition now passes), not
throw contention. Existing fencing/claim tests (#16/#31/#51/#260-era) all stay green.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~EventSourcing|FullyQualifiedName~Idempotency"`.

---

## WP-I — SelfHost: CompositeBenzeneWorker fault racing (#291)

**Files:** `src/Benzene.SelfHost/CompositeBenzeneWorker.cs`, tests in
`test/Benzene.Core.Test/Hosting/CompositeBenzeneWorkerTest.cs`.

**The finding.** `StartAsync` awaits `Task.WhenAll` over every worker's start task — but an
`SqsConsumer`-shaped worker runs its full lifetime inline and never completes, so a sibling's startup
fault is never observed: the rollback catch never runs, the fault is never rethrown,
`BenzeneHostedServiceAdapter.ObserveFault` (the `LogCritical` + `StopApplication` path) never fires,
and the host runs forever with one transport silently dead. Proven with the
`LongRunningWorker`/`ImmediatelyFailingWorker` pair (composite `StartAsync` hangs past the timeout)
in `review-round17-performance-2026-08.md` Finding 4, which also notes every existing test's fake
workers complete synchronously — the blind spot that let this survive.

**Ruling.** Take the review's direction 1 (race the failure), NOT direction 2 (changing the
`IBenzeneWorker` contract is a cross-cutting redesign):

- Keep the no-fault behavior identical: with zero faults, `StartAsync`'s task completes only when
  every worker's task completes (long-running workers keep it alive — `ObserveFault` relies on
  this).
- Add a first-fault signal (a `TaskCompletionSource` set by a fault continuation on each task);
  `await Task.WhenAny(whenAll, firstFault.Task)`. On a fault — whether at startup or later,
  mid-lifetime — run the rollback and rethrow.
- Fix the rollback predicate while there: today it stops only `IsCompletedSuccessfully` workers; it
  must also stop workers whose tasks are **still running** (the long-running shape) — i.e. stop
  every worker whose task is not faulted/cancelled, best-effort with the existing swallow-per-worker
  pattern. Then propagate the original fault.
- Update the method's doc comment (its current reasoning explicitly assumes every task completes).

**Red-green recipe.** The review's red test inverted: `LongRunningWorker` + `ImmediatelyFailingWorker`
→ `StartAsync` must FAULT within the timeout with the failing worker's exception, AND the
long-running worker's `StopAsync` must have been called (add a stop-observed flag to the fake). Add a
late-fault case: a worker that starts fine then faults after a delay → composite task faults, sibling
stopped. Every existing `CompositeBenzeneWorkerTest` stays green.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~CompositeBenzeneWorker|FullyQualifiedName~Hosting"`.

---

## WP-J — CLI: benzene healthcheck non-JSON tolerance (#282)

**Files:** `src/Benzene.CodeGen.Cli.Core/Commands/HealthCheck/HealthCheckCommand.cs`,
`src/Benzene.CodeGen.Cli.Core/Commands/HealthCheck/Extensions.cs`, tests alongside
`HealthCheckCommandFailOnTest`.

**The finding.** `Console.Out.WriteJson(json)` does an unguarded `JValue.Parse` BEFORE the
`Trips`/`IsHealthy` check whose own comment promises tolerance of "a response shape this tool
doesn't recognize" — an empty or plain-text body (realistic: an unwired Lambda, an empty handler
response) crashes with a raw `JsonReaderException` one line before the tolerant path. Proven with
both bodies in `review-round17-cli-registry-2026-08.md`.

**Ruling.** Make `WriteJson` defensive: `try { JValue.Parse... } catch (JsonException) {
source.WriteLine(json); }` — the operator still sees the raw body verbatim on stdout. Then VERIFY
`Trips`/`IsHealthy` itself survives a non-JSON body (if it also parses unguarded, give it the same
guard so an unparseable body follows the documented "only trip on an explicit false" policy — i.e.
does not trip). The command must complete without throwing for empty and plain-text bodies, honoring
`--fail-on` semantics as not-tripped.

**Red-green recipe.** The review's two red tests as permanent regressions (empty body, plain-text
body — `FakeClient` through the existing `HealthCheckClient?` test seam): no exception, raw body
echoed, exit path normal. Existing `HealthCheckCommandFailOnTest` and
`ExecuteAsync_ResponseMissingIsHealthy_DoesNotThrow` stay green.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~HealthCheckCommand"`.

---

## WP-K — Auth hardening (#286, #287 — both minor, both proportionate)

**Files:** `src/Benzene.Mesh.Auth.Oidc/MeshOidcOptions.cs`,
`deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs`, tests in
`test/Benzene.Mesh.Auth.Oidc.Test/MeshOidcOptionsValidateTest.cs` and
`deploy/Mesh/Benzene.Mesh.Host.Test/`.

**The findings.** (#286) `Validate()`'s ≥8-distinct-bytes floor doesn't catch a repeated multi-byte
block: `"ABCDEFGH"` × 4 (32 bytes, exactly 8 distinct values) passes, though it's a 64-bit repeating
structure signing both the CSRF state token and the deterministic session cookie (a weak key here is
a full session-forgery vector). Proven with a probe test. (#287) `MeshDispatchGuardMiddleware.
IsGuarded` matches on canonical path AND topic (explicitly to defeat route aliases);
`MeshAuthGate`'s `dispatchRole` check matches only the literal path — not exploitable today (exactly
one route exists) but a latent drift point the guard's own comment anticipates. Both in
`review-round17-auth-security-2026-08.md`, which otherwise found the entire auth surface clean.

**Rulings:**

1. (#286) Add a smallest-period check alongside (not replacing) the distinct-byte floor: reject a
   key that is an exact repetition of a proper substring (for each period `p` from 1 to `len/2`
   where `len % p == 0`, if the key equals its first `p` bytes tiled, reject). Update the doc
   comment to describe both criteria honestly (the review specifically flagged the comment's "clears
   this by a wide margin" as over-promising). Red: the review's
   `EightByteRepeatingPatternPaddedTo32Bytes` probe inverted — must now throw; existing
   entropy-floor tests (1-byte, 2-byte-alternating rejections; genuine-random acceptance) green.
2. (#287) Give the `dispatchRole` check the same topic-based route-finder fallback `IsGuarded` has —
   read `IsGuarded`'s implementation and mirror its path-OR-topic predicate so the two gates can
   never drift on what counts as the dispatch endpoint. Test at whatever seam `IsGuarded`'s own
   tests use (unit-test the predicate for both the path-match and topic-match cases); an end-to-end
   alias-route test is not required since no second route exists to mount.

**Verify:** `dotnet test test/Benzene.Mesh.Auth.Oidc.Test -c Release` (or wherever
`MeshOidcOptionsValidateTest` lives — locate it first) and `dotnet test
deploy/Mesh/Benzene.Mesh.Host.Test -c Release`.

---

## Coordination notes

- **No two WPs share a source file.** Adjacencies to watch at merge time: WP-B and WP-C both add
  tests around mesh query cancellation (different test files; WP-C's asserts the real
  scope-creation path, WP-B's asserts the composite's catch filter — both must pass together after
  the last merge, which is itself a meaningful integration check on #250's whole chain finally
  working end to end). WP-A (#289) and WP-C both touch cancellation/scope machinery in different
  packages — no shared files. `docs/capability-matrix.md`'s "Mesh — collector" row is touched by
  WP-B and WP-C (and possibly WP-E's XRay note) — the coordinator hand-splices, as in round 16.
- **Order-sensitive verification:** merge WP-C before running the centralized baseline's mesh-host
  suite; #285 is the finding most likely to interact with everything else (it changes what token
  every envelope handler observes). If any pre-existing test implicitly relied on the inner scope's
  token being `None` (e.g. a test driving a handler through the 2-arg overload while a cancelled
  accessor sits in the outer scope), that test was asserting the bug — invert it with a comment.
- **`[OPEN]` entries to record** (in `outstanding-bugs.md`): WP-B — should the mesh spec's
  `MeshTraceEvent` grow a `serviceVersion` field for per-version drift attribution (cross-language
  spec change, decided in the main Benzene repo, not here)? WP-B — is the max-versions cap (default
  8) the right long-term policy vs. a heartbeat-TTL retirement model?
- **Round-16 regression guard, explicitly:** after all merges, the round-16 regression tests for
  #250, #251, #253, #254, #255, #256, #262, #266, #267 must all still pass — WP-A/B/C exist to
  repair those fixes, not replace them. The centralized baseline covers this, but each WP should
  also run its own package's round-16 tests locally before committing.
- Items deliberately NOT filed as fix tasks this round (no action): the FluentValidation named-
  rule-set schema/runtime mismatch (unsupported feature), the Descriptor test-only ALC accumulation
  (bounded, 6 call sites), `RabbitMqConnectionProvider`'s never-disposed health-check connection
  (not DI-tracked, process-lifetime), and the accepted `SessionDuration`-unbounded/stateless-logout
  tradeoff (already documented in the capability matrix).
