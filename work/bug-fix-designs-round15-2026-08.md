# Round 15 review findings (2026-08)

**Status: ACTIVE — findings only, not yet fixed.** This round was explicitly scoped by the user as
"a round of review across the whole benzene dotnet codebase looking for any issues" — broader than
rounds 12/14's targeted follow-ups. 6 parallel agents, each in an isolated worktree detached at
`5be2fc2` (the head of `main` after the GitHub Actions audit/fix), ~60-90 minute budget each,
partitioned to span the whole tree: core messaging pipeline, AWS Lambda triggers (cross-cutting),
Azure Function triggers + clients (cross-cutting), the mesh ecosystem's still-fresh ground, the
cross-cutting infra packages (serialization/resilience/DI), and the CodeGen/Schema suite. Findings
are tracked as task board **#226–#244** (14 worth-fixing, 5 minor), plus a round-summary task **#245**.

Every finding below was **executed**, not just reasoned about: real throwaway console/xUnit probes
built against the actual assemblies (concurrency stress, cancellation races, malformed/adversarial
input, real serializer round-trips), not speculation. Each agent cross-checked its findings against
`work/outstanding-bugs.md` and `work/archive/*.md` (and this round's own sibling round docs) before
reporting, deleted its probe files, and confirmed a clean `git status` + successful build before
finishing.

---

## §1 Core message-handling pipeline — #226

`Benzene.Abstractions`, `Benzene.Core`/`.MessageHandlers`/`.Middleware`/`.Versioning`, `Benzene.Http`
— the oldest, most foundational packages in the repo, last broadly reviewed in rounds 1/8/9 before
this series' adversarial-probe standard matured. Treated as a first-rigor pass. Found one severe
crash bug; the rest of the pipeline held up.

**Worth-fixing:**
- **#226** — `CasterFuncBuilder.CreateCasterFunc` (`src/Benzene.Core.Versioning/CasterBuilder/CasterFuncBuilder.cs:19-38`)
  memoizes a compiled caster delegate only *after* `Expression.Lambda(...).Compile()` returns. Building
  the expression for a self-referential or mutually-recursive versioned DTO shape (e.g. `Node.Child :
  Node`, or two types A↔B referencing each other) requires recursively calling `CreateCasterFunc` for
  the *same* `(TFrom,TTo)` pair before the outer call has memoized anything — the guard never trips,
  and the recursion is unbounded. Verified: a throwaway console app versioning a simple linked
  parent/child DTO shape through the framework's own documented, recommended `Upcast<TFrom,TTo>()` API
  crashes with an uncatchable, unloggable `StackOverflowException` (process exit code 134/SIGABRT) —
  no exception to catch, nothing in the log, just the process dying. `Upcast` explicitly runs this
  "eagerly, at registration time" specifically so caster-graph problems surface at startup rather than
  on the first message — instead it takes the whole process down. Any application versioning a
  tree/graph/linked-list-shaped payload (parent/child categories, org charts, comment threads — very
  ordinary DTO shapes) hits this on startup.

**Just-noting:** a redundant double-lowercase in `CorsMiddleware<TContext>.HandleAsync` (no behavioral
effect); `HandlerPipelineStructureCache`'s unbounded-by-key growth (confirmed startup-only, not a live
leak); several already-recorded `[DECISION]`/`[PERF]` items in `outstanding-bugs.md` re-confirmed
accurate and not re-reported.

---

## §2 AWS Lambda trigger packages (cross-cutting) — #227–#229

Read every trigger/adapter/options file across Sns, Sqs, S3, DynamoDb, Kinesis, EventBridge, Kafka,
XRay, and the shared `SingleContextEscalatingApplicationBase`, specifically hunting for divergences
between siblings that a per-package review would miss. Found two real, undocumented transport-family
splits with concrete failure/silent-swallow impact.

**Worth-fixing:**
- **#227** — `.UsePresetTopic()`/`.UseTopicFrom()` is documented as scoped to specific transports
  (SQS/SNS/EventBridge and others whose adapter wraps its topic getter in
  `PresetTopicMessageTopicGetter<TContext>`), but calling it on S3, DynamoDB Streams, Kinesis, or Kafka
  isn't rejected at build time or treated as a documented no-op — `PresetTopicMiddleware<TContext>`
  unconditionally resolves a required `PresetTopicHolder` service that these four packages never
  register. Verified: an S3 pipeline built with `.UsePresetTopic("fixed-topic")` throws
  `BenzeneResolutionException` on every single message, forever — classified as an **infrastructure
  failure**, which compounds with #228 below.
- **#228** — SNS/S3/EventBridge's shared `SingleContextEscalatingApplicationBase.ProcessAsync` applies
  the `CatchExceptions=true` opt-in uniformly to *all* exceptions, including infrastructure/DI-wiring
  failures like `BenzeneResolutionException`. `SqsApplication` deliberately carves out an unconditional
  rethrow for infra failures regardless of `BatchFailureMode`, reasoning that an infra failure "fails
  the invocation" rather than being treated per-record (dead-lettering one record at a time would drain
  the queue while reporting healthy) — SNS/S3/EventBridge never got the same carve-out, and unlike SQS
  they have no partial-failure channel at all. Verified: an `SnsApplication` with `CatchExceptions=true`
  fed a `BenzeneResolutionException` completes normally — zero retry, zero DLQ, zero signal beyond a
  log line, on every message, forever.

**Minor:**
- **#229** — SNS/S3/EventBridge treat a null/unset `MessageResult` (pipeline completes without any
  middleware setting an outcome) as accepted, while SQS/DynamoDb explicitly treat the same case as a
  failure ("err toward redelivery, never toward loss"). Kafka makes SNS/S3/EventBridge's same choice
  but documents and justifies it in its own CLAUDE.md; SNS/S3/EventBridge's identical choice has no
  such documentation. Scope-limited: in normal wiring `MessageRouter` always sets an explicit
  (non-null) failure result on a missing topic/handler, so this only bites a non-standard pipeline that
  omits `MessageRouter` or short-circuits before it runs.

**Just-noting:** Kafka's all-exceptions catch (already tracked as a `[DECISION]` in
`outstanding-bugs.md`); the claim-check-hydration gap on S3/DynamoDb/Kinesis/Kafka (plausibly a
legitimate scope difference, already tracked on Kafka's own roadmap); uniform absence of
`CancellationToken` threading across all seven AWS Lambda trigger packages (no divergence to report,
since it's absent everywhere equally).

---

## §3 Azure Function trigger packages + clients (cross-cutting) — #230–#232

Read every Azure batch-trigger/client package (ServiceBus, EventHub, EventGrid, QueueStorage, Kafka,
CosmosDb, BlobStorage, Timer) plus the shared `AzureFunctionBatchApplicationBase`/`BoundedFanOut`
primitives every bounded trigger depends on. Found a cancellation gap in a genuinely shared primitive,
plus a Timer-specific failure-escalation blind spot.

**Worth-fixing:**
- **#230** — `BoundedFanOut.WhenAllAsync`'s concurrency-limiting semaphore
  (`src/Benzene.Core.Middleware/BoundedFanOut.cs:77`) takes no `CancellationToken` at all — an item
  still queued behind the semaphore's `MaxDegreeOfParallelism` gate never observes cancellation and
  simply waits for a free slot. Every Azure batch trigger that sets `MaxDegreeOfParallelism` inherits
  this. Verified: a bounded fan-out over 3 items with `maxDegreeOfParallelism: 1`, cancelled 50ms in
  while items 1/2 are still queued, ran all three to completion (~300ms+) with zero
  `OperationCanceledException` anywhere. Impact: on graceful shutdown (host draining), a large bounded
  batch keeps draining its full backlog instead of failing fast — exactly the scenario
  `MaxDegreeOfParallelism` exists to protect.
- **#231** — `TimerApplication` is a plain `MiddlewareApplication`, not
  `AzureFunctionBatchApplicationBase` — so unlike every sibling batch trigger, it has no
  `RaiseOnFailureStatus` equivalent to escalate a message-handler's returned failure `BenzeneResult`
  into a thrown exception. The package's own CLAUDE.md documents `UseTimerTrigger(...).UseMessageHandlers()`
  as a supported dispatch mode, routing a scheduled job through the same handlers as every other
  transport, but never mentions this gap. Verified: a `TimerApplication` invocation whose handler
  returns `BenzeneResult.UnexpectedError()` (rather than throwing) completes without throwing — the
  Azure Functions host sees a successful invocation, no retry, no failed-invocation telemetry, nothing
  in Application Insights.

**Minor:**
- **#232** — three stale doc comments (`EventGridApplication.cs:29`, EventHub's
  `DependencyInjectionExtensions.cs:84`, `EventHubOptions.cs:18`) still describe the default
  configuration as "both flags off," left over from the `RaiseOnFailureStatus` safe-by-default flip
  that every sibling package's docs were correctly updated for. The EventHub `DependencyInjectionExtensions.cs`
  instance is user-facing API doc that could mislead someone into believing the original at-most-once
  default is preserved.

**Just-noting:** client packages (EventHub/EventGrid/QueueStorage/ServiceBus batch clients),
health checks, header getters, and processing-exception types all read as consistent across siblings —
no divergence found; Blob having no `MessageResult`/routing concept is architecturally justified
(one-blob-one-function, no envelope).

---

## §4 Mesh ecosystem broad sweep — #233–#236

Covered `Benzene.Mesh.Wire`, `.Collector`, `.Reporting`, the `Discovery.{Aws,Azure,Kubernetes}`
backends, the mesh UI's server-side code, and a targeted OIDC auth pass — the genuinely-untouched or
cross-package-integration corners left after rounds 9-14 fixed the individually-reviewed pieces.

**Worth-fixing:**
- **#233** — `HttpMeshTraceExporter.PumpAsync` (`src/Benzene.Mesh.Wire/IMeshTraceExporter.cs:92-131`)
  creates a fresh linked `CancellationTokenSource` on every loop iteration and calls
  `WaitToReadAsync(timeout.Token)`; if the channel has data before the timeout fires, the wait returns
  immediately and the flush deadline never elapses. A steady, moderate production trace rate (below
  `batchSize`) never triggers a time-based flush at all — only process shutdown does. Verified: sending
  one event/sec for 20s against the default `batchSize=64, flushInterval=5s` produced **zero** POSTs to
  the collector during the entire run. An ungraceful shutdown (crash, OOM, `kill -9`) loses the entire
  unflushed buffer — no partial visibility. The sibling `HttpMeshIssueExporter` in the same file
  correctly uses a true periodic timer, confirming this is a bug rather than a shared design choice.
- **#234** — `MeshCollectorStore.Register`/`AddEvents`/`AddIssues` (`src/Benzene.Mesh.Collector/MeshCollectorStore.cs:117-124,150,217`)
  all throw `NullReferenceException` on an explicit-`null` wire list (`Topics`/`Produces`/`events`/
  `Issues`) rather than treating it as empty. The mesh spec's own collector contract says "a batch of
  any size, including empty, is accepted... no missing feed ever fails ingestion" — this is the exact
  defect class already fixed once for a null `Status` field, recurring one level up for whole
  collections. Verified via real deserialization of `{"issues":null}`/`{"events":null}`/
  `{"topics":null,"produces":null}` payloads (matching how Go's `encoding/json` marshals a nil slice) —
  all three crash the store.
- **#235** — `MeshSchemaGenerator.TryGetDictionaryValueType` (`src/Benzene.Mesh.Wire/MeshSchemaGenerator.cs:82-98,182-191`)
  only recognizes `IDictionary<string,TValue>`; any other key type falls through to the enumerable
  fallback, which emits an `"array"` of `{key,value}` objects — but System.Text.Json actually serializes
  *any* dictionary (int/enum/Guid-keyed included) as a JSON **object** with string-converted keys.
  Verified: `Dictionary<int,string>` serializes to `{"1":"a","2":"b"}` but the generator's derived
  schema describes it as an array-of-objects shape the wire never produces — any handler contract using
  a non-string-keyed dictionary gets a mesh descriptor that misdescribes its own wire format, breaking
  schema-validation/client-generation/drift-detection tooling built on it.

**Minor:**
- **#236** — `AwsLambdaDiscoveryProvider`'s `benzene:mesh-path` tag is read into
  `SourceOptions["meshPath"]` (with test coverage asserting exactly that), but the only consumer of
  `AwsLambdaInvoke` mesh sources, `LambdaMeshServiceSource`, never reads a `meshPath` option at all — a
  known incomplete item from the original self-discovery design doc that never got finished. An
  operator tagging a Lambda with `benzene:mesh-path` reasonably believes it does something; it silently
  doesn't.

**Just-noting:** OIDC open-redirect/CSRF-state handling (`ReturnToValidator`/`OidcStateToken`) read as
carefully hardened, no gap; mesh UI HTML injection points all correctly encoded; `MeshDescriptorHashing`
including `Runtime` in the hash matches the conformance fixture's own unpinned treatment of that field;
`MeshDiscoveryRunner`'s per-provider isolation/dedup and `MeshSelfReportMiddleware`'s throttle race both
re-verified intact / already accepted `[DECISION]`s.

---

## §5 Cross-cutting infra: serialization/resilience/DI — #237–#238

Covered `Benzene.Avro`, `.MessagePack`, `.NewtonsoftJson`, `.Xml`, `.JsonSchema`, `.DataAnnotations`,
`.FluentValidation`, `.Resilience`/`.Resilience.Polly`, `.Diagnostics`, `.Microsoft.Dependencies`, plus
a light (non-duplicating) recheck of `.Autofac` against round 14's #210. RateLimiting/Cache (round 13)
and deep Autofac review (round 14) were intentionally skipped. Found one serious cancellation-handling
bug with a false published doc claim, and one serializer contract break.

**Worth-fixing:**
- **#237** — `PollyResilienceMiddleware<TContext>.HandleAsync` (`src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs:46`)
  discards the `CancellationToken` Polly hands its `ExecuteAsync` callback, and the wrapped `next`
  delegate has no token parameter at all — silently defeating every cancellation-driven Polly strategy
  (Timeout, Hedging, RateLimiter). Verified: a pipeline with `.AddTimeout(100ms)` wrapping a 2-second
  delay never times out through the middleware (returns normally after ~2s), while the identical
  pipeline invoked directly against a token-observing callback correctly throws `TimeoutRejectedException`
  at ~100ms. Worse: the package's own published cookbook (`docs/cookbooks/polly-resilience.md`)
  contains a "Testing" code sample asserting exactly this behavior works, and explicitly claims "the
  middleware passes the token Polly threads through `ExecuteAsync`" — both are false against the
  current source; the sample was run verbatim and throws nothing. This was an open question flagged
  during the original design phase (resolve via `ICancellationTokenAccessor`, the pattern the sibling
  `Benzene.Resilience.TimeoutMiddleware<TContext>` already uses correctly) that shipped unresolved and
  undocumented.
- **#238** — `Benzene.Xml.XmlSerializer` breaks its own documented null-round-trip contract: `Serialize(type, null)`
  deliberately returns `""` (matching Avro/MessagePack's null-tolerant pattern per its own doc comment),
  but `Deserialize(type, "")` has no matching guard and throws `InvalidOperationException`, while
  `Deserialize(type, null)` NREs outright (unguarded dereference at line 86). Verified side-by-side
  against all five serializers: MessagePack and Avro genuinely round-trip `Serialize(null)` →
  `Deserialize(...)` → `null`; STJ/Newtonsoft throw cleanly on `Deserialize(null)`
  (`ArgumentNullException`) — Xml is the only one that crashes with an unguarded `NullReferenceException`,
  matching neither sibling pattern.

**Minor:** the already-tracked Autofac `IsGenericType`/`IsGenericTypeDefinition` asymmetry (#210) was
re-confirmed still present and unfixed (no new finding, not re-numbered); stray `Debug.WriteLine` calls
in both DI adapters' catch blocks are harmless duplication of the exception message, not worth a
dedicated fix.

**Just-noting:** `MicrosoftBenzeneServiceContainer.Reopen()`'s collection-copy pattern verified correct
via extension-method resolution (initially looked suspicious, isn't); malformed-base64 handling on
MessagePack/Avro consistent with the framework-wide pattern of leaving it to upstream middleware;
FluentValidation custom validators' null/empty handling is standard, correct idiom; JsonSchema/
DataAnnotations/Diagnostics all re-confirmed solid from prior rounds' fixes.

---

## §6 CodeGen suite + Schema + Descriptor — #239–#244

Covered `Benzene.CodeGen.{Client,Core,Cli,Terraform,LambdaTestTool,SourceGenerators}`,
`Benzene.Schema.{OpenApi,Compatibility}`, `Benzene.Descriptor`, `Benzene.CloudService`. Round 14 found
real generated-output-corruption bugs in `CodeGen.ApiGateway`/`Markdown` (#211-212); this pass
specifically hunted for the same bug classes (unescaped interpolation, dead-code matching logic,
unguarded traversal) in the packages that hadn't been checked for them yet — and found four more,
including two crash-on-ordinary-input bugs.

**Worth-fixing:**
- **#239** — both schema-compatibility comparers' discriminator-mapping fallback (`SchemaCompatibilityComparer.VariantKey`,
  `src/Benzene.Schema.OpenApi/Compatibility/SchemaCompatibilityComparer.cs:385-414`, and its twin in
  `Benzene.Schema.Compatibility/JsonSchemaComparer.cs:322-352`) is dead code: the fallback branch is
  only reached when `refId == null`, but it then compares `RefTargetName(entry.Value) == refId` —
  i.e. `== null` — and `RefTargetName` never returns null, so the branch can never match. Every inline
  (non-`$ref`) discriminated-union member falls through to purely positional matching regardless of any
  discriminator mapping. Verified: a schema with a `oneOf` of two inline discriminator-mapped members,
  purely reordered with byte-identical content, was reported as 6 spurious property changes and
  `HasBreakingChanges == True` — a pure no-op reorder would fail the `EnsureBackwardCompatible` CI gate.
- **#240** — `CSharpTypeName.GetName`/`GetArrayType` (`src/Benzene.CodeGen.Client/CSharpTypeName.cs:17,101`)
  return a `$ref`'s raw `Reference.Id` as a C# type name unsanitized, while the referenced type's own
  class declaration correctly runs its name through `CSharpNameFormatter.Format`. Reachable via the
  documented bring-your-own-schema `SuppliedSchemaCatalog` feature, whose schema ids are arbitrary
  caller strings. Verified: a catalogue with a schema id `orderItem` referenced by another schema
  generates a class `OrderItem` (correctly Pascal-cased) but a property of type `orderItem` (raw,
  never-generated) — a straight compile error (CS0246) in the generated client; a hyphenated id
  (`order-item`) produces a hard C# syntax error. The same unsanitized path also flows into generated
  client method signatures.
- **#241** — `OpenApiDocumentBuilder.MapOperationType` (`src/Benzene.Schema.OpenApi/OpenApi/OpenApiDocumentBuilder.cs:201-221`)
  indexes a fixed 8-verb dictionary with `HttpEndpointAttribute.Method`, which is a completely
  unvalidated free-form string throughout the discovery pipeline. Verified: both a real but
  unsupported verb (`CONNECT`) and a plain typo (`Gett`) throw an opaque `KeyNotFoundException` with no
  mention of which handler/topic/path caused it, crashing the whole spec build for what's often a
  one-character typo.
- **#242** — `JsonOpenApiSchemaBuilder.CreateArraySchema` (`src/Benzene.Schema.OpenApi/JsonOpenApiSchemaBuilder.cs:27,72-80`)
  calls `jToken.First()` unconditionally when inferring a schema from an example JSON payload via the
  documented `AddJsonEvent(topic, typeName, json)` extension. Verified: an example payload with an
  ordinary empty array anywhere (`{"id":"abc","tags":[]}`) throws
  `InvalidOperationException: Sequence contains no elements`, with no indication of which field caused
  it.

**Minor:**
- **#243** — `EventServiceDocumentDeserializer.GetEvents`/`GetRequests` (`src/Benzene.Schema.OpenApi/EventService/SchemaDeserializer.cs:88-100`)
  crash with an unhelpful `ArgumentNullException` on a document JSON missing `"events"`/`"requests"`,
  unlike the adjacent `GetTransports`/`GetTags`, which both null-coalesce to empty. Only reachable via
  an externally-sourced or older-shape baseline passed to `SchemaCompatibility.EnsureBackwardCompatible`
  (Benzene's own emitted documents always include both arrays), but the inconsistency with sibling
  getters looks like an oversight.
- **#244** — `Benzene.CodeGen.Terraform`'s HCL generation (`TerraformEventBridgeRuleBuilder.QuoteList`
  and other interpolated fields) doesn't escape `"`/`\` before embedding values, the same bug class
  round 14 found in ApiGateway/Markdown, now confirmed in a third generator. Verified: a topic
  containing `"` produces invalid HCL/JSON syntax in the generated `.tf` file. Downgraded to minor
  since the package is explicitly marked non-packable/experimental and not part of the 1.0 release.

**Just-noting:** `JsonCanonicalizer`'s RFC 8785 number formatting adversarially probed against 13
boundary cases (zero, negative zero, notation-switch boundaries, `double.MaxValue`, smallest denormal,
round-to-even) — all correct; `Benzene.Descriptor` and `Benzene.CloudService`/`.Probe` (all files) came
back clean; `AsyncApiDocumentBuilder` uses proper serializer-mediated output, not string concatenation
— clean; `DefaultMethodName` shares #240's root cause but has no live call site, not reported
separately.

---

## §7 Next steps

Per the established cadence, this document is the review record for #226–#245. No fix packages have
been designed and no code was changed by any review agent (each confirmed a clean `git status` and a
successful build before finishing). The user has indicated a separate agent will pick up fixes from
the task board going forward — this session's role continues to be finding and recording issues, not
fixing them, unless asked otherwise.

If a fix round is wanted, natural groupings are: §1 (the versioning stack-overflow, isolated and
severe — worth prioritizing on its own), §2+§3 (the AWS/Azure transport-family gaps, similar shape
and independently small), §4 (mesh trace-export/collector-robustness/schema-generator fixes, mostly
self-contained), §5 (Polly cancellation wiring + docs correction, Xml serializer contract fix), and §6
(the four CodeGen/Schema crash-on-input bugs, each a small, independent guard/escaping fix).
