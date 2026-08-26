# Fix designs — round 10 review findings (2026-08)

**Status: ACTIVE — ruled, not yet implemented.** This is the fix-design ruling for the round-10
review pass (task board #96): five parallel review agents over Lambda hosting bridges, deeper
CosmosDb/gRPC, health-check logic, Kafka/self-hosted workers, and the abstractions contract
packages, run against `origin/main` @ `4657c9d` (all findings re-checked to still exist at
`9c6b0bd`, the current head — none of the round 7–10 fixes touched these code paths except where
noted). Findings are tracked as task board **#98–#119**; the execution task is **#120**.

Conventions carried over from `work/archive/bug-fix-designs-round7-10-2026-08.md`:

- One work package = one agent = one worktree (`/workspace/wtfix3/<wp>` suggested), committing
  locally, never pushing; the orchestrator merges sequentially into `main`, resolving the expected
  mechanical conflicts in `work/outstanding-bugs.md`/`docs/capability-matrix.md` by keeping both
  sides.
- **Do NOT use `git stash`** in any worktree — all worktrees share `refs/stash`; concurrent stashes
  leaked changes across worktrees twice last round.
- Red→green discipline: for every behavioral fix, first write the failing test against current
  code (or revert-verify after fixing), then fix. Annotation-only packages (WP-X) are exempt from
  red→green but must build warning-clean for the touched files.
- If the implementation must diverge from a ruling below, amend this document in the same commit —
  never silently diverge.
- Definition of done per WP: code + tests + `work/outstanding-bugs.md` `[RESOLVED]` lines +
  `docs/capability-matrix.md` where the observable contract changed + this doc's WP section marked
  done.
- Full-suite baselines to re-verify after the last merge: `dotnet build Benzene.sln -c Release`
  0 errors; `Benzene.Core.Test` ≥3017/2/0; `Benzene.Mesh.Test` 535; `Benzene.Mesh.Host.Test` 141;
  `Benzene.Examples.sln` 0 errors. Note from last round: run the FULL suites, not just the
  touched-area filters — 10 of last round's failures were only visible in the full run.

---

## §1 Work packages

### WP-V — versioned-topic join in the getter layer (#98) — the round's big one

**#98 — `IMessageGetter`/`ResolvedTopicCache` serve a version-blind topic to every non-router
consumer.** Confirmed live by an executed probe: a message with header `benzene-version: v2`
through `UseMeshTrace(...)` exports `TopicVersion = null`, and `MeshCollectorStore` keys
usage/compatibility by `(Topic, TopicVersion)` (`MeshCollectorStore.cs:180,487`), so the mesh's
per-version usage and version-compat reporting classifies every header-versioned invocation as
unversioned.

Root cause is the abstraction shape, not any one consumer (this is why #69/#70 keep recurring):

- `IMessageGetter<TContext>` (`src/Benzene.Abstractions.MessageHandlers/Mappers/IMessageGetter.cs:11`)
  aggregates body + headers + topic but **not** `IMessageVersionGetter<TContext>`; the router's own
  constructor takes both, separately.
- `IMessageTopicGetter.GetTopic` returns an `ITopic` that *has* a `Version` property — so the
  result looks version-complete, but none of the 25 transport topic getters populate it (verified;
  only `PresetTopicMessageTopicGetter` with an explicit preset does).
- The join happens exactly once, inside `MessageRouter.HandleAsync`
  (`src/Benzene.Core.MessageHandlers/MessageRouter.cs:77-88`), into a local that goes nowhere else;
  `MessageGetter<TContext>`'s `ResolvedTopicCache` (`MessageGetter.cs:65-83`) caches the
  **versionless** topic that `Benzene.Mesh.Wire`, `Benzene.CloudService`, `Benzene.Diagnostics`,
  `Benzene.HealthChecks`, `Benzene.Auth.Core` and the XRay decorator all read.

**Ruling — fix at the `MessageGetter` layer, not per-consumer:**

1. `MessageGetter<TContext>.GetTopic` applies `IMessageVersionGetter<TContext>` exactly the way
   `MessageRouter` does today (reuse `GetVersionedTopic` from
   `Benzene.Abstractions.MessageHandlers.Mappers.MessageTopicGetterExtensions` — WP-P built it for
   exactly this), caches the **joined** topic in `ResolvedTopicCache`, and `MessageRouter` consumes
   the already-joined cached topic instead of re-joining locally.
2. `MessageGetter` acquires an `IMessageVersionGetter<TContext>` constructor dependency (nullable,
   preserving behavior for contexts with no version getter registered). Check every DI registration
   site of `MessageGetter`/`IMessageGetter` — the registration is per-transport; missing
   registration must degrade to today's behavior (versionless), never throw.
3. `UseMeshTrace` builds its event *before* `await next()`, so the getter-level join covers it —
   verify with the probe scenario (below); a router write-back alone would NOT fix it, which is why
   the ruling is the getter-level join.
4. While in `MessageRouter`: fix the stale comment at `MessageRouter.cs:106-110` claiming "every
   built-in topic getter converts an unresolvable topic into the '<missing>' sentinel" — false for
   `EventGridMessageTopicGetter`, `QueueStorageMessageTopicGetter`, `TimerMessageMappers` (they
   return null `ITopic`). Comment fix ONLY; the ValidationError-vs-NotFound status asymmetry across
   those three transports is a recorded `[DECISION]` (§3), do not change observable statuses here.

**Tests:** (a) resurrect the reviewer's probe as a real regression test — `benzene-version: v2`
header through a pipeline with `UseMeshTrace`, assert the exported `TopicVersion == "v2"`;
(b) a `MessageGetter` unit test: topic getter returns versionless topic + version getter returns
`v3` → `GetTopic().Version == "v3"`, cached (second call, single invocation of inner getters);
(c) no-version-getter-registered → versionless topic, no throw; (d) the full `Benzene.Core.Test`
suite — this touches the hottest path in the framework; watch the version-selection tests
(#69/#70's regression tests) closely.

**Risk note:** highest-blast-radius change of the round. The agent should read
`MessageRouter`/`MessageGetter`/`ResolvedTopicCache` fully before editing, and run the full
Core.Test suite locally before committing.

### WP-W — validation status-mapping contract (#99, #102)

**#99 — `ValidationStatusAttribute`/`IValidationStatusMapper` silently ignored by two of the three
adapters.** The mechanism lives in the *shared* `Benzene.Abstractions.Validation` package and its
CLAUDE.md documents it as the way to override a failed validation's result status — but only
`Benzene.FluentValidation` (`DefaultValidationStatusMapper.cs:26`) reads it.
`Benzene.DataAnnotations/ValidationMiddleware.cs:34` and `Benzene.JsonSchema/JsonSchemaMiddleware.cs`
hard-wire `IDefaultStatuses.ValidationError` and don't even reference the abstractions package.
**Ruling: wire it in** (not doc-as-FV-only) — both middlewares compute a status at exactly one call
site each; resolve an optional `IValidationStatusMapper` (default to the current behavior when the
handler type carries no attribute) so all three adapters honor the shared contract. Red→green: a
`[ValidationStatus(BadRequest)]`-decorated handler failing DataAnnotations/JsonSchema validation
returns `BadRequest`, not `ValidationError`; undecorated handlers keep `ValidationError`.

**#102 — `ValidationStatusAttribute` allows `AttributeTargets.Method` no code reads**
(`ValidationStatusAttribute.cs:3`; sole reader does `handlerType.GetCustomAttribute`). **Ruling:
drop `AttributeTargets.Method`** (pre-1.0, source-breaking only for code that was already silently
broken). Do it in the same commit as #99 so the attribute's surface is ruled once.

### WP-X — contract-annotation alignment (#100, #101, #103) — low risk, annotation-only

**#100 — `IBenzeneResult.PayloadAsObject` (and `IBenzeneResult<T>.Payload`) declared non-nullable
but null for every failed/void result** (`IBenzeneResult.cs:22`; `BenzeneResult.cs:456` emits
CS8603; seasoned consumers already null-check/`?.`). **Ruling:** annotate `object? PayloadAsObject`
and `T? Payload`, document the failure-path behavior in the XML docs. Fix any new nullability
warnings this surfaces in consumers by honest null-handling, not `!`.

**#101 — `MessageGetter<TContext>` facade narrows the nullability of the interfaces it forwards**
(`MessageGetter.cs:45,55,65` — `string GetBody`/`ITopic GetTopic` vs the interfaces' `string?`/
`ITopic?`; `Topic!` at :77). **Ruling:** align facade signatures with the interfaces. Coordinate
with WP-V (same file!) — WP-X must merge AFTER WP-V; the fix agent for WP-X rebases on the
post-WP-V state.

**#103 — `IVersionSelector.Select(string requestedVersion, ...)` non-nullable parameter its only
caller passes null into** (`IVersionSelector.cs:16`; `MessageHandlerDefinitionLookUp.cs:67` passes
`topic.Version`, null/empty for every unversioned message). **Ruling:** declare `string?
requestedVersion`, and add the doc line that the "must be one of availableVersions" return
contract presumes a non-empty array (the default lookup early-returns on 0 and fast-paths 1).

### WP-Y — host/entry-point seams (#104, #106, #107 + one doc item)

**#104 — ASP.NET hosts never forward `HttpContext.RequestAborted` into the `SendAsync(event, ct)`
overload** (`AspNetServerWorker.cs:64`, `AspApplicationBuilder.cs:122` call the one-arg overload;
Azure Functions and Google PubSub hosts forward theirs). **Ruling:** forward `RequestAborted` in
both call sites. Behavioral consequence: components resolving `ICancellationTokenAccessor` on
ASP.NET now see a real token — which is the documented intent of the overload. Test: an ASP.NET
pipeline handler observes a non-`None` ambient token.

**#106 — `InlineAwsLambdaStartUp.Build()` runs `Configure` before `ConfigureServices` — inverted
vs the production host** (`InlineAwsLambdaStartUp.cs:56-57` vs `AwsLambdaHost.cs:35-36`). With
first-wins `TryAdd*` registrations, a user override wins in production but loses under the test
host. **Ruling:** swap the two lines to match `AwsLambdaHost`. Red→green: register a custom
implementation of a `TryAdd`-registered default via `ConfigureServices`, assert the custom one is
resolved under `InlineAwsLambdaStartUp` (fails today, passes after). Also add one doc line that the
inline host deliberately runs `RunStartUpChecks()` but not `WarmUp()`.

**#107 — `AwsLambdaHost.FunctionHandlerAsync`: a throw from `OnInvocationCompleteAsync` (finally)
masks the invocation's real exception** (`AwsLambdaHost.cs:59-69`; the override point is documented
for telemetry flush, which plausibly throws). **Ruling:** wrap the `OnInvocationCompleteAsync()`
call in catch-and-log so a flush failure never replaces the invocation's outcome. Same WP, same
package, fold in the trivial cleanup: `AwsLambdaMiddlewareRouter.MapResponse` null-checks
`context.Response` *after* serializing into it (`AwsLambdaMiddlewareRouter.cs:76-80`) — check
first (keep the guard; `AwsEventStreamContext` initializes `Response`, but the guard order as
written is dead/misleading).

**Doc item (no task):** add a remark on `IAwsHttpBridge`/`UseHttpBridge*` that the bridge owner
owns exception-to-response conversion — a handler exception propagates as a Lambda function error
(API Gateway 502), unlike Benzene's own API Gateway binding which produces an in-band HTTP error
response. This is the package's stated philosophy; it just isn't written at the seam.

### WP-Z — API Gateway request adapter headers (#105)

**#105 — `ApiGatewayHttpRequestAdapter.Map` replaces the case-insensitive `HttpRequest.Headers`
contract with the raw ordinal wire dictionary — and can assign null**
(`ApiGatewayHttpRequestAdapter.cs:21`; `src/Benzene.Http/HttpRequest.cs:26` establishes
`StringComparer.OrdinalIgnoreCase` + non-null, itself a prior-round fix). For API Gateway events
without headers (health pings, hand-built payloads, authorizer test invokes) `Headers` is null.

**Ruling:** in the adapter — `Headers = new Dictionary<string, string>(request.Headers ??
empty, StringComparer.OrdinalIgnoreCase)` (first-wins on case-collisions, matching
`DictionaryUtils` semantics), `?? string.Empty` for `Method`/`Path`. **First step (the reviewer's
"remains to verify"):** audit every in-repo consumer of `HttpRequest.Headers` for whether any
reads it with a case-sensitive indexer today — record the result in the WP's commit message; it
determines whether this fix closes a live bug or only a latent contract violation (fix ships either
way). Red→green: the reviewer's ~20-line test — an ordinal-cased/null-headers
`APIGatewayProxyRequest` through the adapter, assert case-insensitive lookup works and no NRE.

### WP-AA — CosmosDb change-feed checkpoint isolation (#108)

**#108 — checkpoint failure after a successful batch is misattributed to the handler, and skip
mode retries the just-failed checkpoint inside its own catch**
(`BenzeneCosmosChangeFeedWorker.cs:89-121`: auto-checkpoint at :91 inside the pipeline's `try`;
skip-mode catch calls `checkpointAsync()` a second time at :114 with zero backoff, and the retry's
exception escapes the worker un-logged, reaching the SDK dispatcher). Executed-probe evidence:
both behaviors confirmed (2 checkpoint calls; misattributed "Processing change feed batch …
failed" log).

**Ruling:** move the auto-checkpoint outside the pipeline `try` into its own try/catch: log a
checkpoint failure as a checkpoint failure (naming the lease container as the failing dependency,
not the handler), do NOT re-invoke it from the skip-mode catch, and on checkpoint failure let the
batch be redelivered (correct at-least-once outcome in both modes) without faulting the worker.
Red→green: drive the captured `OnChanges` delegate with a throwing `checkpointAsync` in both modes;
assert single checkpoint invocation, correct log attribution, no escaped exception.

### WP-AB — gRPC client cancellation + health bridge diagnosability (#109, #110 + one doc line)

**#109 — `GrpcBenzeneMessageClient` logs routine cancellation at Error and maps it to
`ServiceUnavailable`** (`GrpcBenzeneMessageClient.cs:96-100` catch-all;
`DefaultGrpcStatusReverseMapper.cs:33` maps `Cancelled → ServiceUnavailable`). Executed evidence:
bare `TaskCanceledException` path → Error log + `ServiceUnavailable`; real mid-flight cancel →
`RpcException(Cancelled)` → no log but still `ServiceUnavailable`. **Ruling:** catch
`OperationCanceledException` in `SendMessageAsync` (mirroring the server's
`GrpcMethodHandler.cs:118`) and return a cancellation-flavoured failure result without an Error
log; change the reverse mapping `Cancelled → Timeout` is NOT ruled — keep `ServiceUnavailable`
for the `RpcException(Cancelled)` path for now and note the mapping question as a doc comment
(changing a wire-visible status vocabulary mapping is a spec-level question; flag to the
cross-repo spec if pursued).

**#110 — `BenzeneHealthCheckBridge`: a typo'd liveness/readiness type name yields unconditionally
`Serving`, and duplicate `Type` keys silently collapse** (`BenzeneHealthCheckBridge.cs:43-46,53`).
**Ruling:** a configured `LivenessCheckTypes`/`ReadinessCheckTypes` entry matching NO registered
check is a wiring error — throw at wiring/startup time (matching the mesh-host "never silently
under-enforced" precedent), not a healthy default at probe time. For the data dictionary, suffix
duplicate `Type` keys the way `HealthCheckNamer` already does elsewhere.

**Doc line (no task):** `src/Benzene.Grpc.Client/CLAUDE.md` + xml-doc — resilience/retry for the
unreachable-channel case is deliberately the app-owned `GrpcChannel`'s `ServiceConfig` retry
policy; Benzene adds none.

### WP-AC — health-check processor + adapters (#111, #112, #113, #114)

**#111 — `CachingHealthCheckProcessor` has no single-flight guard**
(`CachingHealthCheckProcessor.cs:47-59`; executed: 50 concurrent cold-cache callers → 50 full
inner runs, recurring at every TTL expiry). **Ruling:** per-key single-flight —
`ConcurrentDictionary<string, Lazy<Task<...>>>` with `ExecutionAndPublication`, entry
replaced/removed on completion so a faulted run doesn't poison the cache beyond its own await;
correct the XML remark that currently blesses the stampede as "a couple of times". Red→green: the
reviewer's 50-caller repro asserting exactly 1 inner execution.

**#112 — `HttpPingHealthCheck` loses Url + dependency identity on the "didn't respond at all"
failure mode** (`HttpPingHealthCheck.cs:57`; connection-refused → generic decorator result with
`Data=[Exception=HttpRequestException]`, `Dependencies=[]` — with multiple registered ping checks
an operator can't tell which endpoint is down). **Ruling:** catch `HttpRequestException` inside
`ExecuteAsync` and return a failed result carrying `Url` + dependency entry + exception type name,
mirroring the EF/SNS/SQS checks; rethrow `OperationCanceledException` (see #114's contract).

**#113 — a throwing `Timeout`/`IsNonCritical`/`Type` property getter crashes the entire
aggregation** (`HealthCheckProcessor.cs:58,73` read the members outside any guard inside the
`Task.WhenAll` selector; decorators read `_inner.Type` inside their own catch handlers; executed:
a throwing `Timeout` getter → whole `PerformHealthChecksAsync` throws, zero results for healthy
checks). **Ruling:** hoist the per-check member reads into the same guarded scope as execution
(read inside `RunTimedAsync` under try/catch degrading to a failed result for that one check);
snapshot `Type` once up front so the decorators' catch paths can't re-throw through the getter.

**#114 — EF checks swallow `OperationCanceledException` as an ordinary connection failure**
(`DatabaseConnectionHealthCheck.cs:50-53`, `DatabaseHealthCheck.cs:76-79,88-93`; executed:
cancelled token → `failed, CanConnect=False` instead of propagating for the decorators'
`"Cancelled"` classification — defeats the WP-K/#50 contract for this family). **Ruling:** add
`catch (OperationCanceledException) { throw; }` before the catch-alls in
`TryConnect`/`TryGetAppliedMigrationsAsync`. **Scope extension:** the reviewer flagged the same
broad-catch shape family-wide — grep every `IHealthCheck` implementation for `catch (Exception)`
swallowing OCE that #50's central `HealthCheckError.Classify` doesn't already handle, and apply
the same one-line rethrow wherever reachable (list them in the commit message). This is the same
"the fix pattern must reach the whole family" lesson as #39/#50.

### WP-AD — self-hosted worker settlement-on-shutdown (#115, #116, #117) — one theme, three transports

The unifying defect: three of the four self-hosted workers settle *successfully processed*
messages through calls gated on the shutdown/processor token itself, so graceful shutdown converts
completed work into redelivery (double-processing), silently. Kafka is the only one that gets it
right (synchronous `StoreOffset` + commit in the run task's `finally`). Principle for all three
fixes: **settlement of already-completed work is part of graceful drain — it runs under
`CancellationToken.None` or a short bounded independent grace token, never the run/stop token.**

**#115 — SQS: successfully-processed message silently never deleted when shutdown fires mid-batch**
(`SqsConsumer.cs:105-111` delete with the run token; `:128-131` catch swallows the OCE with no log;
executed probe confirmed: handler Ok → cancelled token → delete throws → message redelivered, zero
log lines). **Ruling:** after a batch's pipeline run completes, delete the successful messages
under `CancellationToken.None` (bounded by the SDK's own HTTP timeout); if the delete still fails,
log which message IDs will be redelivered.

**#116 — EventHub: `UpdateCheckpointAsync` sits outside the try/catch and uses
`args.CancellationToken`** (`BenzeneEventHubWorker.cs:137-142` vs try/catch at :98-133; executed
probe: handler success + cancelled args token → OCE propagates unhandled out of
`OnProcessEventAsync`, which per the SDK docs faults the partition task and on some hosts crashes
the process; also ANY transient checkpoint-store blip escapes, bypassing `CatchHandlerExceptions`
entirely). **Ruling:** wrap the checkpoint block in its own try/catch: cancellation path → log
info (redelivery is the correct at-least-once outcome), non-cancellation checkpoint failure → log
+ apply the same `CatchHandlerExceptions` stop-or-continue policy as every other failure in the
file; never let it fault the partition task. Checkpoint under `CancellationToken.None` for the
completed-work case per the principle above.

**#117 — ServiceBus: settlement uses `args.CancellationToken` — same shutdown race, plus the
abandon-in-catch replaces the original exception (SUSPECTED — verify first)**
(`BenzeneServiceBusWorker.cs:242-245` settler passes `_args.CancellationToken` into
`CompleteMessageAsync`/`AbandonMessageAsync`; `:176-195` catch → abandon → rethrow). Documentary
evidence only (SDK docs: args token "will be cancelled when StopProcessingAsync is called");
could not be executed because the settler seam is `internal`. **Ruling:** (1) first verify by
test — the settler seam may need `InternalsVisibleTo` for `Benzene.Test` (acceptable; the repo
already uses it) or a small refactor making the settler token injectable; reproduce the
cancelled-token settle; (2) then settle with `CancellationToken.None` (or
`MessageLockCancellationToken`, which is the more correct bound — rule at implementation time and
amend here); (3) in the catch path, wrap the abandon in its own try/catch so an abandon failure
never replaces the original handler exception in the rethrow. If (1) shows the SDK cancels the
args token only strictly after in-flight handlers complete, downgrade this to a doc note and
record that here — do not fix what can't happen.

### WP-AE — Kafka rebalance + config hygiene (#118, #119) (done)

**#118 — no `SetPartitionsLostHandler`: on partition loss the revoke-drain handler runs — up to
`DrainTimeout` (30s) blocking rejoin, then a commit the Confluent docs explicitly say not to make**
(`BenzeneKafkaWorker.cs:333-370`; the broker's generation fencing makes the stale commit fail —
caught and logged at Debug as "no offsets to commit", mislabeling a fencing rejection — so no
corruption, but recovery is delayed and the log lies). **Ruling:** register a
`SetPartitionsLostHandler` that skips the commit entirely and skips (or tightly bounds, ≤1s) the
drain; log lost-vs-revoked as distinct events at Information.

**#119 — `StartAsync` mutates the caller's shared `ConsumerConfig`**
(`BenzeneKafkaWorker.cs:104` sets `EnableAutoOffsetStore = false` on the caller's instance).
**Ruling:** copy the config (Confluent's `ConsumerConfig` has a copy constructor via its
dictionary form) before mutating, or set the value on a cloned instance handed to the builder —
the caller's object stays untouched.

---

## §2 Merge order & coordination

1. **WP-V first** (touches `MessageGetter`/`MessageRouter`; WP-X edits the same file and must
   rebase on it). Everything else is disjoint and can land in any order after.
2. WP-X explicitly AFTER WP-V.
3. WP-AD's three fixes are one theme; one agent takes all three so the settlement principle is
   applied uniformly (and #117's verify-first gate is decided by someone holding all the context).
4. Expected doc conflicts in `work/outstanding-bugs.md` (every WP appends `[RESOLVED]` lines) —
   resolve keep-both as always.

## §3 Decisions recorded, NOT fixed this round (add as `[DECISION]` to outstanding-bugs.md)

- **Worker self-stop leaves the process Ready and health green** (Kafka onFault, Kafka DLT-produce
  failure, EventHub `CatchHandlerExceptions=false` all deliberately stop the worker; every health
  check probes broker reachability, none probes "is my worker running"; `IBenzeneWorker` exposes
  no state to probe). A liveness-surface for worker state is a design decision for the maintainer
  — candidate shapes: a stopped/faulted flag surfaced through a liveness-category health check, or
  self-stop optionally failing the host fast.
- **EventHub has no poison-message escape hatch** (skip-on-failure or stop-the-worker only) — the
  argument that justified Kafka's retry-then-DLT producer applies verbatim; recorded as a feature
  candidate, not a bug (the current behavior is thoroughly documented as deliberate).
- **gRPC client per-call deadlines not settable by the caller** (`GrpcBenzeneMessageClient` only
  forwards the inherited inbound deadline; `GrpcContextConverter` accepts one with no public path
  to it) — API-surface feature candidate.
- **Missing-topic status asymmetry**: EventGrid/QueueStorage/Timer report a missing topic as
  `ValidationError` ("Topic is missing"), sentinel-returning transports as `NotFound` — same
  condition, two statuses. Normalizing is a wire-visible behavior change; deferred. (The stale
  comment claiming uniformity IS fixed, in WP-V.)
- **Unknown Cosmos `ChangeFeedOperationType` → `Replace`** — already a recorded `[DECISION]`
  (pre-existing); round-10 re-found it independently; unchanged.

## §4 Findings with NO action (for the record)

- gRPC streaming OCE→RpcException translation covers only the pipeline phase — client still sees
  `RpcException(Cancelled)`, no resource leak; possible server-side log noise unverified.
- `AwsEventStreamContext.Handled` non-empty-stream claim — documented, no in-repo middleware
  affected.
- `AwsLambdaBootstrap` cancellation promptness — delegation to Amazon.Lambda.RuntimeSupport;
  probe couldn't complete under host contention; nothing in Benzene's ~15 lines to fix.
- EF fresh-install semantics, HttpPing redirect policy, connection-pool exhaustion bounding,
  `SchemaHealthCheck` (not vulnerable to the #46 class; optional `Lazy<string>` perf hygiene noted
  under `[PERF]`), `Benzene.Grpc.Versioning` end-to-end, default-interface-member audit, warm-state
  leak audit, `IStartUpCheck`/`IWarmUpTask` contract audit — all verified clean/deliberate.

## §5 Round completion

When #98–#119 are landed and baselines re-verified: docs-archivist moves this file to
`work/archive/` stamped with landing commits; capability-scribe pass for the observable-contract
changes (versioned-topic join, validation status mapping, health-check single-flight +
classification, worker settlement semantics, gRPC cancellation classification); mark #120
completed.
