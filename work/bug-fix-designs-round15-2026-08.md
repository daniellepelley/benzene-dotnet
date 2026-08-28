# Round 15 review findings (2026-08)

**Status: ACTIVE — findings only; fix designs now ruled in [`bug-fix-rulings-round14-15-2026-08.md`](bug-fix-rulings-round14-15-2026-08.md) (#225–#275) — not yet implemented.** This round is different in kind from every prior
round: it is the first genuinely **comprehensive** pass — twelve parallel agents, one per coherent
subsystem, together spanning every package under `src/`, every test project, and the full
`examples/`/`templates/`/`deploy/` tree, run simultaneously rather than picking untouched corners.
Each agent was required to look through two lenses on its area: correctness/robustness issues, and
missing test coverage as a first-class deliverable in its own right (not incidental to a bug hunt).
Each was also required to establish its area's most recent prior round and finding count before
reporting, so this document's counts are an honest area-by-area comparison, not raw totals inflated
by re-finding old ground. Findings are tracked as task board **#225–#275** (31 worth-fixing, 20
minor), plus a round-summary task **#276**.

**Environment constraint, honored throughout: no .NET SDK was available to any review agent.**
Rounds 1–14 relied on "execute real adversarial probes" as their verification standard; this round
could not — nothing below was compiled, run, or tested against a real build. Every finding is
**verified by reading**: full-file reads (not excerpts), call paths traced by hand, signatures
confirmed via `grep` before reasoning about behavior, and — where the finding was reasonably
central — cross-checked by the compiling session against the actual source a second time before
publication (see the "independently spot-checked" note below). This is a real reduction in rigor
relative to rounds 1–14's execution-backed standard, stated plainly rather than glossed over; treat
each finding's severity as provisional until a build/test pass confirms it.

**Independently spot-checked before publication:** four of the highest-consequence findings —
the `mesh:report` artifact-overwrite path-traversal (#242), the XML deserialize recursion gap
(#260), the Kafka DI-registration shadowing gap (#229), and the just-landed WP-5 rate-limiter
disposal regression (#249) — were re-read against current source directly by the compiling session,
independent of the reporting agent. All four confirmed exactly as reported.

---

## §0 Headline results

- **A real, exploitable security finding**: `mesh:report`'s untrusted `MeshServiceReport.Name` can
  overwrite the fleet-wide `manifest.json` (or any other top-level mesh artifact) on the default
  filesystem artifact store, via a one-level path-traversal-within-root that the store's own guard
  doesn't catch (#242). Live on any out-of-the-box deployment using the documented default store.
- **This session's own just-landed WP-5 fix introduced a real regression**: removing the #133 DI
  collision guard (per round 13's ruling) also removed the only path by which an internally-created
  rate limiter could ever be disposed — the leak #133 fixed is fully back for all three of the
  package's public, documented entry points (#249). Exactly the kind of second-order finding round
  13's blind-re-audit method exists to catch, found here by a *different* review targeting a
  *different* area than round 13's, independently.
- **A cross-cutting pattern, found independently by three separate agents in three separate areas**:
  outbound sends that never thread ambient cancellation into the underlying SDK/transport call, so a
  `.UseTimeout(...)` wrapper is silently a no-op. Found in RabbitMQ's mandatory-publish coordinator
  and Kafka's producer (#236, #237), nine of eleven single-send outbound clients (#268), and one
  message-routing seam in the core pipeline itself — Azure Functions Event Hub/Queue Storage envelope
  routing (#225). Four independent instances of the same gap, none aware of the others when found.
- **The largest concentration of worth-fixing findings landed in `Benzene.Outbox`** (5 of 5 in §8),
  a package effectively never reviewed before this round — including a transactional DynamoDB commit
  that destructively drains its staged buffer *before* the write it's committing, so a thrown (not
  just oversized) write permanently loses the staged envelopes with no diagnostic signal (#253).
- **A doc that actively misled reviewers for years, explaining a real gap**: `examples/CLAUDE.md`
  flatly claims examples are "NOT part of the primary CI gate" — false, `build-benzene.yml` has a
  real `examples-build` job — and that false claim is precisely why `AwsMesh` (7 projects) and
  `AzureMesh` (1 project) being members of *no* solution file anywhere went unnoticed: nothing
  signals that exclusion means zero verification rather than the same manual tier every example gets
  (#271, #272).
- **The #204 vendored-UI-doc bug (round 14, still unfixed) has a byte-identical sibling instance**
  nobody had looked for: `Benzene.Spec.Ui/CLAUDE.md` makes the identical false "hand-written vanilla
  JS" claim about a file proven byte-identical to the already-known-vendored `mesh-spec-ui.html`
  (#245).
- **Every re-verification of prior rounds' fixes came back clean** except the one WP-5 regression
  above — rounds 1–14's fixes across event sourcing, mesh discovery/catalog, settlement policy,
  auth, gRPC deadline propagation, and the Avro/MessagePack/XML-BOM serialization hardening are all
  still correctly and completely in place, re-checked by fresh adversarial reasoning rather than
  trusted from the record.

---

## §1 Core / Abstractions / Middleware / Pipeline / DI / Hosting / Configuration — #225–#228

**Baseline:** no prior round has reviewed this exact combination as one unit. Closest analogues:
round 10 WP-Y (1 finding, host/entry-point cancellation, fixed), round 7-10 WP-P/WP-S/WP-Q/WP-R
(~12 findings across Core.Middleware/MessageHandlers version-blindness, HostedService/Http/AspNet.Core
hosting, Autofac DI parity, TestHelpers — all fixed at the time). `Benzene.Autofac`'s round-14
findings (#210-213) re-confirmed still present, not re-litigated — folding into the tracker instead.
`Benzene.Configuration.Core`, `Benzene.Results`, `Benzene.Testing`, `Benzene.Microsoft.Dependencies`,
and most of `Benzene.Core.Versioning`/`Benzene.Http` had never been independently reviewed by name —
found clean. **This pass: 2 worth-fixing, 2 minor**, landing precisely in the two places pushed
hardest: cancellation-token propagation through a pipeline boundary, and a DI-registration
extension's reflection edge case.

**Worth-fixing:**
- **#225 — `MiddlewareRouter<TRequest,TContext>` has no way to forward the ambient cancellation
  token into the nested application it dispatches to — envelope-routed messages on Azure Functions
  Event Hub/Queue Storage silently run with `CancellationToken.None`.**
  `src/Benzene.Core.Middleware/MiddlewareRouter.cs:33-52` — `HandleFunction`'s abstract signature has
  no `CancellationToken` parameter anywhere, and the router only stores an `IServiceResolver`. Two
  concrete consumers reachable outside AWS Lambda (which has no cancellation signal by design):
  `BenzeneMessageEventHubHandler`/`BenzeneMessageQueueStorageHandler` both call the 2-arg
  `BenzeneMessageResultApplication.HandleAsync` overload, which internally forwards
  `CancellationToken.None` — the 3-arg overload that would seed the real token structurally can't be
  reached. Both transports correctly seed the real host cancellation token into the **outer**
  per-message DI scope, but `UseBenzeneMessage(...)` envelope routing (the pattern the
  getting-started guide shows for both) creates a **second, independent** DI scope for the inner
  pipeline, unconditionally seeded with `CancellationToken.None`. Any component inside that inner
  pipeline resolving `ICancellationTokenAccessor` — `.UseTimeout(...)`, cooperative cancellation, the
  framework's own documented "genuine cancellation propagates so a transport redelivers interrupted
  work" contract — silently never observes a host shutdown/drain signal on this specific routing path.
  Same bug class as WP-1's #185 (`MeshDispatchMessageHandler`'s hardcoded `CancellationToken.None`),
  at a seam that fix didn't touch. `CancellationTokenSeedingTest.cs` — the suite that thoroughly
  covers token seeding elsewhere — has no case for a router-based nested dispatch, exactly why this
  went unnoticed.
- **#226 — `Filters.DependencyExtensions.AddFilters` crashes with `AmbiguousMatchException` for
  any class implementing `IFilter<T>` against more than one `T`.**
  `src/Benzene.Core.MessageHandlers/Filters/DependencyExtensions.cs:58-71` uses
  `filterType.GetInterface("IFilter\`1")` — matches by simple name only, not by closed generic
  arguments. A legitimate, natural multi-topic filter class (`class OrderFilters : IFilter<Created>,
  IFilter<Updated>`) has two interfaces with that identical simple name, so `GetInterface(string)`
  throws `AmbiguousMatchException` — not a Benzene exception, no actionable message — at process
  startup, taking the whole app down. Nothing in the framework or docs says "one `IFilter<T>` per
  class." Fix: iterate `GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() ==
  typeof(IFilter<>))` (the predicate `AddFilters` already uses to select `filterTypes`) and register
  each closed interface separately. No test exercises this at all.

**Minor:**
- **#227** — `MessageHandlersList` (a DI singleton) uses a plain, non-thread-safe `List<T>` for
  both `Add` and `FindDefinitions`/`ToArray()`, despite its own consumer's doc explicitly framing
  post-startup runtime mutation as a supported scenario (`MessageHandlerDefinitionIndex`'s
  version-stamp invalidation mechanism exists specifically to support it). No live production caller
  currently invokes `Add` after startup, so this is latent rather than reproducible today — but the
  interface doc and the consumer's doc contradict each other on the contract, and only the "startup
  only" reading is actually safe.
- **#228 — flagged for a compiler check, not asserted as a bug** — `MicrosoftBenzeneServiceContainer
  .Reopen()` (`MicrosoftBenzeneServiceContainer.cs:20-23`) does `_services = new ServiceCollection {
  _services }`, which by ordinary collection-initializer semantics calls `Add(_services)` against an
  `Add(ServiceDescriptor)`-only overload — `IServiceCollection` isn't implicitly convertible to
  `ServiceDescriptor`. Read in isolation this looks like it shouldn't compile. Against that: `Reopen()`
  is genuinely exercised production code with a purpose-built regression test, on a branch already
  carrying rounds 1-14's CI-verified fixes — strong evidence it compiles and something is being missed
  by reading alone. Surfaced rather than asserted either way; worth a `dotnet build` glance.

**Missing test coverage:** `MiddlewareRouter.HandleAsync` has zero behavioral test coverage (only its
`Name` property is tested) — directly why #225 went unnoticed. `AddFilters` has exactly one
end-to-end scenario (single filter/single request type) — no multi-interface, zero-filter, or
multi-class case, directly why #226 went unnoticed. `MessageHandlersList` has no direct unit test
of its own. `HandlerPipelineBuilder.Add`'s incremental-registration-after-first-build path (the exact
scenario its own cache's documented "known limitation" warns about) is untested.
`BoundedConcurrentDispatcher<T>`'s `handle` delegate is always invoked with `CancellationToken.None`
(every real caller ignores it and closes over its own token instead) — not a bug, but undocumented
and untested, so a future caller could easily assume the passed token is live when it never is.

## §2 AWS transports (Lambda + self-hosted) — #229–#231

**Baseline:** S3/SNS/EventBridge/PubSub DI+getters/Kinesis checkpointer/S3 keys — round 11 §5
(8 findings, #158-165), all re-verified correct, 0 regressions. TestHelpers/NullLogger — rounds
12-13 WP-3 (2 findings), re-verified fixed. Settlement policy — this session's own WP batches,
re-traced by hand against the plan's table and confirmed exact, including the Kafka carve-out.
API Gateway/Hosting/AspNet — round 10 (7 findings), 0 new correctness issues. XRay — not previously
reviewed by name. **This pass: 2 worth-fixing, 1 minor.**

**Worth-fixing:**
- **#229 — `Benzene.Aws.Lambda.Kafka`'s `AddKafka()` is a missed instance of round 11's #160
  defect class: a user's custom Kafka getter is silently shadowed.**
  `src/Benzene.Aws.Lambda.Kafka/DependencyInjectionExtensions.cs:28-32` uses plain `AddScoped`/
  `AddHeaderMessageVersionGetter` instead of `TryAdd*`. Round 11's #160 fixed exactly this shape for
  S3/DynamoDb/EventBridge/PubSub (MS DI is last-wins; `ConfigureServices` runs before `Configure`) and
  named SNS/SQS as the reference-correct pattern — but Kafka was never named in that fix's scope, and
  its regression test (`AwsGoogleTransportGetterOverrideTest`) has no Kafka case. Concrete failure: an
  app registering a custom `IMessageHeadersGetter<KafkaContext>` before `.AddKafka()` has it silently
  overridden with no error.
- **#230 — `XRayMiddlewareDecorator.Tag()` runs unguarded inside the same try as the pipeline call
  — an annotation-resolution failure aborts the actual message handler, on every stage, whenever
  X-Ray is active.** `src/Benzene.Aws.Lambda.XRay/XRayMiddlewareDecorator.cs:57-73`: `Tag()`'s
  resolver calls (`GetVersionedTopic`, `FindHandler`) are not wrapped in the `Safe()` helper every
  other X-Ray SDK call here uses — only `AddAnnotation`/`EndSubsegment`/`AddException` are. If a
  custom `IMessageGetter<TContext>`/`IVersionSelector` throws, the exception is caught *before
  `_inner.HandleAsync` ever runs* — the actual pipeline stage never executes. Unlike its sibling
  `ActivityMiddlewareDecorator`, which gates resolver calls behind a scoped state so only the first
  span pays this risk (and carries an explicit documented acknowledgement of it, WP-N/#54), XRay wraps
  *every* middleware with no such gating or acknowledgement — a tracing toggle can silently break
  message processing.

**Minor:**
- **#231** — `Benzene.Aws.Sqs.Client.SqsMessageClient.PublishAsync` (`SqsMessageClient.cs:64`)
  takes no `CancellationToken`, unlike every other `*BenzeneMessageClient`; consistent with the
  package's documented minimalism, flagged only in case this client is ever promoted to a first-class
  path.

**Missing test coverage:**
- `Benzene.Aws.Lambda.AspNet` has **zero behavioral test coverage** across all four source files
  (`BenzeneAspNetBridge.cs`, `.RestBridge.cs`, `.AlbBridge.cs`, `BenzeneLambdaServer.cs`) — the only
  test in the package asserts DI registration, never invokes a bridge's `HandleAsync` or
  `BenzeneLambdaServer.StartAsync`'s documented load-bearing ordering guarantee.
- No regression test for #229 (the pattern already exists in `AwsGoogleTransportGetterOverrideTest`,
  just needs a Kafka case) or #230 (`XRayMiddlewareTest` has no case where the resolver itself throws
  during `Tag()`).

## §3 Azure transports (Functions + self-hosted) — #232–#235

**Baseline:** round 5-6 WP-5 (3 findings), round 7-10 WP-C (5 findings, source generator — all
re-verified intact), round 11 §5 (2 of 8 findings touched this scope, both still correctly fixed).
No Azure-scoped work in rounds 12-14. This session's own settlement work re-verified correctly and
completely applied across all five `AzureFunctionBatchApplicationBase` consumers, no gaps.
**This pass: 4 worth-fixing, 0 minor.** Two of the four are notably "the other half of a fix that
already landed for a sibling package but was never carried across."

**Worth-fixing:**
- **#232 — `ServiceBusBatchApplication`'s fallback-abandon can mask the original failure — no
  try/catch around it, unlike its self-hosted sibling which was deliberately fixed for exactly this.**
  `src/Benzene.Azure.Function.ServiceBus/ServiceBusApplication.cs:160-174` — both
  `OnExceptionCaughtAsync`'s fallback abandon and `CleanUpBeforeRethrowAsync` call
  `AbandonMessageAsync` with no try/catch. If that abandon itself throws (very plausible — it's
  invoked precisely because something already went wrong, e.g. slow processing past lock duration),
  under `CatchExceptions=true` the real failure is never logged and the new exception fails the whole
  batch invocation; under the default `CatchExceptions=false`, the abandon failure silently replaces
  the original exception in what's reported to the host. **This is the exact failure mode the
  self-hosted sibling `BenzeneServiceBusWorker.HandleMessageAsync` deliberately guards against**, with
  an explicit comment explaining why — the Functions-triggered package never received the equivalent
  fix. Confirmed by test gap: the existing fallback-fires test never makes the fallback *also* fail.
- **#233 — the trigger source generator: a Service Bus topic trigger missing `SubscriptionName`
  (or vice versa) silently emits a broken binding.** `src/Benzene.Azure.Function.SourceGenerators/
  Transports/MessagingTransports.cs`, `ServiceBus.Read` — `[BenzeneServiceBusTrigger(TopicName =
  "audit")]` with subscription omitted passes both existing checks (topic isn't empty; queue isn't
  set so the queue+topic warning doesn't fire) and generates
  `[ServiceBusTrigger("audit", "", Connection = ...)]` — syntactically valid, broken at deployment,
  far from the point of misconfiguration. Exactly the class of bug BENZ0002-0007 exist to prevent for
  every other required field on every other transport; this asymmetric pair was missed.
- **#234 — seven Azure Functions packages register their message-handler seams with plain
  `AddScoped`, silently shadowing a caller's own registration — the identical defect class round 11's
  #160 fixed for AWS/GCP and a prior pass fixed for the self-hosted Azure workers, but never carried
  to the Functions-triggered Azure family.** `QueueStorage`/`EventGrid`/`EventHub`/`Kafka`/
  `ServiceBus`/`Timer`/`AspNet`'s `DependencyInjectionExtensions.cs` all use `AddScoped`/
  `AddHeaderMessageVersionGetter` instead of the `TryAdd*` overloads that already exist for this exact
  purpose — vs. the already-correct self-hosted siblings `Benzene.Azure.ServiceBus`/`.EventHub`. An
  archived customization-robustness review explicitly recorded "Azure.Function.*... out of scope this
  pass" and it was never revisited. No `AzureFunctionTransportGetterOverrideTest` exists, unlike the
  AWS/GCP sibling test it was modeled on.
- **#235 — `EventGridTriggerEvent.Parse` throws uncaught on malformed JSON, bypassing
  `EventGridOptions.CatchExceptions` entirely.** `src/Benzene.Azure.Function.EventGrid/
  EventGridTriggerEvent.cs:53` — `JsonDocument.Parse(json)` with no try/catch, evaluated as a method
  argument in `Extensions.cs:84` **before** `AzureFunctionBatchApplicationBase.ProcessItemAsync`'s
  catch clause is ever reached. Every other Azure batch transport constructs its context directly from
  the SDK type with no parsing required; EventGrid is the one transport that must parse JSON before
  routing, and that parse sits outside the isolation boundary its own `CatchExceptions` option
  documents — the option set specifically to isolate one poison event does not protect against the
  input shape most likely to be poison. No malformed-JSON test exists anywhere in scope.

**Missing test coverage:** double-fault settle scenarios (#232), the topic/subscription asymmetry
(#233), getter-override tests for the seven Functions packages (#234), and — the broadest gap
—malformed-input tests generally: across all of `test/Benzene.Core.Test/Azure/`, essentially no test
exercises a malformed payload for any getter/context type, not just EventGrid's.

## §4 GoogleCloud + self-hosted RabbitMQ/Kafka — #236–#241

**Baseline:** RabbitMq — round 7-10 WP-A (3 findings, #30/#33/#45, all fixed and intact). Kafka.Core —
round 10 WP-AE (2 findings, #118/#119, fixed and intact). GoogleCloud.Functions.PubSub — round 10
(7 findings across Pub/Sub + sibling getters, all fixed and intact — 0 new issues, every getter/
converter matches documented hardened behavior exactly). GoogleCloud.Functions.Core/.Http — no prior
dedicated review, 0 issues found. Settlement polarity for PubSub and RabbitMq re-verified intact, not
re-reported. **This pass: 3 worth-fixing, 4 minor.**

**Worth-fixing:**
- **#236 — RabbitMQ outbound `mandatory: true` publishes ignore ambient cancellation entirely; the
  whole cancellation-support the round-7-10 WP-A hardening built is unreachable in production.**
  `src/Benzene.RabbitMq/RabbitMqSendMessage/RabbitMqClientMiddleware.cs:83-86` always passes
  `CancellationToken.None` into `PublishMandatoryAsync` — `RabbitMqClientMiddleware` never resolves
  `ICancellationTokenAccessor` at all, despite the coordinator itself being specifically hardened to
  accept and honor a token. Compare the established idiom elsewhere in the estate (`GrpcBenzeneMessage
  Client`, `HttpClientMiddleware`, both explicitly resolve the accessor). A `.UseTimeout(2s)`-wrapped
  send against a stuck broker is held for the coordinator's own 30s default `publishConfirmTimeout`
  regardless — the outer, tighter deadline is silently ineffective; the same for shutdown cancellation.
- **#237 — Kafka outbound produce ignores ambient cancellation too, and inconsistently with its own
  package's dead-letter producer.** `src/Benzene.Kafka.Core/Kafka/KafkaClientMiddleware.cs:24` uses
  the 2-arg `ProduceAsync(topic, message)` overload with no token; the same package's
  `BenzeneKafkaWorker.cs:349` correctly uses the 3-arg token-accepting overload for its dead-letter
  producer — confirming the safe overload exists and is used correctly elsewhere, just not on the
  outbound client path. Bounded only by the producer's `delivery.timeout.ms` (Confluent.Kafka default
  5 minutes) — a stuck broker silently blocks up to 5 minutes with no way to observe pipeline
  cancellation. Both #236/29 corroborated by test evidence: every relevant mock setup uses
  `It.IsAny<CancellationToken>()`, so which token actually reaches the transport is never asserted.
- **#238** — `BenzeneKafkaWorker.StopAsync` (`BenzeneKafkaWorker.cs:486-494`) ignores its own
  `cancellationToken` parameter (the host's stop-timeout token) entirely — if `DrainAsync`/
  `_consumer.Close()` hangs, the host's stop-timeout has no way to abort it. Contrast with
  `RabbitMqWorker.StopAsync`, which does thread its token into its own shutdown calls.

**Minor:**
- **#239** — `RabbitMqWorker` shutdown race: if `BasicCancelAsync` throws during `StopAsync`
  (caught, logged, not rethrown) and a new delivery arrives after `_dispatcher` is nulled, the
  delivery is neither enqueued, acked, nacked, nor logged — it vanishes from the worker's own
  accounting (not silent broker-side loss — it will eventually redeliver — but unlogged locally).
- **#240** — Kafka's dead-letter retry loop has no `OperationCanceledException` carve-out, so a
  record in flight at shutdown gets logged as a handler failure and an attempted (also-cancelled)
  dead-letter — converges to safe but produces alarming log noise on every clean shutdown with
  dead-lettering enabled.
- **#241** — `RabbitMqWorker`/`BenzeneKafkaWorker<K,V>` both implement `IDisposable`, but nothing
  in the standard hosting path (`CompositeBenzeneWorker`, `BenzeneHostedServiceStartup`) ever calls
  `Dispose()` on the concrete worker — dead `IDisposable` surface in the shipped hosting flow; low
  practical impact since the CTS involved are process-lifetime scoped.

**Missing test coverage:** `RabbitMqWorker.StopAsync` has zero test coverage at all (never called in
`RabbitMqWorkerTest.cs`) — no test covers the cancel/drain/close/dispose sequence, a mid-shutdown
exception, or the `StartAsync`-failure rollback path. No test anywhere asserts which
`CancellationToken` reaches the RabbitMQ/Kafka outbound transport call — the direct cause of
#236/29 going uncaught (a single test mirroring the existing `PubSubCancellationTest.cs`, which
already does this correctly for the *inbound* Pub/Sub side, would have caught both).

## §5 Mesh — collector/aggregator/discovery/catalog/wire/artifacts/usage — #242–#243

**Baseline:** round 11 §4 (10 findings, #148-157) — all ten independently spot-checked against
current source and confirmed still fixed, no regressions. **This pass: 1 worth-fixing, 1 minor.**

**Worth-fixing:**
- **#242 — `mesh:report`'s untrusted `MeshServiceReport.Name` can overwrite sibling top-level
  artifacts on the default filesystem store, not just create a bogus `services/*.json` entry.**
  `ArtifactStoreMeshReportPublisher.PublishAsync` (`src/Benzene.Mesh.Aggregator/
  ArtifactStoreMeshReportPublisher.cs:39`) builds the key as `$"services/{report.Name}.json"` with NO
  validation anywhere on the path from the wire. `FileSystemMeshArtifactStore.ResolveWithinRoot`
  (`FileSystemMeshArtifactStore.cs:67-84`) — the only defense — checks the *final resolved path stays
  inside the store root*, not that it stays inside the `services/` subtree the caller intended: for
  `report.Name = "../manifest"`, `"services/../manifest.json"` normalizes to `{root}/manifest.json` —
  still inside root, so the guard passes. The same trick reaches `topics.json`, `topology.json`,
  `asyncapi.json`, `usage.json`, `annotations.json`, `registry.json` — every top-level artifact the
  mesh publishes. **Anyone who can call the documented, opt-in `POST /mesh/report` endpoint with
  `{"name": "../manifest", ...}` overwrites the fleet-wide `manifest.json`** with attacker-chosen JSON,
  corrupting or spoofing the whole published catalog. `FileSystemMeshArtifactStore` is the package's
  documented default, so this is live out-of-the-box. **Not exploitable** against the S3/Blob/GCS
  stores (flat object namespaces where `".."` is a literal segment, not traversal) — specific to the
  shipped default. `ResolveWithinRoot`'s own doc comment names this exact threat model but the check
  is one level too coarse (root-containment, not intended-subtree-containment). Fix shape: validate
  `report.Name` at the handler (reject empty/whitespace/path separators/`.`/`..` segments — the same
  posture `MeshAnnotationsMessageHandler` already applies to its own inputs).

**Minor:**
- **#243** — `MeshCollectorStore`'s ring buffer (`MeshCollectorStore.cs:146-203`) throws
  `ArgumentOutOfRangeException` uncaught if constructed with `maxTraceEvents: 0` and then given events —
  no legitimate deployment would do this, low priority, not re-verified against a live repro.

**Missing test coverage:** the exact gap behind #242 —
`FileSystemMeshArtifactStoreTest.PublishAsync_PathEscapingRoot_IsRejected` only covers paths that
escape the store root entirely; no test exercises a path that stays inside root but escapes the
`services/` subtree. Everything else surveyed (single-writer gate, manifest-last ordering, partial-
failure isolation across discovery/usage/trace sources, K8s pagination, SSRF hardening) has solid,
purpose-built coverage — no `[Skip]`/commented-out tests found anywhere in scope.

## §6 Mesh — dispatch/fleet trace backends/auth/UI — #244–#248

**Baseline:** round 12 §1-2 + round 14 §1 + round 11 §7 (auth) — 62 findings combined across this
area's packages historically. **WP-1 (#185-187+response-cap) and WP-2 (#188-190), landed this
session, were re-verified in full and found correctly and completely applied with no gaps or
drift from the ruling doc anywhere.** **This pass: 1 worth-fixing new finding, plus round 14's
#204 confirmed still open with a newly-found second instance in a sibling package, plus 3 minor.**

**Worth-fixing:**
- **#244 — `MeshOidcOptions.Validate()` doesn't actually implement the algorithm-confusion
  protection its own doc claims parity with.** The doc says `ValidAlgorithms` is required "for the
  same algorithm-confusion reason as `OAuth2BearerOptions.ValidAlgorithms` (RFC 8725 §3.1)" — but
  `Validate()` only checks non-empty. `OAuth2BearerOptions.Validate()` (round 11 #174) additionally
  rejects null/whitespace entries, **explicitly rejects "none" by name**, and checks against a curated
  allowlist. None of that exists here — `ValidAlgorithms = new[] { "none" }` or a typo like `"RS266"`
  is silently accepted at wire-up. Today defense-in-depth rather than an active bypass
  (`RequireSignedTokens = true` still rejects an actually-unsigned token regardless), but a genuine
  sibling-parity gap round 11's own pass on this file missed, and the doc is actively wrong that the
  two classes are equivalent.

**Round 14's #204 status, and a new sibling instance:**
- `src/Benzene.Mesh.Ui/CLAUDE.md` re-verified: zero mention of the vendoring relationship, still
  narrates `mesh-ui.html` as hand-written vanilla JS throughout ~700 lines. **Still open exactly as
  round 14 left it** — not re-numbered, per instructions not to re-report an already-tracked item.
- **#245 — `src/Benzene.Spec.Ui/CLAUDE.md` has the identical bug as round 14's open #204, never
  previously flagged** — round 14 scoped only `mesh-ui.html`/`mesh-spec-ui.html`, never examined
  `Benzene.Spec.Ui`. Proven with a byte comparison, not inference: `spec-ui.html` and
  `mesh-spec-ui.html` are **byte-identical** (260,043 bytes each) — a third vendored copy of the same
  benzene-ui build the drift-check workflow already covers by pattern-match, with a doc claiming it's
  hand-written vanilla JS.

**Minor:**
- **#246** — `HttpMeshServiceDispatcher.ReadCappedAsync` truncates at a raw byte boundary before
  UTF-8 decoding — a response cut mid-multi-byte-character produces a `U+FFFD` glyph before the
  truncation marker in what the package calls the "audit-visible" record. Cosmetic, not a crash.
- **#247 / #248** — `MeshSourceRegistrar` and `MeshDispatchConfig` don't expose round 12-13's
  new `SearchConcurrency`/`CorrelationSearchLimit`/`MaxResponseBytes` options through `mesh.json` —
  an operator can only reach the hardcoded C# defaults, not incorrect (defaults are conservative) but
  a config-schema-lagged-code gap in both the fleet and dispatch config surfaces.

**Missing test coverage:** none found beyond the above — this area's coverage is genuinely strong
(98 auth test cases including a real loopback OIDC provider signing genuine RS256 tokens, CSRF
tamper-detection, DI-scope isolation; full dispatch gate/audit/rate-limit/cancellation matrix; real
Kestrel-hosted acceptance tests for the deployable). The one gap that exists — no "none"/malformed-
algorithm test case — mirrors #244's missing code exactly. No `[Skip]`/commented-out tests found.

## §7 HealthChecks + Cache + RateLimiting + Resilience — #249–#252

**Baseline:** round 11 (15 findings) + round 13's blind re-audit (5 findings, fixed as WP-5).
**This pass: 2 worth-fixing, 2 minor.** One worth-fixing finding is a **second-order regression
introduced by this session's own WP-5 fix** — exactly the class of finding round 13's method exists
to surface, found by hand-tracing the middleware-instantiation lifecycle, not by re-reading the fix's
own doc comments. WP-5's five fixes (#198-202) were re-verified correct and complete otherwise; every
HealthChecks implementation's cancellation classification was individually checked, all correct.

**Worth-fixing:**
- **#249 — WP-5's #200 fix reintroduces #133's own Timer leak for all three built-in
  rate-limiting entry points: the fix's disposal path is unreachable via any public API.**
  `src/Benzene.RateLimiting/Extensions.cs:111-205` — `UseFixedWindowRateLimiting`/
  `UseTokenBucketRateLimiting`/`UsePayloadSizeRateLimiting` each create a `RateLimiter` and capture it
  with `ownsLimiter: true`, meaning `RateLimitingMiddleware<TContext>.DisposeAsync()` is now the
  *only* place disposal happens. But `MiddlewarePipeline<TContext>.CreateChain` constructs a fresh
  middleware instance from the factory on **every single message** and never retains or disposes it —
  and none of the three public `UseXRateLimiting` methods return any handle to the created limiter or
  middleware. **There is no way for any caller using the documented public API to ever dispose the
  limiter it created** — the exact leak #133 demonstrated (100/100 undisposed limiters surviving
  forced GC) is fully back for the three entry points every doc example uses. Worse than pre-#200 in
  one respect: before #200, the (colliding) DI registration at least disposed on shutdown; after #200,
  the collision is fixed but disposal is now structurally unreachable. The package's own test proving
  the disposal mechanism works only does so by reaching into `pipeline.GetItems()[0]`, an internal
  builder API no production caller would use.
- **#250 — `PollyResilienceMiddleware` never threads any `CancellationToken` into Polly, and
  discards the per-attempt token Polly itself supplies — a `Timeout`/`Hedging` strategy cannot
  actually cancel the wrapped work.** `src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs:46`.
  `_pipeline.ExecuteAsync` is called with no cancellation token at all (always `CancellationToken.
  None`; unlike every sibling middleware needing ambient cancellation), and the lambda's own
  per-attempt token — the one Polly's `Timeout`/`Hedging` strategies create specifically so the
  wrapped call can be cancelled when their timer fires — is discarded. A `Timeout`-wrapped slow
  downstream call: Polly throws `TimeoutRejectedException` after its configured deadline, but the
  real call keeps running un-cancelled to completion in the background, potentially producing
  concurrent uncoordinated attempts if retry is also configured. First-rigor-pass territory for this
  package; the package's own `CLAUDE.md` incorrectly claims the token is passed through.

**Minor:**
- **#251** — `src/Benzene.Resilience/CLAUDE.md` still describes the pre-#61 `TimeoutMiddleware`
  exception filter (`ex.CancellationToken == cts.Token && ...`) as the one to protect; the actual
  guard was deliberately simplified by round 7-10's #61 fix and the class's own XML remarks explain
  why — the package doc's separate summary was never updated and could mislead a future editor into
  "restoring" the wrong condition.
- **#252** — `RedisWildcardActions.InvalidateEntryAsync` reports `false` ("invalidate failed")
  whenever a pattern legitimately matches zero keys — a routine, benign no-op invalidate produces a
  spurious "cache may serve stale data" warning on every occurrence. Pre-existing, not introduced by
  WP-5, low severity (log noise).

**Missing test coverage:** `PollyResilienceMiddlewareTest.cs` has zero mentions of `Timeout`/
`CancellationToken` — no test proves or disproves that a Polly timeout/hedging strategy actually
cancels the wrapped work. The rate-limiting disposal gap (#249) has no test reflecting how a real
caller would use the public API — only the internal-API-reaching test exists.

## §8 EventSourcing/Outbox/Idempotency/ClaimCheck/Saga/ResponseEvents/MapReduce — #253–#259

**Baseline:** Event Sourcing — round 11 (12 findings, all fixed, hold up). Saga/ClaimCheck — round 1
+ round 14 (2 open findings, #208-209, correctly not re-reported; ClaimCheck itself re-verified
intact). Outbox/ResponseEvents/MapReduce — effectively first rigor pass. **This pass: 5 worth-fixing,
2 minor — the largest single-area haul this round, concentrated in Outbox.**

**Worth-fixing:**
- **#253 — `OutboxDispatcher.DispatchEnvelopeAsync` treats a post-send settle-call *throw* as a
  failed dispatch, guaranteeing a resend of an already-sent message.**
  `src/Benzene.Outbox/OutboxDispatcher.cs:100-128`. A single try/catch wraps both `sender.SendAsync`
  and `_store.MarkDispatchedAsync`. The package's docs describe only a crash-between-send-and-settle
  as the inherent at-least-once window — but an ordinary transient store error (DynamoDB throttling,
  a network blip) *throwing* from `MarkDispatchedAsync` immediately after a genuinely successful send
  is caught by the same handler and rescheduled/re-sent, converting a routine transient hiccup into a
  guaranteed duplicate delivery, not the rare crash race the docs describe. Untested: no test covers
  the settle call throwing (only returning `false`).
- **#254 — `DynamoDbOutboxTransaction.CommitAsync` destructively drains the stage before the
  write; a thrown (not just oversized) `TransactWriteItemsAsync` call permanently loses the staged
  envelope(s).** `src/Benzene.Outbox.DynamoDb/DynamoDbOutboxTransaction.cs:52-86`. The two validation
  throws (nothing-to-commit, over-100-items) are deliberately ordered before `DrainStaged()` — proven
  by an existing test — but the actual DynamoDB call is not covered by that protection: the drain has
  already happened, so if `TransactWriteItemsAsync` throws (throttling, a conditional-check failure),
  the buffer is now empty with no diagnostic signal (the undrained-envelope warning never fires — count
  is 0). A caller retrying `CommitAsync` gets "nothing to commit" or a commit silently omitting the
  previously-staged send. Undermines the package's "atomic, all-or-nothing" claim under exactly the
  failure mode DynamoDB transactions are meant to be resilient against. Fix shape: peek (non-
  destructive) to build the transact-item list, drain only after success.
- **#255 — `EntityFrameworkOutboxStore<TDbContext>`'s settle methods can throw
  `DbUpdateConcurrencyException` instead of returning the documented `false`, in the exact race the
  class claims is handled.** `src/Benzene.Outbox.EntityFramework/EntityFrameworkOutboxStore.cs:
  187-245`. The `WHERE leaseToken == ...` filter correctly handles a reclaim *before* the read, but a
  concurrent reclaim *between* the SELECT and `SaveChangesAsync` trips EF's own optimistic-concurrency
  check (`RowVersion`), which none of the three settle methods catch — violating `IOutboxStore`'s
  strictly-`true`/`false` documented contract. Blast radius limited by #253's downstream fencing, but
  it's a real contract violation causing a noisy uncaught exception and an extra retry cycle.
- **#256 — `IdempotencyMiddleware<TContext>`'s exception-path `ReleaseAsync` can mask the original
  handler exception if the store itself throws.** `src/Benzene.Idempotency/IdempotencyMiddleware.cs:
  80-89,111-120`. `catch { await ReleaseAsync(...); throw; }` — if `_store.ReleaseAsync` itself throws
  (a real store failure, not a fenced `false`), that new exception propagates and the `throw;` is never
  reached: **the original handler exception — the actual reason the message failed — is discarded**.
  This is precisely the bug class earlier rounds fixed elsewhere (an abandon/settle failure must never
  replace the original exception in the rethrow) but the protection was never applied here.
- **#257 — `Saga.RunOnceAsync`: a state-store failure on the *success* path discards the entire
  successful `SagaResult`, risking a duplicate re-run — the mirror of open finding #208 (which covers
  the failure path), not fixed by anything that would close #208.** `src/Benzene.Saga/Saga.cs:128-134`.
  If `store.RecordFinishedAsync(...)` throws after every stage genuinely succeeded, the caller never
  receives the `SagaResult` — they see only a thrown exception with no way to learn the saga completed.
  A caller that reasonably retries on any thrown exception re-invokes the saga's forward steps a second
  time, with no compensation (they succeeded) and no dedup (the saga has no idea it already ran) — a
  genuine at-least-once/duplicate-side-effect risk specific to the success path. A lower-severity
  variant exists on the failure path too (losing `CompensationFailures` visibility if the store throws
  after rollback already ran).

**Minor:**
- **#258** — `InMemoryEventStore.AppendAsync` has no negative-`expectedVersion` guard, unlike
  `DynamoDbEventStore` (round 11's #121 fix) — falls through to a different exception type
  (`EventStoreConcurrencyException` vs. `ArgumentOutOfRangeException`), the same test-vs-prod
  divergence class round 11's #131 explicitly fixed for `MaxEventsPerAppend` but never mirrored here.
- **#259** — `Benzene.MapReduce` has thin test coverage (6 cases total) relative to its public
  surface: empty-shards, a throwing `reduce` delegate, and `MaxDegreeOfParallelism` actually bounding
  concurrency are all untested.

**Missing test coverage:** every worth-fixing finding above has a named, confirmed-absent regression
test (store-throws-during-settle for #253; `TransactWriteItemsAsync`-throws for #254; reclaim-
between-SELECT-and-SaveChanges for #255; store-throws-during-release for #256; state-store-
throws-on-success for #257). `Benzene.ClaimCheck`, `.Idempotency`'s claim-fencing, `.EventSourcing`
core, `Benzene.Saga`'s concurrency-safety, and `Benzene.ResponseEvents` all otherwise came back solid
with no coverage gaps, no `[Skip]`/commented-out tests found anywhere in scope.

## §9 Validation/Serialization/Observability/gRPC/Auth adapters — #260–#262

**Baseline:** Auth (Basic/Core/OAuth2) — round 11 §7 (11 findings, 6 landed, "no bypass found" —
this pass re-ran the adversarial matrix fresh rather than trusting the conclusion; it still holds, no
new auth findings). Serialization/diagnostics — round 7-10 WP-L/WP-M (5 findings, #56-60, closed for
Avro/MessagePack/Xml-BOM/Newtonsoft/JsonSchema-doc) — all re-verified intact. gRPC — no dedicated
round has ever existed; this is effectively its first first-rigor pass. **This pass: 2 worth-fixing,
1 minor, 2 just-noting.**

**Worth-fixing:**
- **#260 — `Benzene.Xml.XmlSerializer.Deserialize` has no nesting-depth guard — a
  self-referencing/recursive request DTO is an uncatchable StackOverflowException DoS, the identical
  bug class as Avro's #56, left unfixed here.** `src/Benzene.Xml/XmlSerializer.cs:91-93`. Hardened
  against entity expansion (`DtdProcessing.Prohibit`) but carries no analogue of `AvroOptions.
  MaxDepth`/`BoundedBinaryDecoder`'s recursion guard. `Benzene.Avro`'s own schema generator explicitly
  supports and documents recursive record types as legitimate — exactly the feature #56's depth guard
  protects. A deeply-nested `application/xml` body for a naturally recursive DTO shape (comment tree,
  category tree, org chart — all common) drives `System.Xml.Serialization.XmlSerializer` into
  unbounded recursion and process-killing stack overflow, well under any reasonable body-size limit.
  `XmlSerializerTest.cs` has no test approaching this — every test uses a flat two-property payload.
- **#261 — `ReflectionGrpcMethodFinder`'s duplicate-gRPC-method check is case-sensitive;
  `GrpcRouteFinder`'s lookup is deliberately case-insensitive — a case-variant duplicate registration
  bypasses the clear error and crashes with an opaque `ArgumentException` instead.** Identical bug
  shape to round 14's #211 (a validation check not case-folding to match the thing it's meant to
  mirror, "reached through different inputs"). `ReflectionGrpcMethodFinder.cs:27-29` groups via
  default (ordinal) string equality; `GrpcRouteFinder.cs:12-14` builds its lookup with
  `StringComparer.OrdinalIgnoreCase` from the same source. Two handlers differing only in method-name
  casing: the finder doesn't throw its intended `BenzeneException`, and `GrpcRouteFinder`'s
  `.ToDictionary(..., StringComparer.OrdinalIgnoreCase)` throws a generic, far-less-actionable
  `ArgumentException` from inside LINQ internals instead. Confirmed by the existing test's own gap:
  the only duplicate-detection test uses identical casing both times, never exercising the
  case-variant path (a separate test proves the route-finder's *lookup* is case-insensitive, but
  nothing proves the *duplicate detector* agrees).

**Minor:**
- **#262** — `MessagePackSerializer`'s custom-options constructor doc-comment suggests a pattern
  (`MessagePackSerializerOptions.Standard.WithResolver(...)`) that, if followed literally, silently
  reintroduces the `TrustedData` DoS exposure #59 exists to prevent, with zero warning about the
  trade-off. Not reachable through the package's own DI helpers (always the safe default), only a
  direct `new MessagePackSerializer(customOptions)` caller.

**Missing test coverage:** both worth-fixing findings carry their own confirmed-absent test evidence
(no XML depth/recursion test at all; the gRPC duplicate-detection test never exercises case-variance).
`JsonSchema`/Newtonsoft's reliance on `System.Text.Json`/`JsonTextReader`'s built-in MaxDepth (64) was
checked and confirmed already-safe by default — initial suspicion of a gap there was ruled out, not
just assumed.

## §10 CodeGen/CLI/Schema/Descriptor/CloudService/Probe — #263–#265

**Baseline:** Spec/descriptor/CloudService/Probe last reviewed round 11 (6 findings, #166-171) —
re-verified #166-170 fixed, #171 still correctly a deliberate `[DECISION]`, no drift. Autofac/
CodeGen.ApiGateway/Markdown last reviewed round 9 + round 14 (4 findings, #210-213, still open/
unfixed) — re-confirmed all four still open, not re-reported. **This pass: 1 worth-fixing, 2 minor.**

**Worth-fixing:**
- **#263 — the #212 unescaped-interpolation-into-generated-output defect class is systemic across
  `Benzene.CodeGen.Terraform` and reaches `Benzene.CodeGen.Client`, not confined to
  `ApiGatewayBuilderV1`.** Every string field on Terraform settings types (`Name`, `Domain`,
  event-bus/rule names, and message **topic strings**) is interpolated straight into generated `.tf`
  files with no escaping in every builder in the package (`TerraformLambdaBuilder.cs:43,100-102,121,
  127-129`; `TerraformEventBridgeRuleBuilder.cs:31,41,45,100-103`;
  `TerraformLambdaEventBusPermissionsBuilder.cs:81`). A topic containing `${` isn't just mangled — it's
  a live Terraform interpolation expression, so `terraform plan`/`apply` acts on invalid/semantically
  wrong infrastructure. The same gap reaches C# codegen in
  `Benzene.CodeGen.Client/MessageClientSdkBuilder.cs:128,217` (topic interpolated as a raw C# string
  literal — a `"` in a topic produces uncompilable generated client code; an embedded `", ...`
  sequence is a genuine C#-source-injection vector) and
  `OpenApiSchemaCSharpTypeBuilder.cs:69,73`. **The codebase already has the correct fix pattern and
  simply never propagated it**: `Benzene.CodeGen.SourceGenerators/MessageHandlerSourceGenerator.cs:
  360-366` handles the identical input (a user topic string) via `SymbolDisplay.FormatLiteral`, with a
  comment explaining exactly this hazard. That fix was made in one codegen path and never swept to
  Terraform/Client/ApiGateway.

**Minor:**
- **#264** — `HealthCheckCommand.IsHealthy` (`Benzene.CodeGen.Cli.Core/Commands/HealthCheck/
  HealthCheckCommand.cs:82-90`) treats an unrecognized response shape (no `isHealthy` field at all) as
  healthy — a documented, deliberate choice, but a residual CI-gate softness: a misconfigured
  `--lambda-name` returning unrelated 200 JSON passes `benzene healthcheck` as healthy.
- **#265** — `LambdaServiceMarkdownBuilder.BuildValidation` (`Benzene.CodeGen.Markdown/
  LambdaServiceMarkdownBuilder.cs:89`) embeds property names/rules into a Markdown table row with no
  `|` escaping — a property name containing a pipe corrupts the rendered table. Same class as #213,
  cosmetic severity.

**Missing test coverage:** no adversarial-content tests (quote/backslash/`${`/`{{`) anywhere in
`test/Benzene.Core.Test/Autogen/CodeGen/Terraform/` or `.../Client/MessageClientSdkBuilderTest.cs` —
confirmed via grep, zero matches. Same gap class round 14 recorded for ApiGatewayBuilderV1/
MarkdownTypeBuilder, now confirmed absent in the sibling packages too.

## §11 Outbound Clients + TestHelpers family — #266–#270

**Baseline:** reviewed piecemeal — round 12 §3 touched 1 package here (1 finding, #192). WP-3 (this
session) swept 9 of ~14 classes for that one specific hazard as a targeted mechanical fix, not a full
review of cancellation/health-checks/test-coverage. No prior round looked at cancellation propagation
or `ParallelOutboundMiddleware` across this family. **This pass: 4 worth-fixing, 1 minor.**

**Worth-fixing:**
- **#266 — the #192 null-logger hazard confirmed present in all three named-but-unfixed siblings**
  the ruling doc flagged as a follow-up: `RabbitMqBenzeneMessageClient.cs`, `KafkaBenzeneMessageClient
  .cs`, `GrpcBenzeneMessageClient.cs` — all three construct identically to the nine already-fixed
  classes and all project-reference `Benzene.Clients`, so the identical fix applies cleanly.
- **#267 — the same hazard also reaches a class WP-3's own scoping grep should have caught but
  didn't, because the grep matched on class *name* rather than hazard *shape*.**
  `src/Benzene.Clients.Aws.StepFunctions/StepFunctionsClient.cs` and its factory carry the identical
  unguarded-logger shape but the class isn't named `*BenzeneMessageClient` despite living inside
  WP-3's declared package scope — showing the sweep's methodology under-covered even its own stated
  scope, not just its stated boundary.
- **#268 — cancellation never reaches the underlying SDK call for 9 of 11 single-send outbound
  clients** (Sns/Sqs/EventBridge/EventGrid/EventHub/QueueStorage/ServiceBus/AwsLambda clients, plus
  the two F1 siblings) — `IBenzeneMessageClient.SendMessageAsync` carries no `CancellationToken`, and
  none of these resolve the established `ICancellationTokenAccessor` idiom that `HttpBenzeneMessage
  Client`/`GrpcBenzeneMessageClient` already use correctly. Wrapping any of the nine in
  `UseTimeout(...)` has zero effect on the in-flight SDK call — the ambient cancel fires but was never
  threaded to the call it was meant to abort. The clean contrast (every health check in this same
  family correctly forwards cancellation) makes the single-send clients' omission read as an
  oversight, not a boundary.
- **#269 — `ParallelOutboundMiddleware` misclassifies a cancelled branch as an ordinary business
  failure.** No `catch (OperationCanceledException) { throw; }` guard ahead of the general
  `catch (Exception ex)` around each fan-out branch, unlike the established pattern elsewhere in this
  same family (`ClientHealthCheck`, `HealthCheckError.Classify`, `GrpcBenzeneMessageClient`'s own
  dedicated OCE catch). A cancelled Http/Grpc branch mid-fan-out gets folded into an ordinary
  `UnexpectedError` aggregate string — the caller can't tell "cancelled" from "actually failed."

**Minor:**
- **#270** — `Benzene.Clients.Http`'s given-instance `UseHttpClient(httpClient)` overload
  constructs `HttpClientMiddleware` without wiring `ICancellationTokenAccessor`, unlike its DI-resolved
  sibling overload — a documented first-class path silently loses cancellation forwarding.

**Missing test coverage:** four of the nine WP-3-patched classes (`EventGrid`/`EventHub`/
`QueueStorage`/`ServiceBus` clients) have **zero direct unit tests of any kind**, not just no
null-logger test. Only 3 of 9 have an explicit null-logger regression test (matching the fix's own
"representative 2-3" framing). Zero null-logger coverage exists for the three F1/F2 siblings —
notably, the gRPC test helper actually *masks* the gap by defaulting to a non-null logger whenever the
caller omits one, so the real constructor's null-handling is never exercised. `ParallelOutboundTest.cs`
has zero cancellation scenarios, so #269 has no test surface to catch it or verify a fix against.
Clean, worth recording: health-check reachability across the whole family is solid; `EventGrid`/
`ServiceBus`'s missing auto-wired health checks are deliberate, documented decisions, not gaps;
`Benzene.Clients.InProcess` (a heavy test dependency) has no correctness gap that would corrupt other
tests; the six `*BatchMessageClient` classes have no logger field at all by construction, no hazard.

## §12 Examples + Templates + Deploy — #271–#275

**Baseline:** round 12 §4/WP-4 (Cqrs/K8sTransports/Cloudflare, 4 findings, fixed — re-verified
correct, no orphans). Round 14 §4 (9 apps reviewed, #214-223 still open on GoogleCloudMesh/Outbox/
Kafka — #214/#215 re-confirmed still present/unfixed, not re-reported). **This pass is a genuine
first look at AwsMesh, AzureMesh, Azure (top-level), CodeGen, Mesh, Saga, K8sMesh (light),
templates/, deploy/Discovery, deploy/Mesh/helm, plus a systematic solution-membership sweep of the
entire examples/ tree. This pass: 3 worth-fixing, 2 minor.**

**Worth-fixing:**
- **#271 — `examples/CLAUDE.md` flatly contradicts `build-benzene.yml`'s actual behavior, and the
  false claim is precisely what let two example families go completely unchecked.**
  `examples/CLAUDE.md:116-119` states examples are "NOT part of the primary CI gate" and "not
  compile-checked by the main build" — false: `build-benzene.yml` has an `examples-build` job that
  builds `Benzene.Examples.sln` on every push/PR, plus a test step running 9 example test projects.
  This isn't just stale prose — it's *why* the next finding went unnoticed: a reader concluding
  nothing about examples is CI-checked sees no signal that solution-exclusion means zero verification
  rather than the same manual-only tier every example gets.
- **#272 — `AwsMesh` (7 projects) and `AzureMesh` (1 project) are members of *no* solution file
  anywhere, so a compile break in any of the 8 projects is invisible to every build gate in the repo,
  automatic or manual — more severe than the #193/#215 defect class, since even `GoogleCloudMesh`/
  `AzureFunctionsMesh` at least have their own dedicated `.sln` (unwired into automatic CI, but present
  as a fallback).** Verified systematically, not spot-checked: diffed every `.csproj` under
  `examples/` against every path in `Benzene.Examples.sln`. `AwsMesh`'s and `AzureMesh`'s deploy
  workflows are `workflow_dispatch`-only (never run on push/PR) and cost real cloud spend, so in
  practice run rarely. K8sMesh, despite also lacking a sln, is NOT silently unchecked — a
  docker-compose smoke-test workflow builds and behavior-tests it on every relevant push/PR. No
  independent build-break bug found in AwsMesh/AzureMesh's ~2,700 lines (read in full) — the risk is a
  *future* break going unnoticed, not a present one.
- **#273** — `examples/CodeGen/Benzene.Examples.CodeGen.Client/some.dll` is a 12KB unexplained,
  unreferenced binary (a Roslyn in-memory-emit artifact accidentally committed, per `strings`) sitting
  in an example's source directory with a name giving zero information about whether it's safe to
  delete. Harmless to the build (unreferenced), but dead weight worth removing.

**Minor:**
- **#274** — `examples/CLAUDE.md`'s "own solution" list omits `Benzene.Examples.GoogleCloudMesh.sln`
  and `Benzene.Example.AzureFunctionsMesh.sln`, both of which exist on disk.
- **#275** — `deploy/Mesh/helm/benzene-mesh/` has zero CI validation of any kind (no `helm lint`/
  `helm template`/`--dry-run` anywhere) — the one artifact in `deploy/` within scope shipping no
  automated verification; internally consistent on static read, so a coverage gap, not a known break.

**Missing test coverage:** the Helm chart (#275, above) and every mesh-topology example across
every cloud (AwsMesh/AzureMesh/GoogleCloudMesh/AzureFunctionsMesh/Mesh — K8sMesh is the one exception,
covered by its compose smoke test) has no xunit-style test project at all, arguably by design for a
manual demo bed but worth naming as the gap it is. `templates/` by contrast is fully covered — all 12
template shortnames verified matching `build-templates.yml`'s generation+build+test matrix.

---

## §13 Next steps

Per the established review→fix cadence, this document is the review record for #225–#276. No fix
packages have been designed and no code was changed by any review agent (each was explicitly
instructed not to modify, fix, or create any file, and the compiling session made no code changes
either — only this document). The user has indicated a separate agent will pick up fixes from the
task board, matching round 14's own closing note.

**Suggested priority order for a fix round**, given the findings above:
1. **#242** (mesh:report artifact-overwrite) — the one genuine security finding, live on the
   documented default deployment, small and self-contained fix.
2. **#249** (WP-5's rate-limiter disposal regression) — this session's own fix broke something;
   closing it is the direct analogue of what round 13's WP-5 itself was created to do.
3. **§8 Outbox** (#252–#257) — five worth-fixing findings in one package, all threatening the
   package's core atomicity/durability claims; a natural single work package.
4. **The four independent cancellation-not-threaded instances** (#225, #236, #237, #268) — same root
   cause, same fix shape (resolve `ICancellationTokenAccessor`, thread the token into the SDK call),
   cheap to fix as one swept work package rather than four.
5. **Everything else**, grouped by the natural package boundaries the twelve review areas already
   drew — each section above is a reasonable work-package unit on its own.

**A rigor caveat that should gate the fix round's own verification, not just this document's
findings**: because no agent in this round had a compiler, every finding here needs its evidence
re-confirmed against a real build/test run before a fix is written against it — a small number may
turn out to be already-handled-differently-than-the-reading-suggested (the pattern round 13 itself
demonstrated once, when its own agent caught and discarded a false positive before finalizing). Budget
a short re-verification pass at the start of the fix round rather than treating every line above as
settled.

**Explicitly not re-reported, still open from prior rounds** (do not fold these into a #225–#276 fix
package without separately re-confirming them, since they weren't re-verified by every relevant area
this round — only the areas that named them did): #171 (deliberate `[DECISION]` scope-down),
#204 (Mesh.Ui vendoring doc — see #245 above for its now-confirmed sibling), #208/#209 (Saga
rollback/multi-failure — see #256/#257 above for adjacent-but-distinct new findings in the same
package), #210–#213 (Autofac/CodeGen.ApiGateway/Markdown — re-confirmed still open by §10 this round,
not re-derived), #214/#215 (GoogleCloudMesh build error / Outbox solution-membership — both
re-confirmed still open by §12 this round).

---

## §14 Task-number index

| Task | Section | One-line |
|---|---|---|
| #225 | §1 Core | `MiddlewareRouter<TRequest,TContext>` has no way to forward the ambient cancellation |
| #226 | §1 Core | `Filters.DependencyExtensions.AddFilters` crashes with `AmbiguousMatchException` for |
| #227 | §1 Core | `MessageHandlersList` (a DI singleton) uses a plain, non-thread-safe `List<T>` for |
| #228 | §1 Core | Flagged for a compiler check (not asserted): `MicrosoftBenzeneServiceContainer |
| #229 | §2 AWS | `Benzene.Aws.Lambda.Kafka`'s `AddKafka()` is a missed instance of round 11's #160 |
| #230 | §2 AWS | `XRayMiddlewareDecorator.Tag()` runs unguarded inside the same try as the pipeline call |
| #231 | §2 AWS | `Benzene.Aws.Sqs.Client.SqsMessageClient.PublishAsync` (`SqsMessageClient.cs:64`) |
| #232 | §3 Azure | `ServiceBusBatchApplication`'s fallback-abandon can mask the original failure — no |
| #233 | §3 Azure | the trigger source generator: a Service Bus topic trigger missing `SubscriptionName` |
| #234 | §3 Azure | seven Azure Functions packages register their message-handler seams with plain |
| #235 | §3 Azure | `EventGridTriggerEvent.Parse` throws uncaught on malformed JSON, bypassing |
| #236 | §4 GCP/RabbitMQ/Kafka | RabbitMQ outbound `mandatory: true` publishes ignore ambient cancellation entirely; the |
| #237 | §4 GCP/RabbitMQ/Kafka | Kafka outbound produce ignores ambient cancellation too, and inconsistently with its own |
| #238 | §4 GCP/RabbitMQ/Kafka | `BenzeneKafkaWorker.StopAsync` (`BenzeneKafkaWorker.cs:486-494`) ignores its own |
| #239 | §4 GCP/RabbitMQ/Kafka | `RabbitMqWorker` shutdown race: if `BasicCancelAsync` throws during `StopAsync` |
| #240 | §4 GCP/RabbitMQ/Kafka | Kafka's dead-letter retry loop has no `OperationCanceledException` carve-out, so a |
| #241 | §4 GCP/RabbitMQ/Kafka | `RabbitMqWorker`/`BenzeneKafkaWorker<K,V>` both implement `IDisposable`, but nothing |
| #242 | §5 Mesh collector | `mesh:report`'s untrusted `MeshServiceReport.Name` can overwrite sibling top-level |
| #243 | §5 Mesh collector | `MeshCollectorStore`'s ring buffer (`MeshCollectorStore.cs:146-203`) throws |
| #244 | §6 Mesh dispatch | `MeshOidcOptions.Validate()` doesn't actually implement the algorithm-confusion |
| #245 | §6 Mesh dispatch | `src/Benzene.Spec.Ui/CLAUDE.md` has the identical bug as round 14's open #204, never |
| #246 | §6 Mesh dispatch | `HttpMeshServiceDispatcher.ReadCappedAsync` truncates at a raw byte boundary before |
| #247 / #248 | §6 Mesh dispatch | `MeshSourceRegistrar`/`MeshDispatchConfig` don't expose round 12-13's new options through `mesh.json` |
| #249 | §7 HealthChecks/Cache/RateLimiting | WP-5's #200 fix reintroduces #133's own Timer leak for all three built-in |
| #250 | §7 HealthChecks/Cache/RateLimiting | `PollyResilienceMiddleware` never threads any `CancellationToken` into Polly, and |
| #251 | §7 HealthChecks/Cache/RateLimiting | `src/Benzene.Resilience/CLAUDE.md` still describes the pre-#61 `TimeoutMiddleware` |
| #252 | §7 HealthChecks/Cache/RateLimiting | `RedisWildcardActions.InvalidateEntryAsync` reports `false` ("invalidate failed") |
| #253 | §8 Outbox/EventSourcing/Saga | `OutboxDispatcher.DispatchEnvelopeAsync` treats a post-send settle-call *throw* as a |
| #254 | §8 Outbox/EventSourcing/Saga | `DynamoDbOutboxTransaction.CommitAsync` destructively drains the stage before the |
| #255 | §8 Outbox/EventSourcing/Saga | `EntityFrameworkOutboxStore<TDbContext>`'s settle methods can throw |
| #256 | §8 Outbox/EventSourcing/Saga | `IdempotencyMiddleware<TContext>`'s exception-path `ReleaseAsync` can mask the original |
| #257 | §8 Outbox/EventSourcing/Saga | `Saga.RunOnceAsync`: a state-store failure on the *success* path discards the entire |
| #258 | §8 Outbox/EventSourcing/Saga | `InMemoryEventStore.AppendAsync` has no negative-`expectedVersion` guard, unlike |
| #259 | §8 Outbox/EventSourcing/Saga | `Benzene.MapReduce` has thin test coverage (6 cases total) relative to its public |
| #260 | §9 Validation/gRPC/Auth | `Benzene.Xml.XmlSerializer.Deserialize` has no nesting-depth guard — a |
| #261 | §9 Validation/gRPC/Auth | `ReflectionGrpcMethodFinder`'s duplicate-gRPC-method check is case-sensitive; |
| #262 | §9 Validation/gRPC/Auth | `MessagePackSerializer`'s custom-options constructor doc-comment suggests a pattern |
| #263 | §10 CodeGen | the #212 unescaped-interpolation-into-generated-output defect class is systemic across |
| #264 | §10 CodeGen | `HealthCheckCommand.IsHealthy` (`Benzene.CodeGen.Cli.Core/Commands/HealthCheck/ |
| #265 | §10 CodeGen | `LambdaServiceMarkdownBuilder.BuildValidation` (`Benzene.CodeGen.Markdown/ |
| #266 | §11 Clients | the #192 null-logger hazard confirmed present in all three named-but-unfixed siblings |
| #267 | §11 Clients | the same hazard also reaches a class WP-3's own scoping grep should have caught but |
| #268 | §11 Clients | cancellation never reaches the underlying SDK call for 9 of 11 single-send outbound |
| #269 | §11 Clients | `ParallelOutboundMiddleware` misclassifies a cancelled branch as an ordinary business |
| #270 | §11 Clients | `Benzene.Clients.Http`'s given-instance `UseHttpClient(httpClient)` overload |
| #271 | §12 Examples | `examples/CLAUDE.md` flatly contradicts `build-benzene.yml`'s actual behavior, and the |
| #272 | §12 Examples | `AwsMesh` (7 projects) and `AzureMesh` (1 project) are members of *no* solution file |
| #273 | §12 Examples | `examples/CodeGen/Benzene.Examples.CodeGen.Client/some.dll` is a 12KB unexplained, |
| #274 | §12 Examples | `examples/CLAUDE.md`'s "own solution" list omits `Benzene.Examples.GoogleCloudMesh.sln` |
| #275 | §12 Examples | `deploy/Mesh/helm/benzene-mesh/` has zero CI validation of any kind (no `helm lint`/ |
| #276 | — | Round-summary; closes at round completion |
