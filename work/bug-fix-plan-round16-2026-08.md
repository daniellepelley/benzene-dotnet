# Bug-fix plan — round 16 (2026-08)

**Status: READY FOR EXECUTION — not yet started.** Covers task board **#250–#267** (18 findings from
the round-16 review pass, all evidence-backed). Source review docs, all at `work/`:

- `review-round16-core-2026-08.md` (#266, #267)
- `review-round16-infrastructure-2026-08.md` (#262)
- `review-round16-performance-2026-08.md` (#267 corroboration, #262 corroboration)
- `review-round16-mesh-composition-2026-08.md` (#250, #251)
- `review-round16-observability-2026-08.md` (#252, #253, #254, #255, #256)
- `review-round16-aws-2026-08.md` (#260, #261)
- `review-round16-azure-2026-08.md` (#257, #258, #259)
- `review-round16-schema-codegen-2026-08.md` (#263, #264, #265)

Every finding has an executed reproduction in its review doc (a red test run against the real
assemblies at `28473b0`, reproduced inline in the doc when the test file was deleted). Two evidence
tests are **already committed** (`test/Benzene.Mesh.Test/MeshCollectorQueryCancellationTest.cs`,
`test/Benzene.Mesh.Test/MeshCollectorSideBySideVersionTest.cs`) — they currently PASS by asserting
the buggy behavior, and WP-E must invert them (see WP-E).

## Task board mapping

| WP | Tasks | Area |
|----|-------|------|
| A | #266, #262 | Core disposal architecture (Microsoft DI adapter + RedisCacheService) |
| B | #267 | Benzene.Resilience.Polly (hedging/fallback docs + concurrent-attempt guard) |
| C | #257, #258, #259 | Azure trigger family (infra escalation, cancellation, CosmosDb generator) |
| D | #260, #261 | AWS (IdempotencyMiddleware null-result convention, outbound client cancellation) |
| E | #250, #251, #253, #256 | Mesh collector/query (cancellation, versioned catalog, null elements, composite catch) |
| F | #252 (+ XRay bare catch from #256's note) | Mesh fleet trace sources (Jaeger/Tempo/XRay catch filters) |
| G | #254, #255 | Mesh.Dispatch (Prune TOCTOU, NotImplemented audit) |
| H | #263, #264, #265 | Schema/CodeGen (C# escaping, Float/Null schema, Markdown map property) |

## Execution protocol (standard — same as rounds 11–15)

1. **One isolated git worktree per work package**, all created detached from the same base commit on
   `main` (record it at kickoff). `git worktree add --detach <path> <commit>`. **Never `git stash`**
   (shared `.git` object store; `refs/stash` collision risk).
2. **Red first**: reproduce the finding with the recipe below (most are copy-paste from the review
   doc) and confirm it fails/reproduces before changing source. Then fix, then confirm green. Keep
   the test as a permanent regression test unless the recipe says otherwise.
3. **Scoped builds only**: build/test the specific test project (`dotnet test test/<proj> -c Release
   --filter ...`), never the whole solution, while other work packages run in parallel — the host
   OOM-kills concurrent full-solution builds (verified repeatedly in rounds 12–15). The coordinator
   runs one centralized full baseline (full `Benzene.sln` build, `Benzene.Core.Test`,
   `Benzene.Mesh.Test`, `Benzene.Mesh.Host.Test`, `Benzene.Examples.sln` build) after the last merge.
   Round 15's post-merge experience (two integration bugs visible only after all merges landed — see
   `outstanding-bugs.md` "Round 15 + rounds 12–14: two integration bugs...") is why this step is not
   optional.
4. **Subagents cannot receive background-task notifications.** Run every build/test as a single
   plain foreground Bash call. Do not use run_in_background or Monitor.
5. **Definition of done per WP**: fix + regression test(s) green + a dated `[RESOLVED]` entry
   appended to `work/outstanding-bugs.md` (immediately before the `## Open — maintainer decisions`
   heading — every WP appends at the same place, the coordinator resolves the identical-shaped merge
   conflicts with the established sed marker-deletion pattern) + the relevant
   `docs/capability-matrix.md` row updated to describe what the code now does. Commit with a clear
   message citing the task numbers.
6. **Coordinator merges sequentially** into `main`, resolving `outstanding-bugs.md` conflicts
   mechanically and `capability-matrix.md` conflicts by hand-reconciling both sides' additions, then
   runs the centralized baseline, then pushes.

---

## WP-A — Core disposal architecture (#266 high, #262 high)

**Files:** `src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverAdapter.cs`,
`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs`,
`src/Benzene.Cache.Redis/RedisCacheService.cs`, tests under `test/Benzene.Core.Test/Cache/Redis/`
and a new DI-parity test alongside the existing Microsoft/Autofac parity tests.

**The findings.** (#266) `MicrosoftServiceResolverAdapter.Dispose()` disposes the MS DI scope
synchronously; MS DI's `ServiceProviderEngineScope.Dispose()` throws `InvalidOperationException` for
any resolved instance that implements only `IAsyncDisposable` — so **any user-registered
async-only scoped/transient service crashes every message** through the core
`MiddlewareApplication.HandleAsync` `using var serviceResolver = ...` path, and the resource's
`DisposeAsync` never runs (crash + leak). The Autofac adapter does NOT have this problem — Autofac's
own `ILifetimeScope.Dispose()` bridges async-only components correctly. (#262) `RedisCacheService`
is itself `IAsyncDisposable`-only, so it trips exactly this when container-owned — including through
`MicrosoftServiceResolverFactory.Dispose()`'s `(_serviceProvider as IDisposable)?.Dispose()`, which
is the ONLY disposal path `Benzene.Aws.Lambda.Core` has (its whole chain is `IDisposable`-only).

**Rulings:**

1. **Fix #266 generically in the adapter — this is the systemic fix.** In
   `MicrosoftServiceResolverAdapter.Dispose()`: if the wrapped scope/provider implements
   `IAsyncDisposable`, bridge — `DisposeAsync().AsTask().GetAwaiter().GetResult()` — else call the
   sync `Dispose()`. Use an **unbounded** wait here (matching Autofac's blocking semantics), NOT the
   `MeshAnnouncer` bounded-5s pattern: abandoning a scope's disposal mid-way would leak user
   resources by design, and the work being awaited is the user's own disposal code, not a network
   flush. Apply the same preference in `MicrosoftServiceResolverFactory.Dispose()` (the root
   provider). Keep the existing `DisposeAsync` paths untouched.
2. **Still fix #262 too** — add `IDisposable` to `RedisCacheService`, bridging to the existing
   `DisposeAsync()` with the established bounded pattern
   (`DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5))`, swallow `AggregateException`) — same as
   `InternallyOwnedRateLimiterHolder<TContext>` / `MeshAnnouncer`. Rationale for doing both: the
   adapter fix covers Benzene-managed containers, but `RedisCacheService`'s own `CLAUDE.md` tells
   consumers to register it in *their* container, which may not be Benzene's adapter at all; a
   bounded wait is acceptable here because Redis multiplexer disposal is a prompt local operation.
3. **Do NOT add `IAsyncDisposable` to `IServiceResolver` / make `MiddlewareApplication` use
   `await using` this round.** That is a public-contract change rippling through every adapter and
   `using` site; the sync bridge resolves the user-visible defect without it. Record it as an
   `[OPEN]` maintainer-decision entry in `outstanding-bugs.md` ("should the DI abstraction grow an
   async disposal contract?") so it isn't lost.

**Red-green recipe.** Red (all verbatim from the review docs, all verified failing/reproducing at
`28473b0`):
- #266: `services.AddScoped<AsyncOnlyResource>()` (a class implementing only `IAsyncDisposable`
  with a `DisposedAsync` flag); `MicrosoftServiceResolverFactory.CreateScope()`, resolve, dispose
  the scope → today throws `InvalidOperationException` and `DisposedAsync` stays false. Green:
  no throw, `DisposedAsync == true`.
- #262 scoped + singleton scenarios: the two `TestRedisCacheService` tests reproduced in full in
  `review-round16-infrastructure-2026-08.md` (per-message scope disposal; factory disposal). Green:
  `Record.Exception(...)` returns null in both.
- Parity: port the review's Autofac control test as a permanent parity test — both adapters must
  dispose an `IAsyncDisposable`-only container-owned service without throwing and actually run its
  `DisposeAsync`.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Cache.Redis|FullyQualifiedName~Microsoft"` scoped runs; the full-suite check
happens centrally. Watch for tests that today *rely* on sync disposal throwing (there should be
none, but grep for `InvalidOperationException` in DI-related tests before assuming).

---

## WP-B — Benzene.Resilience.Polly (#267 high)

**Files:** `src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs`,
`src/Benzene.Resilience.Polly/Extensions.cs` (XML docs), `src/Benzene.Resilience.Polly/*.csproj`
(`<Description>`), `src/Benzene.Resilience.Polly/CLAUDE.md`, `docs/cookbooks/polly-resilience.md`,
`test/Benzene.Core.Test/Resilience/PollyResilienceMiddlewareTest.cs` (or a sibling file).

**The finding (two halves, one root).** (a) The package's `.csproj` description, XML remarks,
CLAUDE.md, and cookbook all list Hedging and Fallback as supported — but Polly.Core 8.5.0's
`AddHedging`/`AddFallback` exist only on the *generic* `ResiliencePipelineBuilder<TResult>`, and
every `UseResiliencePipeline` overload hands out the non-generic builder; the advertised code shapes
fail to compile (CS1929/CS0305, verified). (b) The deeper bug: `PollyResilienceMiddleware.HandleAsync`
shares one mutable `CancellationTokenAccessor`, one `context`, and one `next` across every Polly
attempt. Any concurrent-attempt strategy — reachable TODAY via Polly's public non-generic
`AddStrategy(...)` extensibility point — runs `next()` (the entire downstream pipeline) twice for
one message, tears the ambient token between attempts, and last-write-wins the shared context.
All three failure modes proven with a 3/3-passing xUnit repro (reproduced in full in
`review-round16-performance-2026-08.md`; the strategy + three tests are copy-paste ready).

**Rulings:**

1. **Docs: remove the Hedging/Fallback claims everywhere** (csproj description, XML remarks,
   CLAUDE.md, cookbook title/body). Replace with an accurate supported-strategy list (Retry,
   Timeout, CircuitBreaker, RateLimiter — everything expressible on the non-generic builder that
   runs attempts strictly one-at-a-time) and one honest paragraph on WHY: Benzene results flow
   through the mutable context, not a `TResult`, so generic result-typed strategies don't map onto
   this middleware, and concurrent-attempt strategies are out of scope (below). Mirror the plain
   boundary-statement style `Benzene.Resilience.Core` already uses for its own "no circuit
   breaker/timeout/bulkhead" line.
2. **Runtime: fail fast, don't silently corrupt.** Add a per-`HandleAsync`-call re-entrancy guard
   inside the attempt callback (e.g. `Interlocked.Increment` on a local counter object created per
   `HandleAsync` call): if a second attempt starts while one is in flight, throw
   `NotSupportedException` with a message naming the problem ("a concurrent-attempt resilience
   strategy (e.g. a custom hedge) is not supported by PollyResilienceMiddleware: attempts share the
   message's pipeline, context, and ambient cancellation token — run attempts sequentially or hedge
   at a different layer"). This is option (a) from the performance review's recommendation; do NOT
   attempt the attempt-isolation redesign (option b) this round — it is an architecture change
   (per-attempt context cloning has no defined merge semantics for a mutable message context) and
   needs a maintainer decision. Record option (b) as an `[OPEN]` entry.
3. Sequential strategies must be provably unaffected: the guard must not add per-attempt allocation
   beyond the one counter, and every existing `PollyResilienceMiddlewareTest` (Retry/Timeout paths,
   the #237 and #63 regression tests) must pass unchanged.

**Red-green recipe.** Red: the `ConcurrentDuplicateStrategy` + 3 tests from
`review-round16-performance-2026-08.md` (all pass today, proving corruption). Green: rewrite them so
the middleware now throws `NotSupportedException` on the concurrent second attempt, `next()` runs at
most once, and the sequential-strategy suite is untouched. Also add a compile-time-honesty check for
the docs half if cheap (a test asserting the cookbook file no longer contains "hedging"/"fallback"
is acceptable), otherwise verify by grep in the commit message.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~PollyResilienceMiddleware"`.

---

## WP-C — Azure trigger family (#257 high, #258 minor, #259)

**Files:** `src/Benzene.Azure.Function.Core/AzureFunctionBatchApplicationBase.cs`,
`src/Benzene.Azure.Function.Timer/TimerApplication.cs`,
`src/Benzene.Azure.Function.SourceGenerators/Transports/MessagingTransports.cs`, tests in
`test/Benzene.Core.Test/Azure/` and `test/Benzene.Core.Test/Autogen/AzureFunctions/`.

**The findings.** (#257) Under `CatchExceptions=true`, `AzureFunctionBatchApplicationBase
.ProcessItemAsync` (lines ~178–189) detects `BenzeneFailure.IsInfrastructure(ex)`, logs a
differentiated message — and never rethrows. This is the exact `#228` defect fixed for AWS
SNS/S3/EventBridge in round 15, unfixed for every Azure batch trigger (ServiceBus, EventHub, Kafka,
QueueStorage, EventGrid) plus, independently, `TimerTickApplication.HandleAsync` (lines ~110–117).
Impact: a mis-wired deploy silently drops messages (host checkpoints on "successful" invocations) or,
for ServiceBus Explicit-ack, loops abandon/redeliver forever with the service reporting healthy.
(#258) The same catch also swallows a genuine ambient-cancellation `OperationCanceledException` for
an already-*running* item, while a still-*queued* sibling correctly aborts the invocation per #230 —
two items hit by the same host cancellation get opposite treatment based on scheduling luck.
(#259) `BenzeneCosmosDbTrigger`'s `DatabaseName`/`ContainerName` are never validated (unlike every
sibling's destination field, BENZ0003–BENZ0007); both omitted compiles clean and emits
`CosmosDBTrigger(databaseName: "", containerName: "", ...)` that fails only at Azure host startup.

**Rulings:**

1. (#257) Mirror `SingleContextEscalatingApplicationBase.ProcessAsync` exactly: compute
   `isInfrastructure` once, keep the existing differentiated log line, then `if (isInfrastructure)
   throw;` — in BOTH `AzureFunctionBatchApplicationBase.ProcessItemAsync` and
   `TimerTickApplication.HandleAsync`. The Azure reviewer confirmed this composes cleanly with
   ServiceBus Explicit-ack's `OnExceptionCaughtAsync` abandon hook (which runs before the
   log/rethrow): the message is still abandoned AND the invocation now fails loudly.
2. (#258) In the same catch, let a *genuine* ambient cancellation escape containment: rethrow when
   `ex is OperationCanceledException && cancellationToken.IsCancellationRequested` (the
   token-verified form, matching `MessageHandler.cs`'s existing pattern — NOT a bare
   type-based exclusion, so an application-produced OCE unrelated to host shutdown is not
   over-escalated). Apply in both files, same block as ruling 1.
3. (#259) New diagnostic `CosmosDbTriggerMissingDestination`, reported when
   `database.Length == 0 || container.Length == 0`, following `ServiceBusTriggerMissingDestination`'s
   exact shape (report + emit nothing), checked alongside — not instead of — the existing
   `DocumentType`/BENZ0002 check. Use the next free BENZ id (the reviewer suggests BENZ0010; verify
   against the actual diagnostic-id list in the generator before claiming it).

**Red-green recipe.** Red (all verified reproducing in `review-round16-azure-2026-08.md`):
- #257: `TimerTickApplication` with `TimerOptions { CatchExceptions = true }` + a pipeline throwing
  `BenzeneResolutionException` → `HandleAsync` completes without throwing today. Same via
  `EventGridBatchApplication` (representative of every base-class consumer). Green: both rethrow.
- #258: `EventGridBatchApplication`, `CatchExceptions = true`, cancelled CTS, pipeline throws
  `new OperationCanceledException(cts.Token)` with that token passed as the call's
  `cancellationToken` → completes without throwing today. Green: propagates. Also add the negative:
  an OCE with an UNRELATED token while the ambient token is not cancelled stays contained.
- #259: `CSharpGeneratorDriver` over `[assembly: BenzeneCosmosDbTrigger(Name = "c", DocumentType =
  typeof(OrderDoc))]` → today zero diagnostics + `databaseName: ""` in output. Green: one
  diagnostic, nothing emitted. Follow the existing generator test file's harness.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Azure|FullyQualifiedName~AzureFunctionTriggerGenerator"` — the Azure suites
include the #230/#231 regression tests, which must stay green.

---

## WP-D — AWS: idempotency convention + outbound client cancellation (#260, #261)

**Files:** `src/Benzene.Idempotency/IdempotencyMiddleware.cs`;
`src/Benzene.Clients.Aws.Sqs/SqsClientMiddleware.cs`, `src/Benzene.Clients.Aws.Sns/SnsClientMiddleware.cs`,
`src/Benzene.Clients.Aws.EventBridge/EventBridgeClientMiddleware.cs`,
`src/Benzene.Clients.Aws.Lambda/AwsLambdaClientMiddleware.cs` + `AwsLambdaClient.cs` +
`src/Benzene.Clients.Aws.Lambda/CLAUDE.md` (false claim at lines ~31–33),
`src/Benzene.Clients.Aws.StepFunctions/StepFunctionsClient.cs`, the three batch clients
(`SqsBatchMessageClient`, `SnsBatchMessageClient`, `EventBridgeBatchMessageClient`),
`src/Benzene.Aws.Sqs/Client/SqsMessageClient.cs`; tests under `test/Benzene.Core.Test/`.

**The findings.** (#260) `IdempotencyMiddleware.WasSuccessful` treats a completed-without-throwing
pipeline whose `MessageResult` was never set as SUCCESS and permanently marks the claim `Completed`
— directly contradicting the "null == failure, redeliver" convention SQS/DynamoDb always had and
#229 extended to SNS/S3/EventBridge. Proven interaction: first attempt → transport throws for
redelivery while the claim is already `Completed`; the redelivery short-circuits as a "duplicate
success" without the real handler ever running. (#261) Every outbound AWS client calls its SDK
method with no `CancellationToken` — despite EVERY one of those SDK methods having a
`CancellationToken` overload (verified by reflection against the installed `AWSSDK.*` packages;
the Lambda CLAUDE.md's claim to the contrary is false) — so `UseTimeout(...)` around any AWS send
is a silent no-op (proven: a 30ms timeout never fires after 300ms), and graceful-drain cancellation
can't abort an in-flight send. The sibling `HttpClientMiddleware`/`GrpcBenzeneMessageClient` already
resolve `ICancellationTokenAccessor` for exactly this.

**Rulings:**

1. (#260) Change `WasSuccessful`'s fall-through precisely — distinguish the two null cases:
   ```csharp
   if (context is IHasMessageResult hasResult)
   {
       return hasResult.MessageResult?.IsSuccessful ?? false;   // result-bearing transport, no result set => NOT proven successful
   }
   return true;                                                  // transport with no result concept at all: no-throw == success, unchanged
   ```
   A context type that doesn't implement `IHasMessageResult` has no result signal to be consistent
   with — keep no-throw-as-success there. On the new `false` path, take the SAME code path the
   middleware already takes for an explicit `IsSuccessful == false` (release the claim so
   redelivery re-runs the handler) — read the existing failure branch and reuse it; do not invent a
   new settle mode.
2. (#261) Give each client middleware/client the same optional `ICancellationTokenAccessor`-resolving
   constructor overload `HttpClientMiddleware` already has (`_cancellation?.CancellationToken ??
   CancellationToken.None` at the point of use), and pass the token into the existing SDK overload
   at every listed call site. Purely additive — no wire change, no `IBenzeneMessageClient` /
   `IStepFunctionsClient` interface change. Fix the Lambda `CLAUDE.md` claim. Where the middleware
   is constructed via DI extensions, resolve the accessor with `TryGetService` so pipelines without
   the registration keep working (the Http client's constructors show the pattern).
3. #261 explicitly does NOT change SDK retry/timeout configuration — only threads the ambient token.

**Red-green recipe.** Red (verified in `review-round16-aws-2026-08.md`):
- #260: `SnsApplication` wrapping only `IdempotencyMiddleware<SnsRecordContext>` around a `next`
  that never sets `MessageResult`. Call 1: throws `SnsMessageProcessingException` AND
  `store.TryClaimAsync` reports `Completed`. Call 2: returns success with the handler counter still
  1. Green: call 1 releases the claim; call 2 re-runs the handler. Also run the full existing
  Idempotency suite — the duplicate-returns-Ok tests for genuinely-completed messages must stay
  green.
- #261: `TimeoutMiddleware<SqsSendMessageContext>` at 30ms around `SqsClientMiddleware` over a
  mocked never-completing `IAmazonSQS.SendMessageAsync` → today still `WaitingForActivation` after
  300ms. Green: `TimeoutException` fires ~on time and the mock observes a cancelled token. Add one
  equivalent test per client family (Sqs/Sns/EventBridge/Lambda/StepFunctions) — mechanical clones.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Idempotency|FullyQualifiedName~Clients"` plus the SNS/S3/EventBridge #228/#229
regression tests (this WP touches their neighborhood's semantics).

---

## WP-E — Mesh collector/query (#250, #251, #253, #256 minor)

**Files:** `src/Benzene.Mesh.Collector/Handlers.cs`, `src/Benzene.Mesh.Collector/MeshCollectorStore.cs`,
`src/Benzene.Mesh.Collector/CompositeMeshFleetReadModel.cs`, `src/Benzene.Mesh.Collector/Views.cs`,
tests in `test/Benzene.Mesh.Test/` (including the two ALREADY-COMMITTED evidence tests).

**The findings.** (#250) The five `mesh:query:*` handlers never resolve
`ICancellationTokenAccessor` — every layer beneath them (read model → composite → trace sources →
`BoundedFanOut`) threads a token that never arrives, so `UseTimeout(...)` on the query envelope is
inert and an abandoned browser query keeps running (and billing) a full X-Ray/Jaeger/Tempo scan.
(#251) `MeshCollectorStore._services` is keyed by service name only and `Register` overwrites the
one stored descriptor — but spec §2.4 requires the catalog key `(service, serviceVersion)` and says
side-by-side versions are NOT drift. A canary's second version evicts the first's contract and
falsely flags its healthy instances as drifted. (#253) `AddEvents` NREs on a null *element* inside a
non-null events list (legal on the wire), partially mutating state and failing ingestion — violating
the file's own "no missing feed ever fails ingestion" invariant; `AddIssues` has the identical
exposure for a null `MeshIssue` element. (#256) `CompositeMeshFleetReadModel.TraceAsync`/
`CorrelationAsync`'s bare `catch { return null; }` swallows a genuine caller cancellation as an
authoritative "not found".

**Rulings:**

1. (#250) Give all five query handlers the same optional `ICancellationTokenAccessor?` constructor
   parameter `MeshDispatchMessageHandler` (#185) has, resolved at the point of use
   (`_cancellation?.CancellationToken ?? CancellationToken.None`), passed into every read-model
   call. Check how the handlers are constructed/registered (`MeshCollectorHandlers.Queries`) and
   mirror however #185's registration made the accessor reach the dispatch handler.
2. (#251) Bring the store into conformance with spec §2.4 (this is an implementation fix, NOT a
   spec change — the spec already rules on this exact scenario; re-read §2.4 in the sibling
   `Benzene` repo's `docs/specification/mesh.md` before coding). Key the service state by
   `(Service, ServiceVersion ?? "")` — a nested per-version map under the name is fine. Required
   behavior: `Register` for a new version must not evict another version's entry; `HashMatches`
   must compare each instance's reported hash against ITS OWN version's descriptor; two versions
   with differing hashes is healthy, same-version-different-hash is drift. Where the view API's
   shape is underdetermined by the spec (e.g. what `Service(name)` returns when two versions are
   live), prefer additive changes (e.g. `ServiceView` gains per-version data, or `Fleet()` lists
   one row per live `(service, version)`) and record the choice in the `[RESOLVED]` entry; do not
   silently change existing single-version behavior — every existing collector test must stay green
   unmodified except where it directly asserts the buggy eviction.
3. (#253) Skip null elements in `AddEvents`'s loop (and null `MeshIssue` elements in `AddIssues`),
   matching the file's existing null-tolerance conventions (the null-`Status`-field test directly
   above is the model). Decide the `Ack.Accepted` count against that existing convention and state
   the choice in the test.
4. (#256) Narrow the composite's bare catch: rethrow when the exception is an
   `OperationCanceledException` and the method's own `cancellationToken.IsCancellationRequested` —
   keep swallowing everything else (that fetch-isolation is correct and deliberate). Match
   `RecentFlowsAsync`/`TopicsFromUsageAsync`'s patterns for consistency if they share the shape.

**Red-green recipe.**
- #250: **invert the committed** `MeshCollectorQueryCancellationTest` — today it PASSES by proving a
  50ms `UseTimeout` has no effect on a 5s fake read model. Rewrite it to the green form (the same
  construction `MeshDispatchTest.UseTimeout_AroundTheDispatchHandler_ActuallyBoundsTheRealDispatchCall`
  uses): the deadline fires, the fake observes cancellation, the call is bounded. Cover at least
  Fleet + Trace handlers (the others are mechanical clones — cover all five cheaply if the fake
  supports it).
- #251: **invert the committed** `MeshCollectorSideBySideVersionTest` — today it PASSES by proving
  eviction (`ServiceVersion == "2.0.0"` only, v1 instance `HashMatches == false`). Rewrite: both
  versions' catalog entries live, each version's instance hash-matches against its own descriptor,
  and add the drift-positive case (same version re-registered with a different hash IS flagged).
- #253: the `AddEvents_NullElementInEventsList_DoesNotThrow_AndAppliesTheOtherEvents` recipe from
  the observability review (batch `[before, null, after]` → today NREs at line ~179; green: no
  throw, both real events ingested). Clone for `AddIssues`.
- #256: `TraceAsync_PropagatesRealCancellation_InsteadOfReportingNotFound` from the observability
  review (fake source throws `OperationCanceledException(cancellationToken)` on a cancelled token →
  today returns null; green: propagates). Plus the negative: a source throwing a plain exception
  still degrades to null.

**Verify:** `dotnet test test/Benzene.Mesh.Test -c Release`. This WP has the largest behavioral
surface (#251) — run the WHOLE Mesh.Test project, not just filters, before committing.

---

## WP-F — Mesh fleet trace sources (#252 + XRay sibling)

**Files:** `src/Benzene.Mesh.Fleet.Jaeger/JaegerTraceSource.cs` (line ~130),
`src/Benzene.Mesh.Fleet.Tempo/TempoTraceSource.cs` (line ~90),
`src/Benzene.Mesh.Fleet.Aws.XRay/XRayTraceSource.cs` (`EnrichRecentFlowsAsync`/`FetchBatchAsync`'s
bare `catch { }`), tests in `test/Benzene.Mesh.Test/`.

**The finding.** The per-service/per-trace isolation filter
`catch (Exception ex) when (ex is not OperationCanceledException)` distinguishes "backend failed"
from "host cancelled" by exception TYPE — but `HttpClient.Timeout` on one slow backend throws
`TaskCanceledException` (an OCE subclass) with the caller's token never cancelled. That exception
escapes the isolation, faults the whole `BoundedFanOut` fan-out, and discards every other service's
already-fetched results — the #189 regression class reintroduced for one exception family. XRay's
bare `catch { }` has the inverse problem (swallows genuine cancellation), same root confusion.

**Ruling.** Replace type-based filters with token-verified ones in all three files:
`catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)`
— i.e. isolate everything EXCEPT an OCE while the caller's own token is actually cancelled. This
isolates a per-request HttpClient timeout (caller token not cancelled → caught, logged, that
service skipped) and still propagates genuine host cancellation. For XRay's bare catch, add the
complementary rethrow (`when` the token IS cancelled, propagate; otherwise keep degrading). Keep
each file's existing log lines/comments, updating the comment to name the timeout-vs-cancellation
distinction (cite `MessageHandler.cs`'s precedent).

**Red-green recipe.** Red (verified in the observability review):
`GetCorrelationAsync_IsolatesAPerServiceTimeout_NotTiedToTheCallersToken` — two Jaeger services, one
returns a trace, the other's handler throws `TaskCanceledException` (simulated HttpClient.Timeout),
caller token `None` → today the exception propagates and everything is lost. Green: the healthy
service's result survives, mirroring the adjacent already-passing `HttpRequestException` isolation
test. Clone for Tempo. Add the complementary test: caller's OWN token cancelled → OCE propagates
(both Jaeger and Tempo), and for XRay: cancelled token propagates instead of silently degrading.

**Verify:** `dotnet test test/Benzene.Mesh.Test -c Release --filter
"FullyQualifiedName~TraceSource"` then the whole Mesh.Test project.

---

## WP-G — Mesh.Dispatch (#254, #255)

**Files:** `src/Benzene.Mesh.Dispatch/MeshDispatchRateLimiter.cs` (lines ~83–94),
`src/Benzene.Mesh.Dispatch/MeshDispatchMessageHandler.cs` (lines ~116–122), tests in
`test/Benzene.Mesh.Test/MeshDispatchTest.cs`.

**The findings.** (#254) `Prune()` uses the unconditional two-argument
`_windows.TryRemove(pair.Key, out _)` from an enumeration snapshot — at a minute boundary it can
delete a CONCURRENTLY-INSTALLED fresh current-minute window, silently losing an increment and
letting more requests through than `MaxPerMinutePerIdentity`/`MaxPerMinutePerTarget` configure
(and Prune runs on every guarded dispatch request, so this is hot-path concurrency, not a timer
edge case). (#255) The `NotImplemented` exit path (registered service, rate limit passed, no
`IMeshServiceDispatcher` matching `entry.Source`) is the ONLY path that never calls `Audit(...)` —
and it's the most routine post-deploy misconfiguration.

**Rulings:**

1. (#254) Switch to the conditional `TryRemove(KeyValuePair<TKey,TValue>)` overload (.NET 5+
   compare-and-remove on key AND value), passing the exact pair the enumeration produced — a stale
   snapshot decision can then never delete a concurrently-replaced value. No other behavioral
   change.
2. (#255) Add the `Audit(...)` call on the NotImplemented branch with a distinct outcome label
   (e.g. `"no-dispatcher"`), same fields as the sibling exit paths, before returning the
   `NotImplemented` result.

**Red-green recipe.** Red (verified in the observability review):
- #254: the deterministic reconstruction from the review — drive `TryAcquire` twice across a
  simulated minute rollover, then apply the stale-snapshot removal exactly as `Prune()`'s loop body
  does (the review used reflection into `_windows` the same way the existing `WindowCount` test
  helper already does) → today the fresh window is deleted and a `limit: 1` request wrongly
  succeeds. Green: the conditional remove refuses the stale delete, the follow-up request is
  refused. If reflecting the internal op feels fragile, an alternative honest green test: hammer
  `TryAcquire`+`Prune` concurrently across a mocked minute boundary and assert the admitted count
  never exceeds the limit — but keep the deterministic one as primary.
- #255: `NoDispatcherRegisteredForSource_StillLeavesAnAuditRecord` from the review (registry has
  the service, zero dispatchers; expect one `benzene.mesh.dispatch.audit` entry) — fails today
  with zero invocations; green after.

**Verify:** `dotnet test test/Benzene.Mesh.Test -c Release --filter
"FullyQualifiedName~MeshDispatch"` — the #185/#186/#187 regression tests live here and must stay
green.

---

## WP-H — Schema/CodeGen (#263, #264, #265 minor)

**Files:** `src/Benzene.CodeGen.Client/OpenApiSchemaCSharpTypeBuilder.cs` (lines ~68–74),
`src/Benzene.Schema.OpenApi/JsonOpenApiSchemaBuilder.cs` (lines ~18–31),
`src/Benzene.CodeGen.Markdown/MarkdownTypeBuilder.cs` (`MapProperty`, lines ~71–88 and ~113–117),
tests in `test/Benzene.Core.Test/Autogen/` (including `CodegenOutputCompilesTest.cs`).

**The findings.** (#263) Discriminator `PropertyName` and each `mapping.Key` are interpolated
unescaped into generated `[JsonPolymorphic]`/`[JsonDerivedType]` C# string literals — the FOURTH
instance of the unescaped-interpolation-into-structured-output class (YAML #212, Markdown #86,
HCL #244), producing uncompilable client SDKs from a `"` in a discriminator value (7 cascading
Roslyn errors, verified) while the CLI reports success. (#264) `JsonOpenApiSchemaBuilder.Create`'s
switch has no `JTokenType.Float` or `JTokenType.Null` case — `{"price":3.14}` or
`{"middleName":null}` in example JSON throws `Exception("No map for Float"/"No map for Null")`
and aborts the whole document, reachable from the public `EventServiceDocumentBuilder.AddJsonEvent`.
Same crash-on-legit-input shape as #241/#242/#243. (#265) `MarkdownTypeBuilder.MapProperty`'s
empty-object `else` branch writes a bare `"{}"` with the property NAME dropped entirely — a
`Dictionary<string,int>`-shaped property renders as an anonymous `{}` line (and `{}[]` for arrays
of such).

**Rulings:**

1. (#263) Add a small local C#-string-literal escaper (backslash, double-quote, and control
   characters at minimum) mirroring the shape of `YamlValueEscaping`/`NameFormatter.EscapeHclString`
   — do NOT take a Roslyn dependency in `Benzene.CodeGen.Client` just for `SymbolDisplay.FormatLiteral`
   (Roslyn is only a transitive dep of the TEST project). Apply to `PropertyName` and every
   `mapping.Key`. Add a `CodegenOutputCompilesTest` theory case with an adversarial discriminator
   value (`12" wheel` from the review) — that test file already compiles emitted output with
   `CSharpCompilation`, which is the right oracle.
2. (#264) Add `JTokenType.Float => CreateNumberSchema()` (mirror `CreateIntegerSchema`,
   `Type = "number"`) and a `JTokenType.Null` branch returning the untyped/nullable placeholder
   convention #242 already established for `CreateArraySchema`'s "nothing to infer" case (read that
   fix and match it exactly — do not invent a new placeholder shape).
3. (#265) In both `else` branches, always emit `{CodeGenHelpers.Camelcase(name)}: ` before the
   placeholder; and where `AdditionalProperties != null`, render the map shape (e.g.
   `scores: {[string]: integer}`) the way `CSharpTypeName.GetName`'s comment (lines ~176–183 of
   `OpenApiSchemaCSharpTypeBuilder.cs`) already mandates for the C# generator.

**Red-green recipe.** All three red tests are spelled out with exact inputs/outputs in
`review-round16-schema-codegen-2026-08.md`: the discriminator-quote compile test (7 Roslyn errors →
0), the two one-liner `CreateSchema` crashes (`3.14` → number schema; `null` → placeholder, no
throw), and the `scores` map property rendering (`{}` anonymous → named + typed). Re-create them as
permanent tests in the matching existing test files.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Autogen|FullyQualifiedName~Schema"` — the #241/#242/#243, #212/#244, #86/#213,
#66/#67/#240 regression tests are the ones this WP must not disturb.

---

## Coordination notes

- **No two WPs share a source file.** WP-E/F/G all add files to `test/Benzene.Mesh.Test` (different
  test files — no conflict beyond the project's implicit glob); WP-C/H both add under
  `test/Benzene.Core.Test/Autogen/` (different subfolders). Every WP appends to
  `work/outstanding-bugs.md` at the same boundary (standard mechanical conflict) and most touch
  `docs/capability-matrix.md` (coordinator hand-reconciles).
- **WP-A ordering note:** the adapter fix (#266) will make #262's Microsoft-DI repro pass on its
  own. Fix and test BOTH anyway, per WP-A ruling 2 — the Redis fix is asserted directly (type
  implements both interfaces + disposal through a raw `ServiceCollection`-built provider without
  Benzene's adapter), so its test stays meaningful after #266 lands.
- **WP-B/WP-D adjacency:** WP-D's #261 makes `UseTimeout` around AWS sends actually work; WP-B's
  guard changes nothing about `TimeoutMiddleware`. No shared files; no ordering constraint.
- **The two committed evidence tests** (`MeshCollectorQueryCancellationTest`,
  `MeshCollectorSideBySideVersionTest`) belong to WP-E and MUST be inverted there — until WP-E
  merges, they pass by asserting the bug; after, they must pass by asserting the fix. The
  centralized baseline will catch a WP-E merge that forgets this (the tests would fail).
- **Two `[OPEN]` entries to record** (in `outstanding-bugs.md`, by WP-A and WP-B respectively):
  should `IServiceResolver` grow an async disposal contract (`await using` through
  `MiddlewareApplication`)? And: should `PollyResilienceMiddleware` ever support concurrent-attempt
  strategies via per-attempt isolation (performance review's option (b))?
- Findings the round explicitly did NOT file as fix tasks (no action this round, listed for
  completeness): `DynamoDbEventStore.SafeCurrentVersionAsync`'s unbounded diagnostic read
  (flagged low-confidence), `RabbitMqConnectionProvider`'s never-disposed health-check connection
  (minor, not DI-tracked), Avro's unwrapped enum-drift exception (documented-unsupported territory).
