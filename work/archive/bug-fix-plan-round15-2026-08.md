> ARCHIVED 2026-08-30: actioned. All six work packages (WP-A through WP-F, task board #226–#244)
> landed and were pushed to `main` via 6 merge commits, in landing order: `6f199f1` (WP-A, #226),
> `09333f0` (WP-B, #227–229), `23ec329` (WP-C, #230–232), `3692ecf` (WP-D, #233–236), `f648ffb`
> (WP-E, #237–238), `d05aa29` (WP-F, #239–244). Full baseline re-verified after the last merge (and
> after two additional post-merge integration bugs found only once this round and rounds 12–14 were
> both fully merged together — see `work/outstanding-bugs.md`'s "Round 15 + rounds 12–14: two
> integration bugs found only by the post-merge baseline" section): `Benzene.sln` build 0 errors;
> `Benzene.Core.Test` 3296 passed/2 skipped/0 failed; `Benzene.Mesh.Test` 575 passed;
> `Benzene.Mesh.Host.Test` 150 passed; `Benzene.Examples.sln` build 0 errors. Per-finding summaries
> live in `work/outstanding-bugs.md` (search "Round 15"), each pointing back to the design ruling in
> [`bug-fix-designs-round15-2026-08.md`](bug-fix-designs-round15-2026-08.md) for the full record. One
> deliberate scope correction from this plan: #227's fourth named transport, Kinesis, was found
> structurally unable to support preset topics (no topic getter or `MessageRouter` at all) and was
> left unfixed by design — see the WP-B resolved entry.

# Round 15 fix plan (2026-08) — covers task board #226–#244

**Status: READY FOR EXECUTION — not yet started.** This is the fix-design ruling doc for the
round-15 review findings recorded in `work/bug-fix-designs-round15-2026-08.md`. It is written for a
fixing agent (or several in parallel) that was not part of the review: everything needed to execute
is in this document plus the task board entries (#226–#244) and the review doc's executed evidence.

Fix rounds 1–11 established the working protocol; follow it:

- **One isolated git worktree per work package** (`git worktree add --detach /workspace/wtfix15/<wp> $(git rev-parse origin/main)`).
  Never work directly in the main checkout alongside another agent.
- **NEVER use `git stash` in any worktree** — the shared `.git` object store's `refs/stash` has
  corrupted sibling work before. Use scratch files if you need to move code aside.
- **Red-green discipline**: for each behavioral fix, first write a failing test reproducing the
  review's executed evidence (the review doc gives the exact probe recipe), watch it fail, then fix,
  then watch it pass. Tests live in the appropriate existing test project (`test/Benzene.Core.Test`,
  `test/Benzene.Mesh.Test`, etc.), permanently — they are regression tests, not throwaway probes.
- **Each work package updates `work/outstanding-bugs.md`** with a `[RESOLVED] #NNN — ...` entry per
  fixed task (appended in its own new section immediately before the `## Open — maintainer decisions`
  boundary), and **`docs/capability-matrix.md`** where the fix changes what a package does.
- **Commit locally in the worktree only; do not push.** The orchestrator merges packages into `main`
  sequentially. Expect the recurring `work/outstanding-bugs.md` merge conflict (two packages appending
  before the same boundary): resolve by deleting only the `<<<<<<<`/`=======`/`>>>>>>>` marker lines
  and keeping both sides' content.
- **Baseline verification after the final merge** (centrally, never trusting per-package runs):
  `dotnet build Benzene.sln -c Release` (0 errors), `dotnet test test/Benzene.Core.Test`,
  `dotnet test test/Benzene.Mesh.Test`, `dotnet test deploy/Mesh/Benzene.Mesh.Host.Test`, and a build
  of `Benzene.Examples.sln`.
- Mark each task board entry completed as its fix lands; on completion of the whole round, this plan
  moves to `work/archive/` stamped with the landing merge commits (docs-archivist protocol).

The six work packages below are disjoint by file — safe to run fully in parallel. WP-C is the only
one touching a shared primitive (`Benzene.Core.Middleware/BoundedFanOut.cs`); no other package
touches that file.

---

## WP-A — Versioning caster recursion crash (#226)

**Files:** `src/Benzene.Core.Versioning/CasterBuilder/CasterFuncBuilder.cs` (and, if needed,
`CasterFactory`/`CreateClassExpression`/`MapDelegate` in the same folder). Tests: a new
`CasterRecursionTest` in the versioning area of `test/Benzene.Core.Test`.

**Problem:** `CreateCasterFunc<TFrom,TTo>()` memoizes into `_funcs` only after
`Expression.Lambda(...).Compile()` returns, so building the expression for a self-referential or
mutually-recursive DTO shape re-enters `CreateCasterFunc` for the same `(TFrom,TTo)` pair before
anything is memoized — unbounded recursion, uncatchable `StackOverflowException`, process death at
startup via the documented `Upcast<TFrom,TTo>()` API.

**Ruling: support recursion properly** rather than merely converting the crash into a thrown
exception. The classic recursive-compiled-lambda fix:

1. Before recursing into `BuildMappingExpression` for `(fromType, toType)`, install an **indirection
   cell** in `_funcs` (or a parallel in-flight map): a wrapper delegate that invokes a mutable slot
   (`Func<object,object>` cell, e.g. a `StrongBox<Func<object,object>>` or a small holder class).
2. Any recursive lookup for the same pair during expression building resolves to that wrapper — the
   generated expression calls through the cell.
3. After `Compile()` returns, fill the cell with the real compiled delegate and (optionally) replace
   the memoized entry with the direct delegate for the non-recursive fast path.
4. Null-terminate correctly: a `null` child must map to `null` without invoking the caster (guard in
   the generated expression or the wrapper).

**Fallback ruling** (only if the indirection genuinely can't be threaded through the expression
builder in reasonable scope): detect the cycle explicitly (in-flight set keyed on the type pair) and
throw `InvalidOperationException` with a message naming the recursive type pair — a catchable,
loggable startup failure honoring `Upcast`'s "fail eagerly at registration" promise. If you take the
fallback, say so in the `[RESOLVED]` entry and the capability matrix so the limitation is on record.

**Red test:** the review's exact probe — `NodeV1 { Name; Child: NodeV1 }` → `NodeV2 { Name; Child: NodeV2 }`
via `new CasterFactory<NodeV1, NodeV2>().Build()`, plus a mutually-recursive A↔B pair, plus a
3-level-deep actual value round-trip asserting mapped output (and a null-child case). Note a
`StackOverflowException` can't be caught in-proc — while the bug is unfixed the red test must run the
probe in a child `dotnet` process and assert on exit code (the review observed 134), or simply be
written after the fix with the in-proc assertions above (acceptable here given the crash is
process-fatal; document which you did).

---

## WP-B — AWS trigger family gaps (#227, #228, #229)

**Files:** `src/Benzene.Aws.Lambda.S3/DependencyInjectionExtensions.cs`,
`.../DynamoDb/DependencyInjectionExtensions.cs`, `.../Kinesis/DependencyInjectionExtensions.cs`,
`.../Kafka/DependencyInjectionExtensions.cs`,
`src/Benzene.Aws.Lambda.Core/SingleContextEscalatingApplicationBase.cs`, and the `UseTopicFrom` doc
comment in `src/Benzene.Core.MessageHandlers/Extensions.cs:202-206`. Tests in `test/Benzene.Core.Test`
alongside the existing trigger application tests.

**#227 — ruling: make preset topics work on all four transports** (option 1 from the review). The
review confirmed `PresetTopicHolder` is a trivial POCO with no transport-specific behavior and found
no reason preset/derived topics wouldn't work identically. For each of S3/DynamoDb/Kinesis/Kafka:
register `PresetTopicHolder` and wrap the package's topic getter in
`PresetTopicMessageTopicGetter<TContext>` exactly as Sns/Sqs/EventBridge do (copy the shape from
`src/Benzene.Aws.Lambda.Sns/DependencyInjectionExtensions.cs:41-46`). Update `UseTopicFrom`'s doc
comment to add the four transports to its supported list.
**Red test:** the review's probe — an S3 pipeline via
`EntryPointMiddleApplicationBuilder<S3Event, S3RecordContext>` + `.UsePresetTopic("fixed-topic")` +
`.UseMessageHandlers()`; currently throws `BenzeneResolutionException` per message; after the fix the
message routes to a handler registered on `fixed-topic`. Repeat (or parameterize) for the other three.

**#228 — ruling: adopt SQS's carve-out.** In `SingleContextEscalatingApplicationBase.ProcessAsync`
(lines ~82–109), before the `_catchExceptions` swallow, rethrow unconditionally when
`BenzeneFailure.IsInfrastructure(ex)` — mirroring `SqsApplication.cs:87-110`'s stated reasoning
(an infra failure fails the invocation; these transports have no partial-failure channel, so
swallowing means 100%-loss-reported-as-success). Keep the existing log line, add "infrastructure
failure — rethrowing despite CatchExceptions" wording so operators see why.
**Red test:** the review's probe — `SnsApplication(pipeline, new SnsOptions{CatchExceptions=true})`
with a pipeline mock throwing `BenzeneResolutionException` currently completes; after the fix it
rethrows. Add the equivalent for S3 and EventBridge (all three share the base class — one base-class
test plus one per concrete app is fine).

**#229 — ruling: align to `!= true`** ("err toward redelivery, never toward loss", the principle
SQS/DynamoDb state explicitly). Change `SingleContextEscalatingApplicationBase.cs:91` from
`context.MessageResult?.IsSuccessful == false` to `!= true`. Impact is deliberately narrow: normal
wiring's `MessageRouter` always sets a non-null result, so only non-standard pipelines change
behavior — and they change toward failure-visibility, matching `RaiseOnFailureStatus`'s
safe-by-default intent. Document the null-result semantics in the base class's doc comment. Do NOT
change Kafka — its skip-on-null choice is separately documented and justified in its own CLAUDE.md;
leave it and its docs alone.
**Red test:** pipeline mock that never sets `MessageResult` → `SnsApplication.HandleAsync` currently
completes; after the fix it escalates (with default `RaiseOnFailureStatus=true`).

---

## WP-C — Azure cancellation + Timer escalation (#230, #231, #232)

**Files:** `src/Benzene.Core.Middleware/BoundedFanOut.cs`,
`src/Benzene.Azure.Function.Core/AzureFunctionBatchApplicationBase.cs`,
`src/Benzene.Azure.Function.Timer/TimerApplication.cs` (+ new `TimerOptions.cs` + the package's
DI extension + `CLAUDE.md`), and the three stale doc comments
(`src/Benzene.Azure.Function.EventGrid/EventGridApplication.cs:29`,
`src/Benzene.Azure.Function.EventHub/Function/DependencyInjectionExtensions.cs:84`,
`src/Benzene.Azure.Function.EventHub/Function/EventHubOptions.cs:18`).

**#230 — fix:** add `CancellationToken cancellationToken = default` to both `WhenAllAsync` overloads
(`BoundedFanOut.cs:34,57`) and pass it to `semaphore.WaitAsync(cancellationToken)` at line 77;
propagate the batch's token from `AzureFunctionBatchApplicationBase.HandleBatchAsync` at every call
site. **Check all callers repo-wide** (grep `BoundedFanOut.WhenAllAsync`) — every call site must
either pass a real token or knowingly pass `default`; do not leave an unaudited caller. Semantics
ruling: a queued item cancelled while waiting for a slot throws `OperationCanceledException` out of
`WhenAllAsync` (after in-flight items complete their natural course) — batch triggers already treat a
thrown OCE as an invocation failure → redelivery, which is the correct drain-abort behavior. Note
whether `Task.WhenAll` aggregation needs adjusting so already-started items still settle before the
cancellation surfaces (don't abandon in-flight tasks un-awaited).
**Red test:** the review's exact probe — `maxDegreeOfParallelism: 1`, 3 items, item 0 sleeps 300ms,
cancel at 50ms: currently all 3 run (~300ms+, no OCE); after the fix items 1/2 never start and
`WhenAllAsync` surfaces cancellation.

**#231 — ruling: give Timer the escalation, matching siblings' safe-by-default** (option 1). Add a
`TimerOptions` with `RaiseOnFailureStatus` defaulting `true` (and, for symmetry with siblings,
`CatchExceptions` defaulting `false`), accepted by `TimerApplication`'s ctor (optional parameter —
keep the existing ctor signature working) and exposed through the package's DI/`UseTimerTrigger`
extension. After the pipeline completes, if `RaiseOnFailureStatus` and
`context.MessageResult?.IsSuccessful != true` (use the `!= true` convention WP-B standardizes),
throw a `TimerMessageProcessingException` (mirror the sibling `*MessageProcessingException` shape:
one id/schedule property, consistent message template). Update the package `CLAUDE.md`'s "Failure
handling" section to document the new default and the flag.
**Red test:** the review's probe — handler sets `BenzeneResult.UnexpectedError()`, invocation
currently completes silently; after the fix it throws by default, and completes when
`RaiseOnFailureStatus=false` is explicitly set.

**#232 — fix:** reword the three stale comments to the sibling packages' exact phrasing
("safe-by-default: RaiseOnFailureStatus on, CatchExceptions off"), keeping EventHubOptions' ordering-
tradeoff remark otherwise intact. Doc-only; no test.

---

## WP-D — Mesh: exporter flush, collector null-tolerance, schema generator, dead tag (#233–#236)

**Files:** `src/Benzene.Mesh.Wire/IMeshTraceExporter.cs`, `src/Benzene.Mesh.Collector/MeshCollectorStore.cs`,
`src/Benzene.Mesh.Wire/MeshSchemaGenerator.cs`, `src/Benzene.Mesh.Discovery.Aws/AwsLambdaDiscoveryProvider.cs`
(+ its test). Tests in `test/Benzene.Mesh.Test`.

**#233 — fix:** in `HttpMeshTraceExporter.PumpAsync` (lines 92–131), stop deriving the flush moment
from a per-iteration `WaitToReadAsync` timeout. Ruling: compute an **absolute next-flush deadline**
(`Environment.TickCount64 + flushIntervalMs`) once, reset it **only after an actual flush** (batch-full
or deadline flush), and bound each wait by `min(remaining-until-deadline, ...)`; when the deadline
elapses with a non-empty buffer, flush regardless of channel activity. Alternatively mirror
`HttpMeshIssueExporter`'s periodic-timer + drain-loop shape in the same file — either is acceptable;
pick whichever keeps `PumpAsync` simpler, and keep the existing shutdown tail-flush intact.
**Red test:** the review's probe against a recording `HttpMessageHandler` — 1 event/sec with
`batchSize=64, flushInterval=1s` (shrink the interval so the test runs in a few seconds): currently
zero POSTs until dispose; after the fix, at least one POST lands within ~2× flushInterval while events
keep trickling. Also assert the batch-full path still flushes early and shutdown still tail-flushes.

**#234 — fix:** null-coalesce every wire-supplied list at entry in `MeshCollectorStore`:
`descriptor.Topics ?? []`, `descriptor.Produces ?? []` (Register, lines ~117–124), `events ?? []`
(AddEvents, ~150), `batch.Issues ?? []` (AddIssues, ~217) — the same pattern the file already uses for
`Status`/`TopicVersion`. Sweep the rest of the file (and the collector's other ingestion entry points)
for any further unguarded wire-list iteration while there.
**Red test:** the review's probes — deserialize `{"issues":null}`, `{"events":null}`,
`{"topics":null,"produces":null}` via `MeshJson.Options` and call the three store methods: currently
NRE; after the fix each is accepted as empty (registered service has empty topic lists; event/issue
batches are no-ops), matching the spec's "no missing feed ever fails ingestion" rule. This is
spec-conformance behavior, not a spec change — no fixture edit is needed (and per repo rules, don't
change a fixture to match an implementation).

**#235 — fix:** extend `MeshSchemaGenerator.TryGetDictionaryValueType` (lines 82–98) to match
`IDictionary<,>`/`IReadOnlyDictionary<,>` for **any** key type, checked **before** the enumerable
fallback (~182–191), emitting `{"type":"object","additionalProperties":<value schema>}` — matching
System.Text.Json's actual serialize-any-key-as-string behavior. Keep the string-keyed path's existing
output byte-identical (descriptor hashes must not churn for already-correct contracts — verify with an
existing string-keyed fixture/test).
**Red test:** derive against a type with `Dictionary<int,string>` (and one enum-keyed) property:
currently emits the array-of-`{key,value}` shape; after the fix emits the object/additionalProperties
shape. Assert a string-keyed dictionary's derived schema is unchanged.

**#236 — ruling: remove the dead tag, don't wire it.** The consuming path
(`LambdaMeshServiceSource`) invokes fixed `benzene:spec`/`benzene:healthcheck` topics through the
BenzeneMessage envelope, which has no path concept — there is nothing meaningful for `meshPath` to
do, and the original design doc's aligning-TODO was abandoned. Delete the `benzene:mesh-path` tag
constant, the `options["meshPath"]` write (`AwsLambdaDiscoveryProvider.cs:19,118-121`), the test
asserting it (`test/Benzene.Mesh.Test/Discovery/AwsLambdaDiscoveryProviderTest.cs:89-104`), and any
doc mention of the tag in the package docs. Note the removal in the `[RESOLVED]` entry referencing
`work/archive/mesh-self-discovery-design-2026-07.md` §6 item 1 so the paper trail closes.

---

## WP-E — Polly cancellation + Xml serializer contract (#237, #238)

**Files:** `src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs`,
`docs/cookbooks/polly-resilience.md`, the package `CLAUDE.md` if it repeats the false claim;
`src/Benzene.Xml/XmlSerializer.cs`. Tests in `test/Benzene.Core.Test` (resilience + serialization
areas).

**#237 — ruling: wire the token for real** (the fix, not the doc retreat). In
`PollyResilienceMiddleware<TContext>.HandleAsync`, stop discarding the token Polly passes the
`ExecuteAsync` callback: for the duration of each attempt, expose that per-attempt token to the
downstream pipeline via `ICancellationTokenAccessor` — the framework's established idiom, and exactly
the pattern the sibling `Benzene.Resilience.TimeoutMiddleware<TContext>` already implements (copy its
scoping/restore discipline: set the accessor's token before invoking `next()`, restore/link the prior
ambient token after, so nested resilience wraps compose). If an ambient token already exists, link the
two (`CancellationTokenSource.CreateLinkedTokenSource`) so neither an outer timeout nor Polly's
per-attempt cancellation is lost. Then verify the cookbook's "Testing" sample now passes **verbatim**
and correct any remaining prose in the doc; the design-phase open question from
`work/archive/polly-resilience-plan-2026-08.md` is thereby resolved — say so in the `[RESOLVED]` entry.
**Red test:** the cookbook's own sample as a real test — `.AddTimeout(50ms)` wrapping a
delay-until-cancelled `next()` that honors the ambient accessor token, asserting
`TimeoutRejectedException`: currently no exception (returns after the full delay); after the fix it
throws at ~50ms. Add a linked-token test (outer ambient token cancelled → downstream observes it
through the middleware unchanged).
**Caveat honestly:** the middleware can only cancel work that *observes* the ambient token — a `next()`
that ignores cancellation still runs to completion (Polly then abandons the attempt). State this
plainly in the cookbook (it is true of `TimeoutMiddleware` too); do not overclaim.

**#238 — fix:** in `XmlSerializer.Deserialize` (lines 77–94), guard
`string.IsNullOrEmpty(payload)` → return `null`/default before any parsing — mirroring the
`Serialize` side's own null→`""` behavior and completing the round-trip contract its doc comment
already claims ("matching sibling serializers Avro/MessagePack"). This also fixes the line-86 NRE.
Both the typed and untyped overloads, if the class has both.
**Red test:** round-trip `Serialize(type, null)` → `Deserialize` → `null` (currently throws
`InvalidOperationException`); `Deserialize(type, null)` → `null` (currently NREs). Keep the existing
malformed-XML behavior (a non-empty garbage payload still throws) asserted so the guard doesn't
over-swallow.

---

## WP-F — CodeGen/Schema generators (#239, #240, #241, #242, #243, #244)

**Files:** `src/Benzene.Schema.OpenApi/Compatibility/SchemaCompatibilityComparer.cs`,
`src/Benzene.Schema.Compatibility/JsonSchemaComparer.cs`, `src/Benzene.CodeGen.Client/CSharpTypeName.cs`
(+ `MessageClientSdkBuilder.cs` signature path), `src/Benzene.Schema.OpenApi/OpenApi/OpenApiDocumentBuilder.cs`,
`src/Benzene.Schema.OpenApi/JsonOpenApiSchemaBuilder.cs`,
`src/Benzene.Schema.OpenApi/EventService/SchemaDeserializer.cs`,
`src/Benzene.CodeGen.Terraform/TerraformEventBridgeRuleBuilder.cs` (+ siblings in that package).
Tests in the schema/codegen test areas of `test/Benzene.Core.Test`.

**#239 — fix (both twins identically):** replace the dead `RefTargetName(entry.Value) == refId`
fallback in `VariantKey` with a comparison that gives an **inline** discriminator-mapped member a real
identity: match a mapping entry to an inline member by the member's position among the inline members
of the union it belongs to, paired with the mapping key it represents (i.e. resolve each mapping
entry's target — `$ref` name when present, else the mapped inline member — and key the variant on the
discriminator mapping key rather than raw position). Keep `$ref`-named matching exactly as is (that
path was fixed in #152/#53 — don't regress it; the existing union tests must stay green). Apply the
same change to both files — they are stated twins; consider extracting the shared helper only if it
doesn't force a new project reference between them.
**Red test:** the review's probe — two inline discriminator-mapped members, purely reordered: currently
6 spurious changes + `HasBreakingChanges == True`; after the fix, zero changes. Add a true-change case
(one inline member's property genuinely removed) asserting it IS still flagged, attributed to the right
variant.

**#240 — fix:** route every `Reference.Id` read in `CSharpTypeName.GetName`/`GetArrayType` through the
same `INameFormatter`/`CSharpNameFormatter` the class-declaration path uses (inject or reuse however
`OpenApiSchemaCSharpTypeBuilder` obtains it — keep one formatter instance/config so property-type names
and generated class names can never diverge again). Confirm the `MessageClientSdkBuilder.AddMethod`
signature path picks the fix up transitively (it calls through `GetTypeName`); if it has its own raw
`Reference.Id` read, fix it the same way.
**Red test:** the review's probes — catalogue `{"orderItem": {...}, "Order": {$ref orderItem}}`
generates a compiling pair (`OrderItem` class, `public OrderItem Item`); hyphenated id `order-item`
generates valid C#. Strongest assertion: compile the generated output in-test (Roslyn
`CSharpCompilation` over the emitted strings) rather than string-matching, if the test project already
references Roslyn; otherwise assert the property type equals the formatted class name.

**#241 — ruling: keep the 8-verb table, fail descriptively.** The 8 verbs are exactly OpenAPI's
supported operation set (`get/put/post/delete/options/head/patch/trace`) — `CONNECT` has no OpenAPI
representation, so widening is wrong. In `MapOperationType` (lines 201–221), replace the raw dictionary
index with a `TryGetValue` (case-insensitive lookup — accept `"GET"`/`"get"`/`"Get"`) and on miss throw
`InvalidOperationException` naming the invalid method string **and the handler/topic/path being
mapped** (thread that context in from the caller — the builder knows which endpoint it's processing).
**Red test:** `"Gett"` and `"CONNECT"` both throw a message containing the verb and the topic/path;
`"get"`/`"GET"` both map successfully.

**#242 — fix:** in `JsonOpenApiSchemaBuilder`, guard the empty-`JArray` case before `jToken.First()`
(lines 27, 72–80): emit an array schema with an untyped/object items placeholder, the same convention
`ExamplePayloadBuilder` already uses for the equivalent case elsewhere in the package (find and match
its exact shape so the two stay consistent).
**Red test:** the review's probe — `CreateSchema("OrderCreated", "{\"id\":\"abc\",\"tags\":[]}")`
currently throws; after the fix returns a schema whose `tags` is an array with placeholder items.
Assert a non-empty array still infers from its first element as today.

**#243 — fix:** null-coalesce `eventsJArray`/`requestsJArray` to empty in
`SchemaDeserializer.GetEvents`/`GetRequests` (lines 88–100), exactly as the adjacent
`GetTransports`/`GetTags` already do.
**Red test:** a minimal document missing both keys deserializes to a document with empty
events/requests (currently `ArgumentNullException`).

**#244 — fix (minor, experimental package):** add one HCL string-escaping helper
(escape `\` then `"`) in the Terraform package and route every interpolated value through it —
`QuoteList` (lines ~100–103) and the package's other interpolation sites (sweep the whole package;
the review says "every other interpolated field"). No structural redesign — this package is
explicitly experimental/non-packable; the goal is just never emitting syntactically invalid HCL.
**Red test:** a topic containing `"` and one containing `\` produce syntactically valid quoted HCL
strings (assert the escaped output; no Terraform binary needed).

---

## Suggested execution order / sizing

All six are independent; run in parallel if staffed. If serialized, land by severity:
WP-A (process-killing, one file) → WP-E (#237 is silent-loss-of-protection with a false doc) →
WP-D (#233/#234 are production-data-loss/ingestion-crash) → WP-B → WP-C → WP-F. WP-F is the largest
(6 findings) but each fix is a small, independent guard/escaping change; WP-A is the deepest single
fix (expression-tree recursion) — give it the agent with the most room.

Nothing in this round touches an observable **spec** contract (`docs/specification/**` in the main
Benzene repo): #234 brings the collector *into* conformance with the existing spec text, and #235
corrects a descriptor to match the actual wire behavior — no fixture changes, no port re-vendoring.

On round completion: capability-scribe updates `docs/capability-matrix.md` rows for
Versioning, AWS triggers (preset-topic support ×4, infra-failure semantics), Azure Timer
(new `RaiseOnFailureStatus`), Mesh wire/collector, Resilience.Polly (real cancellation support),
Xml serialization, and the Schema/CodeGen generators; docs-archivist stamps and archives this plan
plus `work/bug-fix-designs-round15-2026-08.md`. Give each doc agent its own worktree (a round-11
lesson: two doc agents sharing the main checkout raced on the git index).
