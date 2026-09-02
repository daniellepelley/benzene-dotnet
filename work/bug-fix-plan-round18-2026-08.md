# Bug-fix plan — round 18 (2026-09)

**Status: READY FOR EXECUTION — not yet started.** Covers task board **#292–#317** (26 distinct
findings from the round-18 review pass; the review docs report 27 because two agents independently
found the same `PubSubClientMiddleware` gap — it is filed once, as #311). Source review docs, all at
`work/`:

- `review-round18-codegen-schema-cli-2026-08.md` (#292, #293, #294, #295)
- `review-round18-infrastructure-2026-08.md` (#296, #297, #298, #299)
- `review-round18-validation-serialization-observability-2026-08.md` (#300)
- `review-round18-core-foundations-2026-08.md` (#301)
- `review-round18-mesh-core-2026-08.md` (#302, #303, #304, #305)
- `review-round18-mesh-dispatch-auth-2026-08.md` (#306, #307, #308, #309)
- `review-round18-gcp-rabbitmq-kafka-2026-08.md` (#310, #311; its LOW note on the health-check
  connection is folded into #310's ruling, not filed separately)
- `review-round18-clients-2026-08.md` (#311 again, #312)
- `review-round18-aws-2026-08.md` (#313)
- `review-round18-durability-2026-08.md` (#314)
- `review-round18-fresh-changes-2026-08.md` (#315, #316, #317)
- `review-round18-azure-2026-08.md` — no findings; recorded for completeness (Azure ingress/outbox/
  claim-check/health-check territory came back clean).

**Round-18-specific context the fixer must internalize — read this before anything else:**

1. **Nothing in round 18 was executed.** The review sandbox had no .NET SDK, so every finding is a
   hand trace against source at `7f642b2`, not a red test run. Unlike round 17, there is no
   "reproduced inline" code to copy. The fix round's FIRST step in every WP is therefore to write the
   red test from the recipe below and **run it** in a dotnet-capable environment (local .NET 10 SDK,
   or CI via `.github/workflows/build-benzene.yml`). If a red test comes back green on `main`, stop:
   the trace was wrong, record that in `outstanding-bugs.md` as `[NOT A BUG]` with the test as
   evidence, and do not "fix" it.
2. **Four findings are regressions or omissions in earlier fixes** — #296 (#139's class, missed call
   site), #297 (#249's replacement type lost the `IDisposable` bridge round 16 established for
   `RedisCacheService`), #300 (#280 only reached the two streaming shapes), #311 (#268's eight-transport
   sweep never enumerated Pub/Sub egress). The regression tests for the ORIGINAL fixes (#139, #199,
   #249, #266, #268, #270, #280) must stay green — these WPs repair the fix without undoing it.
3. **One finding is a real security bug, not a hardening nit:** #292 is an arbitrary-file-write via a
   spec fetched with `--url`/`--mesh`/`--lambda-name`. WP-A goes first and merges first.
4. **Three findings are `[DECISION]`-shaped**, not pure fixes: #305 (Lambda freeze vs. never-block
   publish), #315 (edge throttle vs. per-instance app guard at concurrency 10), #317 (allowlist input
   stickiness semantics). Each has a proportionate, non-controversial action the WP ships now, plus an
   `[OPEN]` entry for the maintainer — do not make the design call unilaterally.

## Task board mapping

| WP | Tasks | Area |
|----|-------|------|
| A | #292, #293, #294 | CodeGen security: path traversal containment, VTL JSON escaping, enum member collision |
| B | #296, #297, #298, #299 | Cache/RateLimiting/HealthChecks: LazyLoad write-back, `OwnedRateLimiter` sync bridge, health-check message leak + OCE |
| C | #300 | gRPC: unary/client-streaming stale `ok` trailer on response-conversion failure |
| D | #301 | Benzene.Http: fail fast on two `{param}`s in one route segment |
| E | #302, #303, #304, #295, #305 | Mesh aggregator/compositor: spec-parse signal, slug collision, numeric version order, `additionalProperties`, self-report Lambda caveat |
| F | #306, #307, #308, #309 | Mesh collector/tracing: store bounds, Tempo cancellation, checked arithmetic, fan-out token |
| G | #311, #312 | Outbound clients: Pub/Sub cancellation, Azure batch-creation guards |
| H | #310 | RabbitMQ worker: channel-shutdown detection and recovery |
| I | #313, #314 | AWS stores: CloudWatch page merge, DynamoDB outbox lease boundary |
| J | #315, #316, #317 | AwsMesh Terraform/workflow: concurrency validation, dispatch-guard interaction, allowlist echo |

## Execution protocol (standard — same as rounds 11–17, with one addition)

1. **One isolated git worktree per work package**, all detached from the same base commit on `main`
   (record it at kickoff). `git worktree add --detach <path> <commit>`. **Never `git stash`.**
2. **Red first, and actually run it** (see context point 1). Reproduce with the recipe, confirm it
   fails, then fix, then green. Keep tests as permanent regression tests.
3. **Scoped builds only** — build/test the specific test project, never the whole solution, while
   other WPs run in parallel (the host OOM-kills concurrent full-solution builds; verified every
   round). The coordinator runs ONE centralized full baseline (full `Benzene.sln` build,
   `Benzene.Core.Test`, `Benzene.Grpc.Test`, `Benzene.Mesh.Test`, `Benzene.Mesh.Host.Test`,
   `Benzene.Examples.sln` build) after the last merge.
4. **Subagents cannot receive background-task notifications.** Run every build/test as a single plain
   foreground Bash call; never use run_in_background or Monitor-style polling.
5. **Definition of done per WP**: fix + regression tests green + dated `[RESOLVED]` entries appended
   to `work/outstanding-bugs.md` (immediately before `## Open — maintainer decisions`; the
   coordinator resolves the identical-shaped merge conflicts mechanically) + the relevant
   `docs/capability-matrix.md` row(s) updated + the package's own `CLAUDE.md` updated where the
   ruling says so. Commit with a clear message citing the task numbers.
6. **Coordinator merges sequentially** (WP-A first), hand-reconciling `capability-matrix.md` and
   `outstanding-bugs.md` conflicts, then runs the centralized baseline, then pushes to `main` AND
   `claude/benzy-dotnet-publicity-plan-ujib55` (both branches are kept in lockstep in this repo).
7. **New this round — WP-J has no unit tests.** Its verification is `terraform validate` plus a
   `terraform plan` that is EXPECTED to fail (see the WP). If no Terraform binary is available, the
   fixer records that explicitly and the coordinator verifies via the `mesh-example-aws-deploy.yml`
   workflow's plan step rather than skipping verification silently.

---

## WP-A — CodeGen security: traversal, VTL escaping, enum collision (#292 high, #293, #294)

**Files.** `src/Benzene.CodeGen.Core/CodeFileWriter.cs`,
`src/Benzene.CodeGen.Client/OpenApiSchemaCSharpTypeBuilder.cs` (`BuildSimpleType`, `BuildEnumType`,
`FormatEnumMember`), `src/Benzene.CodeGen.LambdaTestTool/LambdaTestFilesBuilder.cs`,
`src/Benzene.CodeGen.ApiGateway/ApiGatewayBuilderV1.cs` (`BuildVerb`, ~lines 180–210),
`src/Benzene.CodeGen.Core/CSharpNameFormatter.cs` (read only, reuse). Tests under
`test/Benzene.Core.Test/Autogen/CodeGen/{Core,Client,LambdaTestTool,ApiGateway}/`.

**The findings.** (#292) `OpenApiSchemaCSharpTypeBuilder` and `LambdaTestFilesBuilder` use the raw
schema-catalogue key / topic id as the emitted **file name** (`$"{name}.cs"`,
`$"{filePrefix}-{transport}.json"`) while correctly sanitizing the emitted **identifier** via
`_nameFormatter.Format`. `CodeFileWriter.CreateAsync` does `Path.Combine(directoryPath, codeFile.Name)`
with no containment check and creates intermediate directories. A spec fetched via `--url`/`--mesh`/
`--lambda-name` with a schema key like `../../../../home/user/.bashrc` writes outside `--directory`,
and `benzene build` reports success. Nested names are an intentional feature
(`CreateAsync_NestedFileNames_CreatesSubdirectoriesAndWritesEachFile`, `AtomicClientSdkBuilder`) — the
fix must keep them while containing them. (#293) `BuildVerb` interpolates raw `path` (and the derived
`resource`) into the VTL request-mapping template's embedded JSON — the one site in the file the
#212/#263 `YamlLiteral` fix didn't reach. Because it sits inside a YAML literal block scalar the YAML
still parses, so existing tests (which only assert YAML validity) pass; the JSON AWS evaluates per
request is malformed/injectable. (#294) `FormatEnumMember` → `CSharpNameFormatter.Format` →
`Pascalcase` is not injective (`foo`/`Foo` both → `Foo`; integer enums sharing a value both → `Value1`),
and `BuildEnumType` emits with no de-dup → `CS0102` in the generated SDK.

**Rulings:**

1. (#292, belt) `CodeFileWriter.CreateAsync` resolves `Path.GetFullPath(Path.Combine(directoryPath,
   codeFile.Name))` and `Path.GetFullPath(directoryPath)` (with a trailing separator appended to the
   root before comparison, so `/out` does not "contain" `/outside`) and throws a `BenzeneException`
   naming the offending `codeFile.Name` when the resolved path is not under the root. Also reject a
   rooted `codeFile.Name` (`Path.IsPathRooted`) explicitly — `Path.Combine` discards the first argument
   for a rooted second one, which is a second traversal shape the `..` check alone misses. Keep the
   nested-subdirectory behaviour (the existing test stays green).
2. (#292, braces) `OpenApiSchemaCSharpTypeBuilder.BuildSimpleType`/`BuildEnumType` and
   `LambdaTestFilesBuilder` derive the file-name stem through the same
   `RemoveNonIdentifierCharacters()`-style stripping already used for the identifier (route through
   `_nameFormatter.Format(name)` for the `.cs` stem; for the Lambda test-tool JSON keep the `:`→`-`
   convention but strip `/`, `\`, `..` and anything outside `[A-Za-z0-9_-]`). Do not change what the
   class/enum identifier is called — only the file stem.
3. (#293) Escape `path` and `resource` for a JSON double-quoted string before interpolation in
   `BuildVerb` using the same `YamlValueEscaping.EscapeForDoubleQuoted` already applied to
   `_options.AllowedHeaders` two lines below (it escapes `\` and `"`; both are what a JSON string
   needs here). Audit every other interpolation inside the `application/json: |` block in that method
   for the same shape while there.
4. (#294) `BuildEnumType` tracks emitted member names in a `HashSet<string>`; on a collision, append
   a disambiguating suffix (the entry's ordinal position, e.g. `Foo`, `Foo2`) and keep the wire value
   correct for string enums (the `[EnumMember(Value=...)]`/attribute path the builder already emits
   for string enums must still carry the ORIGINAL raw value so serialization round-trips — read the
   existing emission before choosing where the suffix goes). Integer enums sharing a value get the
   same treatment.

**Red-green recipes:**

- `CodeFileWriterTest`: `CreateAsync_NameEscapesOutputDirectory_ThrowsAndWritesNothing` — temp root,
  `new CodeFile("../escaped.cs", lines)`; assert `BenzeneException` and that no file exists at the
  sibling path. Second case with a rooted name (`Path.Combine(Path.GetTempPath(), "rooted.cs")`).
  Both red today.
- `OpenApiSchemaCSharpTypeBuilderTest` and `LambdaTestToolBuilderTest`:
  `BuildCodeFiles_SchemaNameContainsPathTraversal_FileNameIsSanitized` — schema key
  `"../../../../tmp/evil"` (topic id `"../../etc/cron.d/pwn"` for the test tool); assert every
  emitted `ICodeFile.Name` contains neither `..` nor a directory separator. Red today.
- `LambdaOpenApiBuilderTest`: `BuildVerb_PathContainingAQuote_ProducesValidEmbeddedJson` — path
  `user/{id}"x`; extract the `application/json: |` block from the emitted YAML and parse its
  non-VTL JSON skeleton (the review notes the block mixes `$input`/`$context` directives with JSON —
  assert on the emitted `"path": "..."` line being a valid JSON string literal, i.e. contains `\"`,
  rather than trying to parse the whole VTL). Red today.
- `CodegenOutputCompilesTest`: a theory case with a string enum `["foo", "Foo"]` and an integer enum
  with two members sharing a value; assert the emitted file compiles under Roslyn. Red today
  (`CS0102`).

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Autogen.CodeGen"` (the whole CodeGen test tree — the existing
`Nested`/`Atomic`/adversarial-path tests must all stay green).

---

## WP-B — Cache, RateLimiting, HealthChecks (#296 high, #297 high, #298, #299)

**Files.** `src/Benzene.Cache.Core/CacheEntry.cs` (`LazyLoadAsync`, ~line 119),
`src/Benzene.RateLimiting/OwnedRateLimiter.cs`, `src/Benzene.Clients.HealthChecks/ClientHealthCheck.cs`
(~line 64), `src/Benzene.HealthChecks/HealthCheckBuilderExtensions.cs` (both three-arg
`AddHealthCheck` overloads, ~lines 105–147). Tests: `test/Benzene.Core.Test/Cache/CacheEntryTest.cs`,
`test/Benzene.Core.Test/Plugins/RateLimiting/RateLimitingPipelineTest.cs`,
`test/Benzene.Core.Test/Clients/ClientHealthCheckTest.cs`,
`test/Benzene.Core.Test/HealthChecks/HealthCheckBuilderExtensionsTest.cs`.

**The findings.** (#296) `LazyLoadAsync`'s cache-aside write-back calls `SetValueAsync` bare. #139
protected `WriteThroughAsync`'s `Set` branch by routing through `SyncCacheAfterWriteAsync` (which is
`private protected`, so visible here); this third "write after the work already succeeded" call site
was never brought under it. `Serializer.Serialize` runs in `CacheWriteActions.SetValueAsync` BEFORE any
provider try/catch, so a serializer failure (or any non-Redis provider's I/O failure) propagates out of a
call that already has a successful database result. (#297) `OwnedRateLimiter` is `IAsyncDisposable`
only, registered as a factory singleton by the three convenience entry points
(`UseFixedWindow/TokenBucket/PayloadSizeRateLimiting`). Microsoft DI's synchronous
`ServiceProvider.Dispose()` throws `InvalidOperationException` for an `IAsyncDisposable`-only tracked
instance — the exact bug round 16 fixed for `RedisCacheService`. The only disposal test uses
`DisposeAsync()`. `git log` shows the sync bridge was lost when #249 replaced
`InternallyOwnedRateLimiterHolder<TContext>`. (#298) `ClientHealthCheck` reports `ex.Message`; every
other health check in the codebase reports `ex.GetType().Name` (several with comments saying why).
(#299) The two `AddHealthCheck(kind, name, probe)` overloads catch `Exception` without a preceding
`catch (OperationCanceledException) { throw; }` — the #50/#114 sweep missed them because the catch
lives in a lambda, not an `IHealthCheck` implementer.

**Rulings:**

1. (#296) One-line change: wrap the write-back exactly as `WriteThroughAsync` does —
   `await SyncCacheAfterWriteAsync(ct => SetValueAsync(payload, expireIn, ct), "set", cancellationToken);`
   — with the same "database read already succeeded" comment. Check the 3-arg/mapping overloads of
   `LazyLoadAsync` (if any) for the same shape.
2. (#297) `OwnedRateLimiter : IAsyncDisposable, IDisposable` with
   `Dispose() => DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5))` — the bounded bridge the
   codebase now uses in `MeshAnnouncer` and `RedisCacheService`. Round 17's #289 found a deadlock in
   a disposal bridge; read that entry and mirror whatever shape it settled on (a bounded `Wait` with
   no captured sync-context is the safe form). Add a one-line rule to
   `src/Benzene.RateLimiting/CLAUDE.md` and to the repo `AGENTS.md` "Conventions": any container-owned
   type that implements `IAsyncDisposable` MUST also implement `IDisposable` with a bounded bridge —
   this is the third time this exact bug has been fixed.
3. (#298) `["error"] = ex.GetType().Name`. Update the package `CLAUDE.md` if it documents the field.
4. (#299) Add `catch (OperationCanceledException) { throw; }` ahead of the broad catch in both
   overloads, matching `HealthCheckError.Classify`'s behaviour.

**Red-green recipes:**

- `CacheEntryTest.LazyLoadAsync_CacheMiss_WriteBackThrows_StillReturnsTheSuccessfulDatabaseResult` —
  verbatim from the review (`FakeCacheEntry<string> { ThrowOnSet = true }`, assert
  `result.IsSuccessful && Payload == "from-database"`). Red today.
- `RateLimitingPipelineTest`: copy
  `InternallyCreatedLimiter_ReachableViaPublicApi_IsDisposedWhenTheContainerIsDisposed` as
  `..._IsDisposedWhenTheContainerIsDisposedSynchronously` and change the disposal line to
  `((IDisposable)provider).Dispose()`. Expected red: `InvalidOperationException`. Keep the async
  original.
- `ClientHealthCheckTest`: fake client throwing `new InvalidOperationException("host=10.0.0.5")`;
  assert `Data["error"] == nameof(InvalidOperationException)` and does not contain `10.0.0.5`.
- `HealthCheckBuilderExtensionsTest`: probe throwing `OperationCanceledException`; assert it
  propagates from the built check rather than yielding `Failed`. One case per overload.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~CacheEntryTest|FullyQualifiedName~RateLimiting|FullyQualifiedName~ClientHealthCheckTest|FullyQualifiedName~HealthCheckBuilderExtensionsTest"`.
Also re-run the #139/#199 and #249/#266/#289 regression tests by name (they live in the same two
files) to satisfy context point 2.

---

## WP-C — gRPC: unary/client-streaming trailer deferral (#300 high)

**Files.** `src/Benzene.Grpc/GrpcMethodHandler.cs` (`HandleAsync` ~30–45, `ClientStreamingAsync`
~62–78, `RunPipelineAsync` ~114–157, `WriteStreamAsync`/`ClassifyStreamException` ~159–194),
`src/Benzene.Grpc/Serialization/ProtobufJsonGrpcMessageAdapter.cs` (read only). Tests:
`test/Benzene.Grpc.Test/` (model on `GrpcNullResponseHostingTest.cs`).

**The finding.** #280 deferred the `benzene-status` trailer past the stream drain for the two
streaming shapes. The unary and client-streaming shapes still write `benzene-status: ok` inside
`RunPipelineAsync` (with `deferSuccessTrailer` false) and THEN call
`IGrpcMessageAdapter.ConvertResponse<TResponse>(ResponsePayload)` unguarded. `ConvertResponse` is a
first-class throw site (POCO/proto mismatch; `double.NaN` via `System.Text.Json` without
`AllowNamedFloatingPointLiterals`). Result: `StatusCode.Unknown`, no Benzene classification, no rich
error details, and a stale `ok` trailer — which `DefaultGrpcStatusReverseMapper` then prefers, handing a
Benzene client a result with `Status == "ok"` and `IsSuccessful == false`.

**Rulings:**

1. Move the "materialize the typed response" step (the `Response is TResponse` check and the
   `ConvertResponse` call) INSIDE the guarded region, before the trailer is written — the cleanest
   shape is for `RunPipelineAsync` to take a `Func<TResponse>`/materializer (or for the two
   non-streaming callers to pass `deferSuccessTrailer: true` and then write the trailer themselves
   after conversion succeeds, in the same try/catch that classifies via the existing
   `ClassifyStreamException` path, renamed to something shape-neutral if that helps). A conversion
   failure must produce the same trailer + `RpcException` + rich-details treatment a handler exception
   gets.
2. Do NOT change `ProtobufJsonGrpcMessageAdapter.SerializeOptions` to allow `NaN` literals as part of
   this WP — that is a separate wire-behaviour decision. File it as `[OPEN]` (should the JSON bridge
   carry `NaN`/`Infinity` the way proto3 JSON does?).
3. Update `src/Benzene.Grpc/CLAUDE.md`'s error-handling section so all four RPC shapes are described
   as classifying identically, and note the round-17/18 history in one sentence.

**Red-green recipe.** `GrpcConversionFailureHostingTest` (new, `TestServer` + real `GrpcChannel`, per
`GrpcNullResponseHostingTest`): a unary handler declaring a POCO response the target proto cannot accept
(or with a `double.NaN` field). Red assertions today: `ex.StatusCode == StatusCode.Unknown` and
`call.GetTrailers().GetValue("benzene-status") == "ok"`. After the fix: the status code the
`IGrpcStatusCodeMapper` assigns to the classified error and a non-`ok` `benzene-status` trailer.
Duplicate for client-streaming. Keep #280's streaming tests green.

**Verify:** `dotnet test test/Benzene.Grpc.Test -c Release`.

---

## WP-D — Benzene.Http: two parameters in one segment (#301)

**Files.** `src/Benzene.Http/Routing/CompiledRoutePath.cs` (constructor, ~24–41),
`src/Benzene.Http/Routing/UrlMatcher.cs` (read only). Tests:
`test/Benzene.Core.Test/Core/Http/UrlMatcherTest.cs` / `RouteFinderTest.cs`.

**The finding.** `Array.FindIndex(routeParts, x => x.StartsWith("{"))` takes only the first
parameter; a second `{param}` is concatenated verbatim into the literal `Suffix`, so `/files/{name}.{ext}`
compiles cleanly, passes `HttpRouteStartUpCheck`, appears in the spec, and 404s forever. Single
parameter per segment is a documented limitation; violating it silently is the bug.

**Ruling.** Throw a `BenzeneException` from the constructor when a segment contains more than one
part starting with `{`, naming the full pattern and the offending segment ("only one {parameter} per
path segment is supported"). `HttpRouteStartUpCheck` already forces construction at init, so this
becomes a fail-fast startup error with no new check type. Mention the new error in
`src/Benzene.Http/CLAUDE.md` next to the existing single-parameter note.

**Red-green recipe.** `CompiledRoutePath_TwoParametersInOneSegment_ThrowsAtConstruction` (red today —
no throw) plus a pinning test that today's instance never matches `["report.pdf"]` (delete it once the
constructor throws, or keep it as documentation of why). Cover `{a}{b}` (no separator) and
`{year}-{month}` too. Existing single-parameter prefix/suffix tests stay green.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~Core.Http"`.

---

## WP-E — Mesh aggregator/compositor correctness (#302, #303, #304, #295, #305 decision)

**Files.** `src/Benzene.Mesh.Aggregator/MeshAggregator.cs` (`BuildServiceAsync` ~998–1022,
`FetchSpecAsync`, `ParseTopics`/`ParseOutboundTopics`/`ParseTransports` ~1172–1297, the sort at ~514,
`ApplyCrossVersionCompatibility` ~541–573, `CompareVersions` ~575–642),
`src/Benzene.Mesh.Aggregator/AsyncApiCompositor.cs` (`Slug`, `SchemaKey`, merge loop ~61–159),
`src/Benzene.Mesh.Aggregator/MeshSnapshotBuilder.cs`, `src/Benzene.Mesh.Contracts/*` (only if an
additive field is added — see ruling 1), `src/Benzene.Schema.Compatibility/JsonSchemaComparer.cs`
(`Walk` ~58–145), `src/Benzene.Mesh.Reporting/MeshSelfReportMiddleware.cs` + its `CLAUDE.md`
(docs only). Tests: `test/Benzene.Mesh.Test/MeshAggregatorTest.cs`, `AsyncApiCompositorTest.cs`,
`MeshSelfReportMiddlewareTest.cs`,
`test/Benzene.Core.Test/Autogen/Schema/OpenApi/Compatibility/JsonSchemaComparerTest.cs`.

**The findings.** (#302) A spec endpoint answering 200 with a non-spec body (maintenance page, truncated
body, login HTML) yields `Error == null`, a valid-looking hash, and zero topics/edges/transports —
indistinguishable in every published artifact from a service that genuinely has none. The three parsers
already catch `JsonException` internally; they just discard the signal. (#303) `AsyncApiCompositor`
namespaces by `Slug(ServiceName)`, which is lossy (`"Orders API"` and `"orders_api"` → `orders-api`).
Channels (`channels[referenced] = ...`) and schemas (`schemas[schemaRenames[key]] = ...`) have no
collision guard (only operations go through `UniqueKey`), so the later service silently overwrites the
earlier one's channel/schema while the earlier one's `$ref`s already point at that key. (#304) Topic
versions are ordered `StringComparer.Ordinal`; `ApplyCrossVersionCompatibility` takes `siblings[i-1]`
as the predecessor. `v1, v10, v11, v2, ...` compares v10 against v1 and v2 against v11. (#295)
`JsonSchemaComparer.Walk` never reads `additionalProperties`, unlike its documented "behaviourally
identical" sibling `SchemaCompatibilityComparer` (fixed in #168) — a `Dictionary<string,string>` →
`Dictionary<string,int>` change is `Compatible` in the mesh UI. `MeshAggregator.InlineSchema` does
preserve `additionalProperties`, so the shape reaches the comparer routinely. (#305) The
deliberately-unawaited `_ = PublishBestEffortAsync()` is unreliable on Lambda (the package's own named
primary target), where the environment freezes when the handler's returned `Task` completes.

**Rulings:**

1. (#302) Make the three parsers return a parse outcome rather than swallowing: the smallest shape is
   a private `TryParseSpec(specJson, out JsonDocument)` done ONCE in `BuildServiceAsync` (the three
   parsers currently each re-parse the same string) — on failure, record
   `Error = "SpecParseError"`, `ErrorClass = <the existing class used for malformed/unexpected
   responses>` on the service result so it flows to `MeshServiceSnapshot`/`MeshManifestEntry` through
   the existing fetch-error path, and skip the topic/topology/transport contributions. Prefer reusing
   the existing `Error`/`ErrorClass` fields over adding a new one — adding a manifest field is a
   **spec change** (`benzene` repo, conformance fixtures) and is out of scope. If the existing
   `ErrorClass` vocabulary genuinely has no fitting value, stop and record `[OPEN]` rather than
   inventing one. A document that parses but has no `requests`/`events` (`"{}"`) stays a legitimate
   empty service.
2. (#303) Extend the existing `UniqueKey` collision guard to channels and schema keys (a colliding key
   gets the same disambiguating suffix operations already get), AND make `RewriteSchemaRefs` run
   against the post-disambiguation key map so the earlier service's `$ref`s follow its own schema.
   Do not change `Slug` itself (the prefix is human-facing and appears in the UI). Update the class doc
   comment so "nothing overwrites" is true again.
3. (#304) Stop relying on sort-adjacency. In `ApplyCrossVersionCompatibility`, order siblings with a
   numeric-aware comparer: reuse `Benzene.Mesh.Contracts`' `MeshVersionOrder`/`MeshVersionScheme` if
   it applies to the `vN`/`N` shapes topic versions use; otherwise a local "natural sort" (split
   digit runs, compare numerically) with ordinal fallback. Versions that are not mutually orderable
   under the chosen scheme get `Compatibility = null` (mirror `MeshVersionOrdering.NotOrderable`'s
   precedent) rather than a wrong-baseline verdict. The published `topics.json` order (the ~514 sort)
   may stay ordinal — this ruling is about which pair is COMPARED, not display order; if you do change
   the published order, that is a fixture-visible change and must be checked against
   `test/conformance-fixtures/`.
4. (#295) Add an `additionalProperties` branch to `Walk` mirroring
   `SchemaCompatibilityComparer.cs:187–199` and the existing `items` branch: both sides objects →
   recurse; one side only → `TypeChanged`. Add the case to the shared equivalence corpus so the
   parity assertion the doc comment promises actually covers it.
5. (#305, decision-shaped) Ship the doc change now: state plainly in
   `src/Benzene.Mesh.Reporting/CLAUDE.md` and the package's docs page that on AWS Lambda the
   opportunistic publish may be frozen before completion and is not reliable on a cold/bursty
   invocation pattern. Add the review's "publish has not started when `HandleAsync` returns" test as
   documentation of the mechanism. Record `[OPEN]`: should the middleware expose an awaited mode (or a
   flush hook the Lambda host calls before returning) for hosts with freeze semantics? Do not change
   runtime behaviour in this WP.

**Red-green recipes:**

- `MeshAggregatorTest.RunOnceAsync_SpecEndpointReturnsGarbageWith200_ServiceCarriesASpecParseError` —
  fake `IMeshServiceSource` returning `"<html>502 Bad Gateway</html>"` with a healthy health check;
  assert the manifest entry carries a non-null `Error` and contributes nothing to `topics.json`.
  Companion: `"{}"` still yields `Error == null`. Red today (`Error == null` for garbage).
- `AsyncApiCompositorTest.Merge_TwoServiceNamesWithTheSameSlug_KeepBothChannelsAndSchemas` —
  `"Orders API"` and `"orders_api"`, each with channel `created` and schema `Order` of different shape;
  assert two distinct channels and two distinct schemas, and that each service's operations/`$ref`s
  resolve to its own. Red today (one of each).
- `MeshAggregatorTest.RunOnceAsync_ElevenTopicVersions_V10IsComparedAgainstV9NotV1` — as in the
  review; assert `BaselineVersion == "v9"` for v10 and `"v1"` for v2. Red today.
- `JsonSchemaComparerTest`: baseline `{"type":"object","additionalProperties":{"type":"string"}}` vs
  `integer`; assert a `TypeChanged` and that `SchemaCompatibilityComparer` reports the same over the
  equivalent `OpenApiSchema` pair. Red today (zero changes).
- `MeshSelfReportMiddlewareTest.HandleAsync_ReturnsBeforeThePublishHasStarted` — documents #305;
  green today, kept as evidence.

**Verify:** `dotnet test test/Benzene.Mesh.Test -c Release --filter
"FullyQualifiedName~MeshAggregatorTest|FullyQualifiedName~AsyncApiCompositorTest|FullyQualifiedName~MeshSelfReportMiddlewareTest"`
and `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~JsonSchemaComparerTest"`.
Then `dotnet test test/Benzene.Conformance.Test -c Release` — rulings 1 and 3 touch published
artifact shapes.

---

## WP-F — Mesh collector/tracing (#306, #307, #308, #309)

**Files.** `src/Benzene.Mesh.Collector/MeshCollectorStore.cs` (`EnsureService` ~605, `EnsureTopic`
~636, `Heartbeat` ~258, `EnsureActivity` ~709, and the #290 `maxVersionsPerService` machinery to
mirror), `src/Benzene.Mesh.Collector/MeshTimeRangeResolver.cs` (~132–134),
`src/Benzene.Mesh.Tracing.Tempo/{PrometheusQueryClient,TempoServiceGraphTopologyBuilder,TempoTopologyMessageHandler}.cs`,
`src/Benzene.Mesh.Fleet.Tempo/TempoTraceSource.cs` (~107). Reference idiom:
`src/Benzene.Mesh.Dispatch/MeshDispatchMessageHandler.cs` (optional `ICancellationTokenAccessor`),
`src/Benzene.Mesh.Fleet.Jaeger/JaegerTraceSource.cs:152`. Tests: `test/Benzene.Mesh.Test/
{MeshCollectorStoreTest,TempoTopologyMessageHandlerTest,PrometheusQueryClientTest,TempoTraceSourceTest}.cs`
and a new `MeshTimeRangeResolverTest` if none exists (check first).

**The findings.** (#306) #290 capped `Descriptors` only. `_services`, `_topics`,
`ServiceState.Instances`, `_providerActivity`/`_consumerActivity` are still unbounded for the process
lifetime; Kubernetes pod churn grows `Instances` by one permanent entry per pod ever seen, and
`Benzene.Mesh.Host`'s default open ingestion lets anyone grow all of them at network speed. Query paths
iterate `Instances.Values`, so it is also an O(n) degradation. (#307) `Benzene.Mesh.Tracing.Tempo` has
zero `CancellationToken` anywhere; five sequential PromQL calls ignore `.UseTimeout(...)`. (#308)
`'w'`/`'M'`/`'y'` multiply in unchecked `long` before widening; `now-2635249153387078803w` wraps to
exactly 5 days instead of degrading to absent as the file's own contract states. (#309)
`TempoTraceSource.GetCorrelationAsync` omits the trailing token on `BoundedFanOut.WhenAllAsync`
(Jaeger passes it), so queued items still start after cancellation.

**Rulings:**

1. (#306) Mirror #290's shape: constructor-injected caps with defaults —
   `maxInstancesPerService` (evict the oldest `LastHeartbeat` when over cap; default 64),
   `maxServices` (default 1024), `maxTopics` (default 4096), `maxActivityPairs` (default 8192) with
   least-recently-touched eviction for the last three (record a "last seen" tick on each entry; the
   file already has `LastHeartbeat` for instances). Wire the new options through
   `Benzene.Mesh.Host`'s config section the way `maxVersionsPerService` is (find how #290 surfaced
   its cap and copy it; if it is not configurable from `mesh.sample.json`, keep parity and don't add
   config). Record `[OPEN]` alongside #290's existing open question: cap vs. heartbeat-TTL retirement
   for all five collections, and whether open ingestion should default to `sharedSecret` given this
   DoS shape.
2. (#307) The three-step idiom from the review: `QueryAsync(..., CancellationToken)`, thread through
   `BuildAsync`/`RunQueryAsync`/`QueryPerMinuteAsync`/`QueryLatencyMsAsync`, and give
   `TempoTopologyMessageHandler` a constructor-optional `ICancellationTokenAccessor?` resolved from
   DI, exactly as `MeshDispatchMessageHandler` does. Additive only.
3. (#308) `TimeSpan.FromDays((double)n * 7)` etc. so the multiplication is in `double` and
   `FromDays`' own overflow check applies (the `'d'` path already benefits). Keep the existing
   `catch (OverflowException)`.
4. (#309) Add `, cancellationToken` to the `WhenAllAsync` call. One line.

**Red-green recipes:**

- `MeshCollectorStoreTest.Heartbeat_ManyDistinctInstanceIdsForOneService_InstancesAreCappedAtTheConfiguredMax`
  (5,000 heartbeats, assert `Service(name)!.Instances.Count <= cap` and that the most recent are the
  ones kept); equivalents for `Register`/`AddEvents` with many distinct service/topic names and
  activity pairs. All red today (counts reach 5,000). #290's
  `Register_ManyDistinctVersions_DescriptorsAreCappedAtTheConfiguredMax` stays green.
- `TempoTopologyMessageHandlerTest`: wrap the handler in `Benzene.Resilience`'s timeout middleware at
  50 ms over a stalling `HttpMessageHandler`; red today (runs the full stall). Plus the "actual token
  instance reaches `GetAsync`" assertion per the WP-C (round 14–15) convention.
- `MeshTimeRangeResolverTest.ParseDuration_LargeWeekCountThatWrapsInLong_TreatedAsAbsent` —
  `From = "now-2635249153387078803w"` → `null`, not `now - 5d`. Red today. Parallel `M`/`y` cases
  (compute the wrap constants; the review explains the method).
- `TempoTraceSourceTest`: more matches than `SearchConcurrency`, token cancelled immediately; assert
  fetch attempts started ≤ `SearchConcurrency`. Red today.

**Verify:** `dotnet test test/Benzene.Mesh.Test -c Release --filter
"FullyQualifiedName~MeshCollectorStoreTest|FullyQualifiedName~Tempo|FullyQualifiedName~Prometheus|FullyQualifiedName~MeshTimeRangeResolver"`
then `dotnet test deploy/Mesh/Benzene.Mesh.Host.Test -c Release` (ruling 1 may touch host config).

---

## WP-G — Outbound clients: Pub/Sub cancellation, Azure batch creation (#311, #312)

**Files.** `src/Benzene.Clients.GoogleCloud.PubSub/{PubSubClientMiddleware,Extensions}.cs`,
`src/Benzene.Clients.Azure.ServiceBus/ServiceBusBatchMessageClient.cs` (~59, ~99, `finally` ~120),
`src/Benzene.Clients.Azure.EventHub/EventHubBatchMessageClient.cs` (`SendGroupAsync` ~104, ~124,
~143). Reference: `src/Benzene.Clients.Http/HttpClientMiddleware.cs` (#270's given-instance resolve),
`src/Benzene.RabbitMq/RabbitMqSendMessage/RabbitMqClientMiddleware.cs` (#236). Tests:
`test/Benzene.Core.Test/Clients/Azure/{EventHub,ServiceBus}/*CancellationTest.cs` (shape to copy),
`test/Benzene.Core.Test/Clients/Azure/BatchMessageClientTest.cs`; new
`test/Benzene.Core.Test/Clients/Google/PubSubClientMiddlewareCancellationTest.cs` (note: the existing
`test/Benzene.Core.Test/Google/` folder is the INGRESS side — keep the egress test under `Clients/`).

**The findings.** (#311, found independently by two agents) `PubSubClientMiddleware` has no
`ICancellationTokenAccessor` and calls `PublishAsync` with no token at all — the ninth single-send
transport, missed by #268's sweep. (#312) Both Azure native-batch clients call
`CreateMessageBatchAsync`/`CreateBatchAsync` (network calls) once before any `try` and once mid-loop
inside a `try/finally` with no `catch`. A throw there escapes `SendBatchAsync`, discarding accumulated
`failures` and the implicit successes of already-sent chunks, breaking the "returns a `BatchSendResult`,
never throws for transport failure" contract the other four batch clients honour.

**Rulings:**

1. (#311) Constructor-optional `ICancellationTokenAccessor? cancellation = null`; thread
   `_cancellation?.CancellationToken ?? CancellationToken.None` into `PublishAsync(topic, messages,
   cancellationToken)`; DI overload gets it by injection, given-instance overload via
   `serviceResolver.TryGetService<ICancellationTokenAccessor>()` per #270. Update the package
   `CLAUDE.md` and `docs/capability-matrix.md`'s cancellation row.
2. (#312) Wrap both creation call sites per file in a try/catch that maps the exception onto
   `FailedBatchEntry` records for every index not yet reported (the remaining, not-yet-batched entries
   of the current chunk/group) and returns normally. For the initial call that means every entry is a
   failure with the exception's type/message per the existing `FailedBatchEntry` convention. Keep the
   `finally` disposal.

**Red-green recipes:**

- `PubSubClientMiddlewareCancellationTest` — mocked `PublisherServiceApiClient` capturing the token;
  assert the exact instance from a fake accessor reaches `PublishAsync`. Today it cannot even be
  written against the current signature (that IS the red).
- `BatchMessageClientTest` (Azure): `CreateMessageBatchAsync` succeeds once then throws
  `ServiceBusException`; enough messages to roll; assert no throw, first-batch entries absent from
  `Failures`, remaining entries present. Same for `EventHubsException` in the Event Hub client. Also a
  "first creation throws" case: all entries in `Failures`, no throw. Red today (exception escapes).

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter
"FullyQualifiedName~Clients.Google|FullyQualifiedName~Clients.Azure"`.

---

## WP-H — RabbitMQ worker: channel-shutdown recovery (#310 high)

**Files.** `src/Benzene.RabbitMq/RabbitMqWorker.cs` (`StartAsync`/`StopAsync` ~66–141,
`AckAsync`/`NackAsync` ~202–240), reference
`src/Benzene.RabbitMq/RabbitMqSendMessage/RabbitMqMandatoryPublishCoordinator.cs:307–321`
(`OnChannelShutdownAsync`, the outbound-side solution), `src/Benzene.RabbitMq/CLAUDE.md`. Tests:
`test/Benzene.Core.Test/RabbitMq/RabbitMqWorkerTest.cs`.

**The finding.** Delivery tags are per channel-open lifetime. After connection auto-recovery, an
in-flight handler acks with a stale tag → `PRECONDITION_FAILED` → broker closes the CHANNEL (not the
connection, so auto-recovery does not repair it). `AckAsync`/`NackAsync` swallow into `LogError`; the
consumer is dead; nothing observes it; the separate health-check connection keeps reporting healthy. The
outbound coordinator subscribes to `ChannelShutdownAsync` for exactly this; the worker does not.

**Rulings:**

1. Subscribe to `_channel.ChannelShutdownAsync` in `StartAsync`. On a shutdown not initiated by
   `StopAsync` (track a `_stopping` flag the way the coordinator distinguishes graceful close), reopen a
   fresh channel + consumer against the connection (which auto-recovery keeps alive) with the same
   prefetch/QoS and resume consuming — option (a) from the review. Bound the reopen with a small retry
   (e.g. 3 attempts, backoff) and on exhaustion fall back to option (b): log `Critical` and fault the
   worker through whatever signal `BenzeneKafkaWorker`'s `onFault` path uses, so a supervisor restarts
   the process instead of it idling silently. Read the Kafka worker's fault path before choosing the
   signal so both workers report faults the same way.
2. In `AckAsync`/`NackAsync`, distinguish "channel already closed" (`AlreadyClosedException` /
   `!channel.IsOpen`) from a transient failure in the log message and level, so the serious condition
   is visible in logs even before the reopen kicks in.
3. Update `src/Benzene.RabbitMq/CLAUDE.md`: the health-check section's "does not share the worker's
   connection" note should now say the health check cannot see a dead consuming channel and that the
   worker's own shutdown handler is what covers it (addresses the review's LOW note without a
   separate task).

**Red-green recipe.** `RabbitMqWorkerTest`: mocked `IChannel` whose `BasicAckAsync` throws and whose
`IsOpen` flips to `false`, then raise the mock's `ChannelShutdownAsync` event with a non-graceful
`ShutdownEventArgs`. Assert the worker creates a new channel on the connection mock and re-registers a
consumer (verify `CreateChannelAsync` called twice and `BasicConsumeAsync` twice). Red today (once each).
Second test: `StopAsync`-initiated shutdown does NOT trigger a reopen. Third: reopen exhausted → fault
signal observed.

**Verify:** `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~RabbitMq"`.

---

## WP-I — AWS stores: CloudWatch page merge, DynamoDB outbox lease boundary (#313, #314)

**Files.** `src/Benzene.Mesh.Usage.CloudWatch/CloudWatchUsageSource.cs` (`GetMetricDataAsync`,
`FetchUsageAsync`), `src/Benzene.Outbox.DynamoDb/DynamoDbOutboxStore.cs` (`TryClaimAsync` ~263–299,
condition at ~273–276). Tests: `test/Benzene.Mesh.Test/CloudWatchUsageSourceTest.cs`,
`test/Benzene.Core.Test/Outbox/DynamoDb/DynamoDbOutboxStoreTest.cs`.

**The findings.** (#313) `GetMetricData`'s `NextToken` continues a single query's series across pages
with the SAME `Id`; the helper flattens pages, so `FetchUsageAsync` emits two same-dimension
`MeshUsageEntry` rows each holding a fragment — violating `docs/mesh-usage-feed.md` §2 ("entries from
one source never overlap"). Reachable at ~70 live dimension combinations in the default 24 h / 60 s
window. Same class as round 17's XRay dedup (#274). (#314) The claim condition uses
`leaseUntil < :now` while `InMemoryOutboxStore`, `EntityFrameworkOutboxStore` and
`OutboxOptions.ClaimLease`'s doc are inclusive (`<=`); same class as #272.

**Rulings:**

1. (#313) Replace the flat list with a `Dictionary<string, double>` of per-`Id` summed `Values`
   (the caller only reads `.Id` and `.Values.Sum()`), accumulated across pages; `FetchUsageAsync`
   iterates the dictionary. One entry per `Id`, by construction.
2. (#314) `leaseUntil <= :now`. Add a sentence to the class doc comment stating the inclusive
   boundary matches the other two stores.

**Red-green recipes:**

- `CloudWatchUsageSourceTest.FetchUsageAsync_QuerySeriesSplitAcrossNextTokenPages_YieldsOneEntryWithTheSummedCount`
  — mock returns page 1 (`NextToken = "p2"`, partial `Values`) then page 2 (same `Id`, rest); assert
  `Assert.Single(entries.Where(e => e.Topic == ...))` with `Count` = both pages' sum. Red today (two
  entries).
- `DynamoDbOutboxStoreTest.ClaimAsync_ConditionAllowsReclaimAtExactlyLeaseUntil` — capture the
  `UpdateItemRequest` and assert the condition text contains `leaseUntil <= :now` (and not `<`). Red
  today. Mirror `InMemoryOutboxStoreTest`'s lease-lapse test at the exact tick so the three stores
  have the same boundary case on record.

**Verify:** `dotnet test test/Benzene.Mesh.Test -c Release --filter "FullyQualifiedName~CloudWatchUsageSourceTest"`
and `dotnet test test/Benzene.Core.Test -c Release --filter "FullyQualifiedName~Outbox"`.

---

## WP-J — AwsMesh Terraform/workflow (#316, #315 decision, #317 decision)

**Files.** `examples/AwsMesh/deploy/variables.tf` (`mesh_lambda_reserved_concurrency` ~54–58,
`mesh_dispatch_throttling_rate_limit` ~196, `mesh_dispatch_max_per_target_per_minute` ~214;
`trace_sample_rate` ~76–85 is the validation pattern to copy), `examples/AwsMesh/deploy/main.tf`
(the concurrency comment ~670–716; dispatch route settings ~863–872),
`.github/workflows/mesh-example-aws-deploy.yml` (input ~24–27, apply step ~194–219, "Show URLs"
~247–249), `examples/AwsMesh/README.md`. Optional unit test:
`test/Benzene.Mesh.Test/MeshDispatchRateLimiterMultiInstanceTest.cs`.

**The findings.** (#316) `mesh_lambda_reserved_concurrency` has no `validation` block; `0` is AWS's
documented "cannot be invoked" value and takes down the whole mesh surface with a clean apply. (#315)
Raising concurrency 1→10 means up to 10 warm environments each with its own in-memory
`MeshDispatchRateLimiter`; the per-target guard is now effectively bounded by the API Gateway edge
throttle (~120/min) rather than `MaxPerMinutePerTarget = 30`. Neither the long resource comment nor the
variable descriptions mention it. (#317) `mesh_allowed_emails` blank = reset to the single-owner
default, documented only in input help text; nothing in the run output says what allowlist the apply
will produce.

**Rulings:**

1. (#316) Add `validation { condition = var.mesh_lambda_reserved_concurrency > 0 ... }` with an error
   message naming the outage (mirror `trace_sample_rate`'s phrasing). Pure fix, no decision.
2. (#315, ship now) Add the interaction to the `main.tf` concurrency comment and to
   `mesh_dispatch_max_per_target_per_minute`'s description ("at concurrency N this bounds one warm
   instance; the edge throttle is the fleet-wide bound"). Add the review's multi-instance unit test
   (N independent limiters round-robin accept up to ~N× the ceiling) as documentation of the
   mechanism. Record `[OPEN]`: retune `mesh_dispatch_throttling_rate_limit`/burst downward, or split
   the Lambda into a low-concurrency write function (aggregation + dispatch) and an uncapped read
   function — the split is the real fix the resource comment already floats.
3. (#317, ship now) Option (a) only: before `terraform apply`, print the resolved allowlist the run
   will apply (the parsed `emails_json`, or the literal default from `variables.tf` when the input is
   blank) as a clearly-labelled step, and repeat it in the closing "Show URLs" step via
   `terraform output` (add an output for it if none exists). Record `[OPEN]`: should blank mean
   "keep current" (read the live env var / `terraform output` first) instead of "reset" — a semantic
   change to a documented input, the maintainer's call.

**Verify:** `terraform -chdir=examples/AwsMesh/deploy validate`, then
`terraform -chdir=examples/AwsMesh/deploy plan -var mesh_lambda_reserved_concurrency=0 ...` which MUST
fail at validation (the only red→green in this WP). If Terraform is unavailable locally, say so and
verify via the deploy workflow's plan step. For the workflow change, `actionlint` if available; the
coordinator confirms the new echo step on the next real deploy run. `dotnet test test/Benzene.Mesh.Test
-c Release --filter "FullyQualifiedName~MeshDispatchRateLimiter"` for the optional unit test.

---

## Coordination notes

- **Merge order: WP-A first** (security), then any order, WP-E and WP-F last (largest mesh
  surface; WP-E ruling 1/3 may touch conformance-visible artifacts and needs the
  `Benzene.Conformance.Test` run in the centralized baseline).
- **No two WPs share a source file.** Adjacencies: WP-E (#295) edits `Benzene.Schema.Compatibility`
  while WP-A edits `Benzene.CodeGen.*` — different packages, but both have tests under
  `test/Benzene.Core.Test/Autogen/`; no file overlap. WP-B (#297) and WP-G (#311) both touch the
  cancellation/disposal rows of `docs/capability-matrix.md` — coordinator hand-splices. WP-F (#306)
  and the existing #290 code sit in the same method family in `MeshCollectorStore.cs`; keep #290's
  cap and test untouched.
- **Cross-language note:** WP-E ruling 1 must NOT add a new manifest field. If the fixer concludes a
  new field is the only honest shape, that goes to the `benzene` repo as a spec change first and the
  WP ships only the "reuse `Error`/`ErrorClass`" version here.
- **`[OPEN]` entries to record** (in `outstanding-bugs.md`'s maintainer-decisions section): #305
  awaited/flush mode for Lambda hosts; #315 edge-throttle retune vs. Lambda split; #317 blank-input
  semantics; #306 cap-vs-TTL for all five collector collections (extend #290's existing entry) and
  whether `Benzene.Mesh.Host` ingestion should default to `sharedSecret`; #300's `NaN`/`Infinity`
  JSON-bridge question.
- **Regression guard, explicitly:** after all merges, the regression tests for #139, #199, #249,
  #266, #268, #270, #280, #289, #290 must still pass — WP-B/C/F/G repair or extend those fixes, not
  replace them.
- **Items deliberately NOT filed** (no action): the RabbitMQ health-check-connection LOW note (folded
  into #310's doc ruling); everything the Azure review swept clean; the `benzene-ui` feed-error
  threading and `mesh-example-aws-logs.yml` metrics step (both swept clean in the fresh-changes
  review); the cross-language `Benzene` repo's stale vendored `mesh-ui.html` copies (belongs to that
  repo, noted in the session log).
