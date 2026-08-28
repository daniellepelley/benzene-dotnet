# Rounds 14–15 fix designs — RULING + implementation plan

**Status:** ✅ **READY FOR IMPLEMENTATION** — design ruling, 2026-08-28. Covers the **entire open
findings backlog**: round 15's task board **#225–#275** (the comprehensive twelve-area pass,
[`bug-fix-designs-round15-2026-08.md`](bug-fix-designs-round15-2026-08.md)) **and** round 14's
still-open **#204–#223** ([`bug-fix-designs-round14-2026-08.md`](bug-fix-designs-round14-2026-08.md)),
which was never fix-designed. The round-summary tasks **#224**/**#276** close when their round's
findings doc is stamped actioned. This document does not restate the evidence — the findings docs
hold it; here are only the decisions, their rationale, and the rejected alternatives.

**This is a ruling document**, successor to
[`bug-fix-rulings-round12-13-2026-08.md`](bug-fix-rulings-round12-13-2026-08.md). An implementing
agent must not re-litigate a decision or "fix" it back the other way — if a design here doesn't
survive contact with the code, amend **this document's section in the same commit** as the divergent
implementation, stating why. Same anti-flip-flop discipline as
`work/settlement-consistency-fix-plan.md`.

**Rulings worth a maintainer glance before work starts** (each flagged again in place):
WP-B's disposal mechanism for the rate-limiter regression (#249); WP-F's Saga/Outbox behaviour
changes (#253–#257 + #208/#209 — durability semantics, the most behaviour-sensitive package in the
batch); WP-K's solution edits (`AGENTS.md` requires explicit approval — granted here, recorded
below); and §1's routing of the three mesh-UI behaviour findings (#205–#207) **out of this repo
entirely**, to the upstream `benzene-ui` repo.

---

## 0. Gate — before any fix package starts

**G1 — Re-verify on a real build first.** Round 15 was conducted with **no .NET SDK available**;
every finding is verified by reading, not execution, and its own §13 says findings are provisional
until a build confirms them. The fix round's first action, before any WP branches: run the full CI
baseline (`dotnet build Benzene.sln -c Release`; `test/Benzene.Core.Test`; `test/Benzene.Mesh.Test`;
`deploy/Mesh/Benzene.Mesh.Host.Test`; `Benzene.Examples.sln`) on the current branch head and record
the numbers. Any finding whose premise a green build/test contradicts is closed as
`[FALSE-POSITIVE]` in `outstanding-bugs.md` with one line of evidence — not silently dropped, and
not "fixed" anyway.

**G2 — Settle #228 in the same pass.** `MicrosoftBenzeneServiceContainer.Reopen()`'s
collection-initializer construct was flagged as looks-like-it-shouldn't-compile but couldn't be
checked without a compiler. The G1 build settles it: if it compiles, close #228 with a one-line
note explaining *why* it binds (find the extension method or conversion involved and name it in a
code comment so the next reader isn't stopped by the same puzzle); if it doesn't compile, the branch
was never buildable and that is finding zero.

**G3 — Fold the stragglers into the tracker.** Round 14's #204–#223 and round 15's #225–#275 are
absent from `outstanding-bugs.md`'s open section (round 15 §13 noted this for #210–#213
explicitly). WP-L adds them all under a "Tracked findings rounds 14–15" section as part of Batch
bookkeeping — the tracker and the task board must agree before per-WP `[RESOLVED]` entries start
landing.

---

## 1. Scope rulings

- **Spec:** nothing in either round touches `docs/specification/**`. No fixture changes, no
  re-vendoring, no cross-repo work.
- **The vendored UI is not editable in this repo — #205, #206, #207 route upstream.** Round 14's
  three mesh-UI behaviour findings (Refresh has no confirmation step; Sign-out has no pending state;
  Sign-out's fetch lacks an explicit `credentials` option) are changes to `mesh-ui.html` — which
  #204/#245 establish is a minified React bundle vendored verbatim from the external `benzene-ui`
  repo, guarded by a drift-check that fails CI if it is hand-edited. **Ruling: no agent edits those
  files here, ever.** The three findings are recorded in `outstanding-bugs.md` as
  `[UPSTREAM: benzene-ui]` with a pointer to this section, and are out of every WP below. What IS
  in scope here is #204/#245 — fixing the two `CLAUDE.md`s that invite exactly that forbidden edit
  (WP-J).
- **Solution edits approved.** WP-K adds projects/solutions for `AwsMesh`, `AzureMesh`, and
  `Outbox` (#272, #215). **This ruling is the explicit approval `AGENTS.md` requires**, same
  precedent as the round-12/13 ruling's #193 grant.
- **No new NuGet dependencies anywhere in this batch.** In particular, WP-G's XML depth guard is a
  hand-rolled `XmlReader` wrapper (the `BoundedBinaryDecoder` shape), not a package; WP-K's Helm
  validation is a CI step using the runner's own `helm`, not a repo dependency.
- **Public API:** additive only. The one deliberate behavioural exception — WP-B restoring
  container-owned disposal — is called out in its own section and in `CHANGELOG.md`.
- **Deletions:** WP-K deletes `examples/CodeGen/.../some.dll` (#273) — an unreferenced, unexplained
  committed binary; git history preserves it if it ever turns out to matter. No other deletions.

---

## 2. Cross-cutting principles this batch enforces

The recurring shapes across both rounds are instances of principles earlier rulings already
established — named here so each WP applies them deliberately:

- **P8 (a fix lands on every sibling)** is the dominant theme of this entire batch: #229/#234 are
  the TryAdd fix landing on the two transport families it missed; #232 is the self-hosted worker's
  abandon-guard landing on its Functions sibling; #244 is OAuth2's algorithm allowlist landing on
  its OIDC sibling; #260 is Avro's depth guard landing on XML; #263 is the source generator's
  `FormatLiteral` fix landing on three more code generators; #266/#267 are WP-3's NullLogger fix
  landing on the four classes its grep missed. **Every one of these WPs must end with a test that
  asserts the family, not the instance** — the round-11 `AwsGoogleTransportGetterOverrideTest`
  pattern.
- **The cancellation idiom is `ICancellationTokenAccessor`, seeded via `SeedCancellationToken`,
  threaded into every I/O call** — four independent areas found the same omission (#225, #236,
  #237, #268). WP-C fixes all four with one idiom and one shared test pattern (assert *which* token
  reaches the mocked transport — never `It.IsAny<CancellationToken>()` in the new tests; the
  existing inbound `PubSubCancellationTest` is the model).
- **P10 (a gate fails loudly)** governs #233 (silent broken binding), #235 (parse outside the
  isolation boundary), and #271 (a doc claiming no gate exists where one does).

---

## 3. Work packages

Evidence, exact file:line citations, and concrete failure scenarios for every item below are in the
two findings docs — implementers read the relevant section **in full** before coding.

### WP-A — mesh:report path traversal (#242) — LAND FIRST, ALONE

The one security finding, live on the documented default deployment. Two layers, both applied
(defence in depth, matching `ResolveWithinRoot`'s own stated threat model):
1. **Validate at the boundary:** `MeshReportMessageHandler` rejects a `report.Name` that is
   null/empty/whitespace, contains a path separator (`/` or `\`), or has a `.`/`..` segment —
   the same posture `MeshAnnotationsMessageHandler` already applies to its inputs. Rejection is a
   failure result, not a throw, matching the handler's existing error shape.
2. **Tighten the store:** `FileSystemMeshArtifactStore` gains subtree-aware resolution — a caller
   passing `services/<name>.json` asserts containment within `services/`, not merely the root.
   Smallest form: `ResolveWithinRoot` keeps its current contract, and
   `ArtifactStoreMeshReportPublisher` pre-sanitizes; but the test MUST pin the store level too
   (`"services/../manifest.json"` → rejected), because the store is the layer whose own doc claims
   to close this.
*Rejected:* sanitize-only-at-the-handler — leaves every future caller of the store one string-concat
away from the same bug; the store's doc already promises more than it delivers.
Tests: the two cases round 15 named (store-level `services/../manifest.json` rejected; end-to-end
`report.Name = "../manifest"` leaves `manifest.json` untouched), plus the flat-namespace stores
(S3/Blob/GCS) asserting `..` stays a literal segment — pinning that their immunity is real, not
assumed.

### WP-B — RateLimiting disposal regression + Polly cancellation (#249, #250, #251, #252)

- **#249 — restore reachable disposal without restoring the #133 collision.** ⚠ *Maintainer-glance
  design.* The mechanism, verified against this repo's actual seams:
  - Keep the direct closure capture for **use** (WP-5's collision fix stays — nothing resolves
    `RateLimiter` from the container, so shadowing remains impossible).
  - Each `UseInternallyOwnedRateLimiting` call additionally registers its created limiter for
    **disposal tracking** via an implementation-**factory** singleton of a small wrapper type
    (`OwnedRateLimiter`, holding the limiter and implementing `IAsyncDisposable`):
    `x.AddSingleton<OwnedRateLimiter>(_ => owned)`. MS DI's rule — factory-created singletons are
    container-disposed, provided instances are not — is exactly why the pre-#200 code did dispose
    and why a bare instance registration would not.
  - A factory singleton is only disposal-tracked once *instantiated*, and non-last descriptors of a
    shared type are only instantiated through `IEnumerable` resolution — so the middleware factory
    closure forces it: `_ = resolver.GetServices<OwnedRateLimiter>();` (the interface has
    `GetServices<T>` — verified) before constructing the middleware, gated behind a once-only flag
    per registration so it isn't paid per message.
  - `ownsLimiter` flips back to `false` for the internal path — the container owns disposal again;
    the middleware's own `DisposeAsync` path and its internal-API test become the BYO-only story.
    (This also removes a latent hazard the round-15 review implied but didn't number: with
    per-message middleware instances sharing one captured limiter, any `ownsLimiter:true` disposal
    of one instance would have killed the limiter for all subsequent messages.)
  - **Implementer validation (mandatory):** confirm `GetServices<T>` instantiates all descriptors
    under BOTH DI adapters (Microsoft + Autofac) with a disposal test each: build a pipeline via
    `UseFixedWindowRateLimiting` alone (public API only — the whole point of the finding), dispose
    the container, assert the limiter's `Timer` is dead. Re-read round 11's #133 and round 13's
    #200 before landing; update `RateLimiting/CLAUDE.md`'s disposal story and add a `CHANGELOG.md`
    line (behavioural: shutdown disposal restored).
  *Rejected:* pipeline-lifecycle disposal (`IMiddlewarePipeline : IAsyncDisposable`) — the
  principled long-term answer but a framework-wide feature, too large for a fix round; an
  `out RateLimiter` overload — pushes ownership onto every caller and fixes nothing for existing
  code. If the `GetServices` mechanism fails validation under either adapter, STOP and amend this
  section — do not improvise a third design silently.
- **#250 — Polly cancellation, two mandatory parts + one investigate:** (a) resolve
  `ICancellationTokenAccessor` (constructor-optional, the `HttpBenzeneMessageClient` idiom) and pass
  its token into `ResiliencePipeline.ExecuteAsync` — upstream cancellation now reaches Polly's
  strategies; (b) correct the package `CLAUDE.md` claim that this already happens. (c)
  *Investigate:* the concrete `CancellationTokenAccessor` is settable — so the middleware CAN
  re-seed the current scope with a token linking ambient + Polly's per-attempt token before invoking
  `next()`, restoring the prior token in a `finally`, which would make a Polly `Timeout` genuinely
  cancel downstream work that observes the accessor. Implement if it holds up under adversarial
  reading (re-entrancy: nested Polly middlewares, retry attempts each re-seeding); otherwise
  document the limitation honestly in the CLAUDE.md and XML docs ("Polly-initiated timeout does not
  cancel the wrapped work; compose `UseTimeout` inside for that") and record the choice here as an
  amendment. Either way: the missing `Timeout`-strategy test gets written.
- **#251** — fix the stale `Benzene.Resilience/CLAUDE.md` filter description (mechanical; the class's
  own XML remarks are the source of truth).
- **#252** — `RedisWildcardActions.InvalidateEntryAsync` returns `true` for "ran successfully, zero
  matches"; `false`/throw stays for real failures. One test: zero-match invalidate produces no
  warning.

### WP-C — the cancellation-threading sweep (#225, #236, #237, #238, #268, #270, #231)

One idiom, applied at six seams; each gets the assert-the-actual-token test.
- **#225 (core — the widest-reach item):** `MiddlewareRouter` can't thread the ambient token into
  its nested dispatch. **Decision:** add a protected overload seam — the router resolves
  `ICancellationTokenAccessor` from the `IServiceResolver` it already holds and passes the token to
  the existing 3-arg `MiddlewareApplication.HandleAsync(request, factory, token)` overload built for
  exactly this; the two concrete consumers (`BenzeneMessageEventHubHandler`,
  `BenzeneMessageQueueStorageHandler`) pick it up with no signature break (new virtual with a
  default delegating to the old path keeps third-party subclasses compiling). Test lands in
  `CancellationTokenSeedingTest` — the suite whose missing router case is why this survived 15
  rounds.
- **#236 (RabbitMQ mandatory publish):** `RabbitMqClientMiddleware` resolves the accessor and passes
  its token into `PublishMandatoryAsync` — the coordinator was already hardened to honor it; only
  the call site lags.
- **#237 (Kafka produce):** `KafkaClientMiddleware` switches to the 3-arg `ProduceAsync` overload
  with the accessor's token — the same package's dead-letter path already models it.
- **#238 (Kafka StopAsync):** thread the method's own `cancellationToken` into the drain/close wait
  (mirror `RabbitMqWorker.StopAsync`), so the host's stop-timeout can actually abort a hang.
- **#268 (nine single-send clients):** same accessor resolution in each `*ClientMiddleware`
  (Sns/Sqs/EventBridge/EventGrid/EventHub/QueueStorage/ServiceBus/AwsLambda) — constructor-optional
  parameter, wired through each `UseXClient` extension; `Http`/`Grpc` are the in-family reference
  implementations. One shared test pattern per client: seed a cancelled accessor, assert the SDK
  mock received that token.
- **#270 (minor):** the given-instance `UseHttpClient(httpClient)` overload wires the accessor like
  its DI-resolved sibling.
- **#231 (minor):** deferred — `SqsMessageClient` is the documented-minimal raw client; add the
  token only if it graduates. Recorded as `[DECISION: deferred]`, not silently dropped.
*Rejected for the whole WP:* adding `CancellationToken` parameters to `IBenzeneMessageClient` /
`IMiddleware` signatures — the ambient-accessor idiom exists precisely so public shapes don't churn;
a signature change is the flagged-breaking path and nothing here needs it.

### WP-D — DI TryAdd family sweep (#229, #234)

Mechanical, matching the SNS/SQS reference pattern: `AddScoped`→`TryAddScoped`,
`AddHeaderMessageVersionGetter`→`TryAddHeaderMessageVersionGetter` in
`Benzene.Aws.Lambda.Kafka` (#229) and the seven Azure Functions packages (#234:
QueueStorage/EventGrid/EventHub/Kafka/ServiceBus/Timer/AspNet — AspNet's `IMessageVersionGetter`
included). **Respect round 12's non-bug note:** `Benzene.Aws.Lambda.ApiGateway`'s plain `AddScoped`
enricher/response-handler chains are deliberately multi-registration — do not "fix" them. Tests: a
Kafka case added to `AwsGoogleTransportGetterOverrideTest`, and a new
`AzureFunctionTransportGetterOverrideTest` covering all seven (the family, not an instance).

### WP-E — Azure transport correctness (#232, #233, #235)

- **#232:** wrap the fallback-abandon in `OnExceptionCaughtAsync`/`CleanUpBeforeRethrowAsync` in its
  own try/catch-and-log, porting the self-hosted worker's guard (that worker's comment is the spec).
  Guard at the two hook *implementations*, and additionally at the base-class call sites — a future
  transport's hook shouldn't be able to reintroduce the masking. Tests: the double-fault cases round
  15 named, for both the Functions package and (as a regression pin) the already-correct worker.
- **#233:** new `BENZ0010` diagnostic for topic-XOR-subscription on the Service Bus trigger,
  following BENZ0003/0009's exact pattern; both asymmetric test cases.
- **#235:** move `EventGridTriggerEvent.Parse` inside the isolation boundary — parse within the
  handler path where `CatchExceptions` governs, converting a `JsonException` into the same
  per-event failure shape as any other poison event (settlement polarity per the settlement plan's
  table — EventGrid retains). Malformed-payload test for EventGrid, plus — seeding the broad gap
  round 15 called out — one malformed-input test each for the Kafka/QueueStorage/ServiceBus/EventHub
  getters in the same test pass.

### WP-F — Outbox / Idempotency / Saga durability (#253–#258, #208, #209) ⚠ maintainer-glance

The behaviour-sensitive package cluster; every ruling here changes what happens to data under
failure. Read round 15 §8 AND round 14 §2 in full first.
> **Amendment (2026-08-28, corrected before WP-F's implementation landed):** this section originally
> had #253 and #254 swapped relative to their actual assignment in
> `bug-fix-designs-round15-2026-08.md` §8 (#253 is the `OutboxDispatcher` settle-throw finding; #254
> is the `DynamoDbOutboxTransaction` drain finding). Corrected here to match the findings doc, which
> is the source of truth for finding identity.

- **#254 (DynamoDb outbox drains before write):** build the transact-item list from a
  **non-destructive peek**; drain only after `TransactWriteItemsAsync` succeeds. The existing
  over-100-items retry test is the model; add the thrown-write case (staged envelopes still present,
  retry commits them).
- **#253 (settle-throw → guaranteed duplicate):** split the try — a `SendAsync` failure keeps
  today's reschedule path; a `MarkDispatchedAsync` **throw after a successful send** is handled
  separately: log at error level with the envelope id, retry the settle once, and if it still fails
  leave the envelope for the sweeper **with an attempt-count/log signature that says
  "sent-but-unsettled"** rather than driving the full resend path as if the send failed. The
  inherent crash window stays (documented); the routine-transient-becomes-duplicate path closes.
  *Rejected:* swallowing the settle failure entirely — an unsettled envelope must remain visible to
  the sweep or it leaks.
- **#255 (EF settle throws `DbUpdateConcurrencyException`):** catch it in the three settle methods
  and return `false` — it means exactly what the documented `false` means (another claimant won the
  race). Test: reclaim-between-SELECT-and-SaveChanges.
- **#256 (idempotency release masks the original exception):** wrap `_store.ReleaseAsync` in its own
  try/catch inside `ReleaseAsync`; log-and-swallow the store failure; the original `throw;` always
  runs. Same for the `CompleteAsync` path if it shares the shape. This is the established
  settle-never-masks rule (round 5-6 #10, round 7-10 WP-N) landing on its last sibling.
- **#257 + #208 + #209 (Saga):** one coherent mini-design, since all three touch `RunOnceAsync`'s
  failure surface:
  - **#208 (failure path, round 14):** a state-store throw mid-run must not abort with zero
    rollback. Ruling: wrap state-store calls that occur after any effect-producing stage; on store
    failure, run compensation for completed stages as normal and surface the store failure inside
    the returned `SagaResult` (a new `StateStoreFailure` member — additive), not as a raw throw.
  - **#257 (success path, round 15):** `RecordFinishedAsync` failing after full success must not
    replace the result with a throw. Ruling: catch, log, and return the success `SagaResult` with
    the same additive `StateStoreFailure` populated — the caller learns both truths: the saga
    succeeded, and the record of it didn't persist. A caller that retries on exceptions no longer
    re-runs a succeeded saga.
  - **#209 (round 14):** concurrent same-stage failures — surface all of them: `SagaResult` gains a
    `Failures` list alongside the existing single `Failure` (kept, first-item, for compatibility),
    mirroring how `CompensationFailures` is already a list.
  ⚠ These are additive-API but real semantic changes to the package's contract — the maintainer
  should eyeball the `SagaResult` shape before it lands; `CHANGELOG.md` entry required.
- **#258 (minor):** add the negative-`expectedVersion` `ArgumentOutOfRangeException` guard to
  `InMemoryEventStore`, mirroring DynamoDb (#121's fix) — test/prod exception parity.
- **Coverage items:** the five named missing regression tests (one per finding above) plus #259's
  MapReduce gaps (empty shards; throwing reduce delegate; parallelism cap honored end-to-end).

### WP-G — Serialization + gRPC (#260, #261, #262)

- **#260 (XML recursion DoS):** a depth-counting `XmlReader` wrapper (override the read methods,
  increment on element start, decrement on end, throw a `BenzeneException` past a configured
  `MaxDepth`, default 32 — Avro's `MaxDepth` shape and default rationale). Note `Benzene.Xml` has
  **no options type today** (verified — the package is four files); WP-G creates `XmlOptions`
  following `AvroOptions`' pattern rather than hunting for one that doesn't exist. Hand-rolled,
  no new dependency. Tests: a deep recursive DTO payload rejected at the limit; a legitimate
  nested payload under it round-trips; the existing entity-expansion test still green.
- **#261 (gRPC case-fold):** case-fold `ReflectionGrpcMethodFinder`'s duplicate `GroupBy`
  (`OrdinalIgnoreCase` comparer) to agree with `GrpcRouteFinder`'s lookup; the case-variant
  duplicate now throws the intended `BenzeneException`. Test: the case-variant pair.
- **#262 (minor):** one doc-comment addition on `MessagePackSerializer`'s custom-options
  constructor: caller-supplied options must set `.WithSecurity(MessagePackSecurity.UntrustedData)`
  for untrusted payloads.

### WP-H — CodeGen escaping + round-14 stragglers (#263, #264, #265, #210, #211, #212, #213)

- **#263 + #212 (the systemic escaping sweep):** one pass over every emission site:
  - C# emission (`MessageClientSdkBuilder`, `OpenApiSchemaCSharpTypeBuilder`): use
    `SymbolDisplay.FormatLiteral` — the codebase's own proven fix
    (`MessageHandlerSourceGenerator.cs:360-366` is the model, comment and all).
  - YAML emission (`ApiGatewayBuilderV1` — #212): quote-and-escape scalar values (a small
    `YamlLiteral` helper: wrap in single quotes, double embedded single quotes) for every
    interpolated summary/path/tag.
  - HCL emission (`Benzene.CodeGen.Terraform`, all three builders): an `HclLiteral` helper —
    escape `"` and `\`, and neutralize `${`/`%{` (Terraform's live template syntax) per HCL's
    `$${`/`%%{` escaping. This is the finding's sharpest edge: unescaped `${` is *interpreted*,
    not just mangled.
  Tests: adversarial-content cases (quote, backslash, `${`, pipe, newline) per generator — the
  exact missing-coverage list round 15 §10 recorded.
- **#211:** case-fold `ApiGatewayBuilderV1`'s duplicate-route `GroupBy` on `Method` (the finder it
  mirrors already does, with a comment saying why).
- **#213:** null-guard `MarkdownTypeBuilder.MapProperty`'s `Items` (its sibling already does).
- **#210 (Autofac closed-generic):** change the six generic-routing checks from `IsGenericType` to
  `IsGenericTypeDefinition` so a closed-generic handler class behaves as under Microsoft DI. Test:
  the closed-generic handler case, run against both adapters.
- **#264 (minor):** keep `HealthCheckCommand.IsHealthy`'s documented lenient default but add
  `--strict` (unrecognized shape → unhealthy) so a CI gate can opt into P10; one test each mode.
- **#265 (minor):** escape `|` in `LambdaServiceMarkdownBuilder`'s table cells.

### WP-I — Clients null-logger completion + parallel middleware (#266, #267, #269 + coverage)

- **#266:** apply `logger ?? NullLogger<T>.Instance` to the three siblings
  (`RabbitMqBenzeneMessageClient`, `KafkaBenzeneMessageClient`, `GrpcBenzeneMessageClient`) —
  same mechanical change as WP-3's nine.
- **#267:** same for `StepFunctionsClient` + its factory. Then re-run the *shape* grep (unguarded
  `_logger.Log*` in a catch, repo-wide) rather than the name grep, and list any further hits in the
  `[RESOLVED]` entry — closing the methodology gap, not just the four known instances.
- **#269:** `catch (OperationCanceledException) { throw; }` ahead of `ParallelOutboundMiddleware`'s
  per-branch catch-all... **ruling refinement:** rethrowing from one branch abandons the others'
  results, so instead classify: a branch whose exception is an OCE *while the ambient token is
  cancelled* is recorded as a distinct cancelled outcome (and the aggregate surfaces cancellation),
  never folded into `UnexpectedError` text. Follow `HealthCheckError.Classify`'s
  ambient-vs-budget distinction. Test: cancelled-branch case (the suite currently has zero
  cancellation coverage).
- **Coverage debt (required in this WP):** direct failure-path + null-logger tests for the four
  never-tested clients (`EventGrid`/`EventHub`/`QueueStorage`/`ServiceBus`), and fix the gRPC test
  helper that silently substitutes a non-null logger so the real null path is exercisable.

### WP-J — Mesh auth/UI/docs/config (#244, #245, #204, #246, #247, #248)

- **#244:** port `OAuth2BearerOptions.Validate()`'s algorithm hardening to `MeshOidcOptions` —
  reject null/whitespace entries, reject `"none"` by name, validate against the same
  `KnownSigningAlgorithms` allowlist (share the constant rather than copying it, if project
  references allow; otherwise duplicate with a cross-reference comment). Tests mirror the OAuth2
  validation matrix.
- **#245 + #204:** rewrite both `Benzene.Mesh.Ui/CLAUDE.md` and `Benzene.Spec.Ui/CLAUDE.md` to lead
  with the vendoring reality: generated React bundles from `benzene-ui`, byte-identity guarded by
  `mesh-ui-drift-check.yml`, **never hand-edit**, where changes actually go. Feature narration may
  stay if reframed as "what the shipped bundle does"; every "vanilla JS"/"hand-rolled" claim goes.
- **#246 (minor):** back off `ReadCappedAsync`'s truncation point to the last complete UTF-8
  sequence boundary before appending the marker.
- **#247/#248 (minor):** expose `SearchConcurrency`/`CorrelationSearchLimit` (fleet) and the
  dispatch guard bounds + `MaxResponseBytes` through `mesh.json`, with `MeshConfigValidator` bounds
  checks (positive, sane ceilings) so the config surface catches up with rounds 12-13's options.
  Update `deploy/Mesh/CONFIG.md`.

### WP-K — Examples, CI truth, deploy (#271–#275, #214–#223)

- **#271:** rewrite `examples/CLAUDE.md`'s "How these build" to state what `build-benzene.yml`
  actually runs (the `examples-build` job; the nine in-memory example test projects) — the false
  no-CI claim is the root cause of this family of gaps.
- **#272:** give `AwsMesh` and `AzureMesh` build coverage. **Decision: per-folder `.sln`s**
  (mirroring `GoogleCloudMesh`/`AzureFunctionsMesh`) **plus a build-only CI job that builds all
  four mesh-example solutions** on push/PR — folding 8 cloud-deploy example projects into
  `Benzene.Examples.sln` would drag cloud-SDK restore into every contributor build, which is why
  those siblings got their own solutions in the first place. The CI job is the load-bearing half:
  a solution nothing builds is #272 with extra steps.
- **#214:** fix the `GoogleCloudMesh` build error (`MeshRegistry.FromEnvironment()`), now caught by
  the same new CI job.
- **#215:** `Outbox` example joins `Benzene.Examples.sln` (it is in-memory, no cloud SDKs — the
  main solution is right for it).
- **#216–#223 (round 14's remaining examples items):** document `GoogleCloudMesh` in
  `examples/CLAUDE.md` (#216); pin the Kafka compose image to the ZK-compatible tag its own test
  harness already pins (#217); move the Asp instrumentation key and dummy connection string to
  config placeholders with a comment (#218, #222); add the port-mismatch hint to the Asp JWT demo's
  401 path or README (#219); delete the orphaned `Benzene.Examples.App.Data` project (#220 — same
  deletion rule as #273); fix the CS8632 and ASP0001 warnings (#221, #223).
- **#273:** delete `some.dll`. **#274:** complete the own-solution list (now including the two new
  ones). **#275:** add a `helm lint` + `helm template --validate` CI step for
  `deploy/Mesh/helm/benzene-mesh/` (near-zero cost; the kind-cluster dry-run is optional stretch).

### WP-L — Core seams + bookkeeping (#225 lands in WP-C; here: #226, #227, #228 + tracker)

- **#226:** replace `GetInterface("IFilter\`1")` with the closed-interface enumeration the method's
  own selection predicate already uses; register each closed `IFilter<T>` separately. Tests:
  multi-interface filter class routes for both topics; plus the zero-filters and multi-class cases.
- **#227:** ruling on the contract question: **runtime mutation is supported** (that is what
  `MessageHandlerDefinitionIndex`'s version-stamp mechanism exists for) — so make it true: lock
  `Add`, snapshot under the lock in `FindDefinitions`, align `IMessageHandlersList`'s doc with the
  index's. *Rejected:* doc-only "startup only" — it would render the index's invalidation mechanism
  dead code and contradict its own remarks.
- **#228:** per gate G2.
- **Tracker bookkeeping (G3):** all rounds-14/15 findings into `outstanding-bugs.md`; both findings
  docs' Status lines flip to "fix designs ruled in this document"; per-WP `[RESOLVED]` entries as
  packages land.
- **Coverage debt parked here:** behavioral tests for `Benzene.Aws.Lambda.AspNet`'s four bridge
  classes (round 15 §2's zero-coverage finding — a real test-writing task, not a one-liner:
  v1/v2/ALB request through the real ASP.NET pipeline, and `BenzeneLambdaServer.StartAsync`'s
  documented ordering guarantee), `MiddlewareRouter.HandleAsync` behavior (lands with WP-C's #225
  test), `MessageHandlersList` direct tests (with #227), `HandlerPipelineBuilder.Add`'s
  incremental path, and a pinning test/doc note for `BoundedConcurrentDispatcher`'s unused token
  parameter.

---

## 4. Implementation plan

**Preconditions:** gate G1–G3 first, on `origin/main`-rebased branch state, with the recorded
baseline. The same no-SDK caveat that bound round 15 binds any implementing agent working in this
environment: if `dotnet` is unavailable locally, say so in every commit, keep changes
pattern-mirrored, and treat CI as the verification loop (`AGENTS.md`'s own instruction).

**Sequencing:**
1. **Gate** (G1–G3) — one agent, first, alone.
2. **WP-A** (security) — immediately after the gate, alone.
3. **WP-B** and **WP-F** next — the regression our own fix caused, and the durability cluster —
   each single-agent, sequential or parallel with each other but not with anything touching their
   trees.
4. **WP-C, WP-D, WP-E, WP-G, WP-H, WP-I, WP-J, WP-K, WP-L in parallel worktrees**, one agent each —
   trees are disjoint except `outstanding-bugs.md`/`CHANGELOG.md` (append-conflicts; keep both
   sides, the established pattern). Cap concurrency at what the host tolerates (2–3 builds).
   WP-C and WP-D both touch `Benzene.Aws.Lambda.Kafka` — WP-D's TryAdd edit is in
   `DependencyInjectionExtensions.cs`, WP-C's Kafka edit is in `Benzene.Kafka.Core` (the worker
   package), so they do not collide; the Azure overlap between WP-C(#225)/WP-E is
   EventHub/QueueStorage handler files vs EventGrid/ServiceBus/source-gen files — also disjoint,
   but merge WP-C before WP-E to keep the handler-file history linear.
5. Merge order within the parallel set otherwise unconstrained; each merges when its own tests are
   green in CI.

**Per-package definition of done** (unchanged from prior rulings): revert-verified red→green test
per code fix; the family-level test where P8 applies; XML docs + named `docs/*.md` pages +
`docs/capability-matrix.md` rows updated in the same package; `[RESOLVED]` entry per finding in
`outstanding-bugs.md` pointing here; task board updated; one logical change per commit; push with
retry/backoff.

**Round completion:** full baselines green (now including the new mesh-examples build job and helm
lint); both findings docs stamped actioned; docs-archivist moves them and this ruling to
`work/archive/`; #224/#276 closed. A follow-up blind re-audit of 2–3 of the heaviest-fixed areas
(Outbox, the cancellation sweep, CodeGen escaping) is the round-13-style verification this batch
deserves, once an SDK-equipped environment can run it.

**Amendment rule (repeat):** a design here that doesn't survive contact with the code is amended in
this document in the same commit as the divergent implementation — the record and the code never
disagree.

---

## 5. Task-number index

| Task(s) | WP | Ruling in one line |
|---|---|---|
| #242 | WP-A | Validate `report.Name` at the handler AND make the store subtree-aware; test both layers |
| #249 | WP-B | Closure capture stays for use; factory-registered `OwnedRateLimiter` + `GetServices` forcing restores container disposal |
| #250 | WP-B | Ambient token into `ExecuteAsync` (mandatory); linked per-attempt re-seed investigated; CLAUDE.md corrected |
| #251, #252 | WP-B | Stale doc fixed; zero-match invalidate returns `true` |
| #225 | WP-C | Router resolves the accessor, threads token to the existing 3-arg overload; non-breaking virtual seam |
| #236, #237, #238 | WP-C | RabbitMQ/Kafka outbound + Kafka StopAsync thread the tokens that already exist for them |
| #268, #270 | WP-C | Nine clients + the given-instance Http overload get the accessor idiom; tests assert the actual token |
| #231 | WP-C | Deferred, recorded — raw SQS client stays documented-minimal |
| #229, #234 | WP-D | TryAdd sweep to the two missed transport families; family-level override tests |
| #232 | WP-E | Fallback abandon guarded at hooks AND base call sites; double-fault tests both siblings |
| #233 | WP-E | New BENZ0010 topic-XOR-subscription diagnostic |
| #235 | WP-E | EventGrid parse moves inside the isolation boundary; malformed-input tests seeded across Azure getters |
| #253–#257, #208, #209 | WP-F | Outbox peek-then-drain, sent-but-unsettled path, EF `false`-on-race, release-never-masks, `SagaResult` gains `StateStoreFailure` + `Failures` |
| #258, #259 | WP-F | InMemory store parity guard; MapReduce coverage |
| #260 | WP-G | Hand-rolled depth-counting `XmlReader` wrapper, `MaxDepth` default 32 |
| #261, #262 | WP-G | Case-fold the duplicate detector; doc the MessagePack custom-options footgun |
| #263, #212 | WP-H | `FormatLiteral`/`YamlLiteral`/`HclLiteral` across every emission site; adversarial-content tests |
| #210, #211, #213 | WP-H | Autofac `IsGenericTypeDefinition`; ApiGateway case-fold; Markdown null guard |
| #264, #265 | WP-H | `--strict` healthcheck mode; Markdown pipe escaping |
| #266, #267 | WP-I | NullLogger to the 3 siblings + StepFunctions; re-grep by shape, not name |
| #269 | WP-I | Cancelled branch classified distinctly, never folded into `UnexpectedError` |
| #244 | WP-J | OAuth2's algorithm allowlist ported to `MeshOidcOptions` |
| #245, #204 | WP-J | Both UI CLAUDE.md docs rewritten around the vendoring reality |
| #246, #247, #248 | WP-J | UTF-8-safe truncation; fleet + dispatch options exposed via `mesh.json` |
| #205, #206, #207 | — | **Routed upstream to `benzene-ui`** — not fixable in this repo (§1) |
| #271, #272, #214, #215 | WP-K | CLAUDE.md CI truth; mesh-example solutions + build job; GoogleCloudMesh fix; Outbox joins the sln |
| #216–#223, #273, #274, #275 | WP-K | Round-14 example items; `some.dll` deleted; solution list completed; helm lint in CI |
| #226, #227 | WP-L | Closed-interface filter registration; `MessageHandlersList` made safe for its documented contract |
| #228 | Gate | Settled by the G1 build; outcome recorded either way |
| #224, #276 | — | Round summaries; close at round completion |
