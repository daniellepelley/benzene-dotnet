> ARCHIVED 2026-08-20: actioned; shipped (`src/Benzene.Descriptor`, `src/Benzene.CodeGen.Build`, `src/Benzene.CodeGen.Client`; dogfooded in `examples/CodeGen`).

# Spec + Mesh Tooling — Implementation Plan

**Status:** Approved plan, ready to implement (maintainer approved all items 2026-08-09)
**Date:** 2026-08-09
**Source:** [`spec-mesh-interconnection-dx-assessment-2026-08.md`](spec-mesh-interconnection-dx-assessment-2026-08.md) —
read it first for the *why*; this document is the *how*.
**Audience:** implementation agents. Each phase below is written to be picked up as a
self-contained task by an agent without further product decisions. Do the phases in order unless a
phase's "Depends on" says otherwise.

**Decisions already made (do not re-litigate):**
1. `tools/Benzene.Descriptor` **is approved** to join `Benzene.sln` under `src/` conventions.
2. The mesh artifact store **is** the contract registry (no new registry system).
3. `benzene diff` / `compat-check` **fail CI on breaking changes by default**, with `--warn-only`
   to opt out.
4. The `version` tag on `benzene.messages.processed` **is approved** (Phase 7).

---

## 2026-08-12 — owner design review: Phase 1 reconfirmed, three amendments

The owner independently re-stated Phase 1 as the first deliverable — a build tool that pulls a
service's metadata (the spec, the mesh endpoint descriptor) out at build time into JSON files that
can be stored or fed straight into code generation — without having this plan in front of them.
That is a reconfirmation, not a new requirement. A review against the code
(`tools/Benzene.Descriptor`, the spike outputs, the codegen estate) settled the open deltas.
**A Phase 1 implementer must read this section before the phase; where it conflicts with the phase
text below, this section wins.**

### Amendment A — `--emit descriptor` emits the mesh §2 wire shape, not the distilled projection

Phase 1 step 3 says `descriptor` → "current `service.json` output". That current output
(`DescriptorEmitter.Distil`, `descriptorVersion: 0.1`) is a bespoke deployment projection — neither
of the two wire shapes. Amended: **`--emit descriptor` emits the mesh ServiceDescriptor exactly as
pinned by the spec repo** (`Benzene/docs/specification/mesh.md` §2, hash per §2.2 — i.e.
`MeshJson.Serialize(MeshDescriptorFactory.Create(...))`, which `DescriptorEmitter.BuildMesh` already
computes internally and then mostly discards). The default for `--emit` flips from `descriptor` to
`both`.

Why exact wire shapes: interchangeability is already real in the code — the mesh stores a fetched
spec **verbatim, never deserialized** (`MeshServiceSnapshot.SpecJson`), and `benzene:mesh:register`'s
body *is* the §2 shape, so a build-emitted artifact is drop-in indistinguishable from a live fetch
(the static-floor story). And the §2 shape is already covered by
`conformance/mesh-descriptor-cases.json`, so the build artifact gets conformance coverage for free —
no new envelope, no new fixture. Provenance (built-at, git sha) stays out-of-band in CI artifact
metadata, never inside the contract document.

The distilled deployment projection (`consumes`/`produces`/`transportKind`/`transportsResolved`) is
**renamed `--emit deploy` → `.deploy.json` and deferred**: its most IaC-relevant field rests on
`OutboundRouteInspector`'s best-effort private-field reflection (the README says it is "meant to be
replaced"), and `destinationRef` is absent pending the outbound read-model
([`benzene-clients-redesign-plan-2026-07.md`](benzene-clients-redesign-plan-2026-07.md) §2.2 /
[`benzene-outbound-model-plan.md`](../benzene-outbound-model-plan.md)). Shipping a versioned public
artifact whose load-bearing fields are spike-grade creates a compat obligation we should not take
yet.

### Amendment B — `Benzene.CodeGen.Terraform` is deprecated (supersedes the design doc's stance)

The owner's call: the opinionated codegen outputs, Terraform first among them, "are not
particularly useful and should be deprecated." This **supersedes**
[`deployment-descriptor-design.md`](../deployment-descriptor-design.md)'s recorded position
("Repositions, doesn't deprecate") and its recommendation 5 ("Keep `Benzene.CodeGen.Terraform`").
The descriptor makes the correct boundary obvious — logical needs + env-var contracts are the
product; rendering IaC is the operator's house style — and a Benzene-owned `.tf` renderer is a
liability that implies support for every dialect decision it bakes in.

Path (the package never shipped to NuGet — `IsPackable=false` — so `[Obsolete]` serves nobody):
1. **In Phase 1 (docs-only, one paragraph):** deprecation banner on `docs/terraform.md`, fix its
   overclaim ("Benzene includes tools to automatically generate Terraform configuration" — it is
   not shipped), and note the replacement posture (descriptor artifact + reference generator).
   Update `docs/index.md`'s Code Generation entry accordingly. Freeze the package: no new features,
   no new tests; resolve `src/Benzene.CodeGen.Terraform/CLAUDE.md`'s "fork in the road" note to
   "resolved: deprecated, frozen."
2. **After the reference cookbook exists (follow-up, not Phase 1):** delete
   `Benzene.CodeGen.Terraform`, its tests (`test/Benzene.Core.Test/Autogen/CodeGen/Terraform/`),
   and `docs/terraform.md`. `Benzene.CodeGen.ApiGateway` gets the same treatment unless a real
   consumer surfaces (its CLI `--output api-gateway` switch case goes with it).
   `Benzene.CodeGen.Markdown` is already superseded by `Benzene.Spec.Ui` (its consumer was removed)
   and retires on the same pass.
3. **Salvage as documentation, not code:** the event-wiring semantics buried in the generators —
   SNS `filter_policy` over Benzene topics, EventBridge `event_pattern` matching `detail-type`
   against topics verbatim — move into the planned reference "`.json` → your-house-style IaC"
   cookbook ("teach the pattern, don't own the policy").

**Explicitly not deprecated:** `Benzene.CodeGen.Client` (+ Atomic/MessageHandler builders — the
strategic clients-from-spec surface, per [`benzene-clients-vision.md`](../benzene-clients-vision.md)
§2.5), `CodeGen.Core`, `CodeGen.LambdaTestTool`, the `benzene` CLI, and the source generators.

### Amendment C — small Phase 1 additions and settled questions

- **Implement `--startup <fullTypeName>`**: `DescriptorEmitter.FindStartUpType`'s
  multiple-candidates error message already tells the user to pass a flag that does not exist in
  the parser. Add it in step 3's parser work.
- **Input contract is settled — the DLL, full stop.** csproj-as-input is the wrong layer (the tool
  would re-implement what MSBuild already knows; the `.targets` file passing `$(TargetPath)` is the
  csproj integration). Source-level extraction is structurally insufficient: produced events and
  wired transports are imperative (`AddResponseEventDeclarations(...)`, `.UseSqs(...)`) and only
  executing registration materialises them — the design doc's extractability table is the
  authority. The source generators remain a complement (consumer-side static scans), never the
  mechanism.
- **Architecture is settled — keep the standalone ALC tool.** The design doc preferred an
  in-build-context MSBuild task; the prototype shipped the standalone tool and inherited the ALC
  version-pinning risk the doc predicted. Step 4's loud version-skew detection is the accepted
  mitigation; revisit the in-context task only if skew failures show up in real use.
- **Output location:** next to build output (`$(TargetDir)$(AssemblyName).spec.json` /
  `.service.json`), `--output` overridable. No new well-known artifacts folder — decision 2 above
  stands: the mesh artifact store is the contract registry; build outputs are transient.
- **Cross-language:** the descriptor shape is already language-neutral and conformance-pinned
  (mesh.md §2 + fixture); the spec document (`EventServiceDocument`) is deliberately .NET-side and
  is not being promoted to the spec repo in this round. The extraction mechanism is per-language by
  nature. At most, when a second port actually builds this, the porting guide gains one informative
  sentence ("a build-time-emitted descriptor MUST equal the wire descriptor for the same build,
  `instanceId` aside") — nothing goes in the spec now.
  **Superseded 2026-08-13 by `cross-language-clients-plan-2026-08.md` Phase 1**: the spec document is now
  promoted, as `docs/specification/contract-document.md` in the Benzene spec repo, with a
  language-neutral `contractHash` algorithm and conformance fixtures — see that plan for why
  cross-language client generation reverses this deferral by necessity.

---

## §0. Ground rules for every phase — READ FIRST

**Repos.** Main work is in **benzene-dotnet**. Phase 8 also touches the cross-language **Benzene**
(spec/website) repo. Never edit conformance fixtures (`docs/specification/conformance/` in the
Benzene repo) in any phase of this plan.

**Verify before you edit.** File paths and APIs cited here were verified on 2026-08-09 but the repo
moves fast. Before changing a file, read it; if a cited symbol has moved/renamed, follow it — the
*intent* of the step is authoritative, the line numbers are not.

**Project conventions (benzene-dotnet):**
- New `src/` csproj shape (copy an existing sibling, e.g. `src/Benzene.MapReduce/Benzene.MapReduce.csproj`):
  `TargetFramework=net10.0`, `ImplicitUsings`, `Nullable`, `AssemblyName`, `RootNamespace`,
  `GenerateDocumentationFile=true`. Version/metadata/SourceLink come from `src/Directory.Build.props`
  — do not set them per-project. No central package management; AWS SDK packages pin `3.7.301.4`.
- **Solution registration** (`Benzene.sln`): a new project needs (a) a `Project(...)` entry before
  `Global`, (b) a full Debug/Release × AnyCPU/x64/x86 configuration block in
  `ProjectConfigurationPlatforms` (copy an existing project's 12 lines, swap the GUID), (c) a
  `NestedProjects` entry if it belongs in a solution folder. Generate a fresh GUID; never reuse one.
- **Tests** live in the single `test/Benzene.Core.Test/Benzene.Test.csproj` project (xunit + Moq,
  **no FluentAssertions**), organized by folder. A new src package must be added as a
  `ProjectReference` there. CLI/codegen tests live under `test/Benzene.Core.Test/Autogen/`.
- Registration extends `IBenzeneServiceContainer` (not `IServiceCollection`); use
  `TryAdd*` for defaults a caller may override; last registration wins for `IRequestMapper`-style
  overrides.
- Every new package gets a `CLAUDE.md` (copy the tone/length of `src/Benzene.MapReduce/CLAUDE.md`).
- Docs: Markdown in `docs/`; cookbooks in `docs/cookbooks/`; every doc reachable from
  `docs/index.md`. Match the voice of neighbouring docs. The website build in the Benzene repo
  link-checks these — never link to a file that doesn't exist.

**Verification discipline (every phase):**
```bash
dotnet build Benzene.sln -v q          # 0 errors (warnings pre-exist; add none)
dotnet test test/Benzene.Core.Test/Benzene.Test.csproj --filter "FullyQualifiedName~<YourArea>"
```
plus the phase's own acceptance checks. Commit per phase with a conventional message
(`feat(codegen): ...`, `docs: ...`); push to the designated branch.

**CLI architecture primer** (read once — Phases 2, 3, 5 build on it):
- `src/Benzene.CodeGen.Cli/Program.cs` — thin `Main`; interactive REPL when `args` is empty,
  otherwise one-shot `ConsoleApplication.ExecuteAsync(args)`. **`Main` currently returns `Task` and
  never sets an exit code.**
- `src/Benzene.CodeGen.Cli.Core/ConsoleApplication.cs` — instantiates every command in its
  constructor and hands them to `CommandRouter`. **New commands must be added to this constructor.**
- Commands subclass `CommandBase<TPayload>` (name + description in the ctor); payload properties
  carry `[Arg(Name = Constants.X, Description = ..., DefaultValue = ...)]` from
  `Parsing/ArgAttribute.cs`; arg names/descriptions are `const`s in
  `src/Benzene.CodeGen.Cli.Core/Constants.cs` — add new ones there, matching style.
- `Commands/Build/CodeBuilderFactory.cs` maps `--output` → builder: `client` (default,
  `MessageClientSdkBuilder`), `topic-client` (`AtomicClientSdkBuilder`), `message-handlers`
  (`MessageHandlerBuilder`), `readme`, `api-gateway`. It derives service name/namespace from
  `payload.LambdaName` via `LambdaNameParser`.
- The CLI packs as dotnet tool **`benzene`** (`PackAsTool` in `Benzene.CodeGen.Cli.csproj`).

---

## Phase 1 — Productize `Benzene.Descriptor` (the contract artifact)

**Goal:** every service can emit `{Service}.spec.json` (the `EventServiceDocument`, codegen/gate
input) and `{Service}.service.json` (the mesh descriptor) as build artifacts, via a shipped
`benzene-descriptor` dotnet tool and an opt-in MSBuild hook.
**Depends on:** nothing. **Effort:** S–M. *Unlocks all later phases.*

Existing code: `tools/Benzene.Descriptor/` — `Program.cs` (hand-rolled arg parser: `--assembly`,
`--output`, `--service`, `--service-version`, `--cloud`, `--region`, `--host`),
`DescriptorEmitter.cs`, `HostAdapters.cs` (neutral + aws-lambda), `OutboundRouteInspector.cs`,
`ServiceLoadContext.cs` (ALC), `build/Benzene.Descriptor.targets`, and a thorough `README.md`.
Design doc: [`deployment-descriptor-design.md`](../deployment-descriptor-design.md).

Steps:
1. **Move** `tools/Benzene.Descriptor/` → `src/Benzene.Descriptor/` (git mv, keep history). Update
   the csproj's relative `ProjectReference` paths (`..\..\src\...` → `..\...`). Keep
   `PackAsTool`/`ToolCommandName=benzene-descriptor`; **remove** `PackageOutputPath=./nupkg` (the
   repo's pack pipeline decides output); align the PropertyGroup with sibling csprojs (add
   `GenerateDocumentationFile` only if it compiles clean — this is an exe, doc-comments optional).
2. **Register in `Benzene.sln`** per §0 (project entry + config block).
3. **Add `--emit spec|descriptor|both`** to `Program.cs`'s parser and thread it through
   `DescriptorEmitter`. *(AMENDED 2026-08-12 — see the amendments section above, which wins:
   `descriptor` emits the mesh §2 ServiceDescriptor wire shape, not the distilled `Distil()`
   output; the default is `both`; the distilled projection is deferred as `--emit deploy`. Also
   add the `--startup <fullTypeName>` flag per Amendment C.)*
   - `descriptor` → ~~current `service.json` output~~ the mesh §2 ServiceDescriptor (Amendment A).
   - `spec` → serialize the `EventServiceDocument` the emitter already computes internally
     (it constructs the app and reads the spec — find where it obtains the document and add a
     serialization path reusing `Benzene.Schema.OpenApi`'s existing serializer, the same shape
     `EventServiceDocumentDeserializer` reads back).
   - `both` → write `<name>.spec.json` and `<name>.service.json` next to each other. When
     `--output` names a file and `--emit both`, treat `--output` as the *descriptor* path and
     derive the spec path by replacing `.service.json`→`.spec.json` (or appending `.spec.json`).
4. **Fail loudly**: exit code 0 only on success; non-zero with a one-line reason on: assembly not
   found, no `BenzeneStartUp`/entry type found, Benzene version mismatch between tool and service
   (the README's ALC pinning risk — detect by comparing `Benzene.Core` assembly identity loaded in
   the ALC vs the tool's own, and print both versions), and any exception during construction.
5. **Targets file**: extend `build/Benzene.Descriptor.targets` with
   `<BenzeneDescriptorEmit Condition="...">both</BenzeneDescriptorEmit>` passed as `--emit`, and
   **remove `ContinueOnError="true"`** (a failing emit should fail the build — that's the point).
   Pack the targets into the tool package (`<None Include="build\**" Pack="true" PackagePath="build\"/>`)
   AND document the copy-into-repo alternative in the README (tool packages don't auto-import
   targets; the README must show the `.config/dotnet-tools.json` + explicit
   `<Import Project="...">` or copied-targets patterns honestly).
6. **CI**: add the project to `deploy-benzene.yml`'s pack/push if the sln pack doesn't already
   cover it once it's in the sln (read the workflow; it packs the solution — verify the new project
   is picked up and produces a `.nupkg` in a dry run: `dotnet pack src/Benzene.Descriptor -o /tmp/pk`).
7. **Tests** (`test/Benzene.Core.Test/Autogen/Descriptor/DescriptorEmitterTest.cs`): point the
   emitter at an already-built example assembly (e.g. `examples/Aws/Benzene.Examples.Aws.Minimal`'s
   output — build it in the test's fixture or use a `[Fact]` gated on the file existing); assert
   `--emit both` writes two parseable files, the spec round-trips through
   `EventServiceDocumentDeserializer`, and a bogus `--assembly` path exits non-zero. If in-test
   process-spawning is awkward, factor `DescriptorEmitter` so its core is callable in-process and
   test that; keep `Program.cs` a thin shell.
8. **Docs**: new `docs/contract-artifacts.md` — what the two artifacts are, the tool install
   (`dotnet tool install -g benzene-descriptor` once published; local-tool manifest alternative),
   the MSBuild opt-in, CI upload snippet (`actions/upload-artifact`), and the version-pinning
   caveat. Link from `docs/index.md` under **Code Generation**. Update
   `src/Benzene.Descriptor/README.md` status line (no longer a spike; sln-approved).

**Acceptance:** sln builds; `dotnet pack` yields the tool; running it against a built example emits
both artifacts; bad input → non-zero exit; docs page linked from index; tests green.

---

## Phase 2 — CI-safe CLI + `benzene diff`

**Goal:** the `benzene` CLI becomes usable in CI (real exit codes), and gains a `diff` verb over
the existing compatibility comparer.
**Depends on:** nothing (pairs naturally with Phase 1's artifact). **Effort:** S.

Existing code to fix:
- `Commands/Build/ClientCodeBuilder.cs` — `catch (Exception ex) { Console.Error.WriteLine(ex); }`
  swallows all failures → exit 0.
- `Commands/Spec/AwsLambdaSpecClient.GetSpecAsync` returns `null` on error; callers then NRE.

Steps:
1. **Exit codes, one mechanism:** change `Program.Main` to `static async Task<int> Main` returning
   `0`/`1`; `ConsoleApplication.ExecuteAsync` lets exceptions propagate (keep the REPL loop's local
   try/catch); commands signal failure by throwing. Remove the swallow in `ClientCodeBuilder`
   (let it throw after printing a friendly message, or just let it throw). In
   `AwsLambdaSpecClient`, throw a clear exception instead of returning null:
   `"Lambda '{name}' did not answer the spec topic — is UseSpec() registered and the function name/profile correct?"`.
   Unknown command / bad args in `CommandRouter`/parser → print help, return non-zero.
2. **`DiffCommand`** (`Commands/Diff/DiffCommand.cs` + `DiffPayload.cs`), registered in
   `ConsoleApplication`:
   - Args (add consts to `Constants.cs`): `--baseline <file>` (required), `--current <file>`
     (required), `--fail-on breaking|warning|none` (default `breaking`), `--warn-only`
     (equivalent to `--fail-on none`; keep both for ergonomics), `--format text|json`
     (default `text`).
   - Implementation: read both files;
     `SchemaCompatibility.Compare(...)` / the string-JSON overload of
     `EnsureBackwardCompatible(baselineJson, currentJson)` in
     `src/Benzene.Schema.OpenApi/Compatibility/SchemaCompatibility.cs` already exists — prefer
     calling `new SchemaCompatibilityComparer().Compare(baseline, current)` on deserialized
     documents and formatting the `SchemaCompatibilityReport` yourself (one line per
     `SchemaChange`: compatibility, kind, what/where; then a summary count). `--format json`
     serializes the report.
   - Exit: non-zero iff the report trips the `--fail-on` threshold
     (`report.HasBreakingChanges` / `HasWarnings`).
3. **Project reference:** `Benzene.CodeGen.Cli.Core.csproj` must reference
   `Benzene.Schema.OpenApi` (check — it likely already does transitively via CodeGen.Client;
   make it explicit).
4. **Tests** (`test/Benzene.Core.Test/Autogen/CodeGen/Cli/DiffCommandTest.cs`): reuse the fixture
   specs the compatibility tests use
   (`test/Benzene.Core.Test/Autogen/Schema/OpenApi/Compatibility/`); assert breaking → throws (or
   non-zero result surface), additive → success, `--warn-only` → success with breaking changes,
   json format parses.
5. **Docs:** extend the Phase 4 CLI reference page (if doing this phase first, leave a TODO
   marker the Phase 4 agent will fill; do not ship undocumented flags silently).

**Acceptance:** `benzene diff --baseline a.json --current b.json` exits 1 on a removed response
field, 0 on an added optional one; every existing CLI command exits non-zero on failure; tests green.

---

## Phase 3 — `--file` / `--url` / `--mesh` sources for `build` and `spec`

**Goal:** decouple client generation from deployed-AWS-Lambda + credentials: generate from a local
artifact (Phase 1's output), a service URL, or a mesh manifest.
**Depends on:** Phase 2 (exit codes; touches the same files). **Effort:** S.

Steps:
1. **Introduce a spec-source abstraction** in `Benzene.CodeGen.Cli.Core`
   (`Commands/Spec/ISpecSource.cs`): `Task<string> GetSpecJsonAsync(SpecRequest request)`.
   Implementations:
   - `AwsLambdaSpecSource` (wrap the existing `AwsLambdaSpecClient`),
   - `FileSpecSource` (`File.ReadAllTextAsync`; `Constants.File` + its description already exist),
   - `HttpSpecSource` (GET `{url}/benzene/spec?type=benzene&format=json` — mirror how
     `profile-check`'s prober calls the spec endpoint; reuse its HTTP plumbing from
     `Benzene.CloudService.Probe` if referenced, else a plain `HttpClient`),
   - `MeshSpecSource` (`--mesh <manifest-url> --service <name>`): fetch `manifest.json`, find the
     service entry, fetch its `services/{name}.json` (`MeshServiceSnapshot` shape from
     `Benzene.Mesh.Contracts`) and return its `specJson`. Resolve relative to the manifest URL
     exactly as the Mesh UI does (relative-path artifacts).
2. **Wire into payloads:** add `File`, `Url`, `Mesh`, `Service` args to `BuildPayload` and
   `SpecPayload` (+ `Constants` entries: `mesh`, `service`, reuse `url`/`file`). Source selection:
   exactly one of `--file`/`--url`/`--mesh`/`--lambda-name` must be given; anything else → helpful
   error, non-zero.
3. **Naming without a Lambda:** `CodeBuilderFactory`/`LambdaNameParser` derive service name and
   namespace from `LambdaName`. Add `--service-name` (defaults: `--mesh` → the mesh service name;
   `--file` → the document's own service/title field if present, else the file stem; `--url` → host
   segment) and route naming through it. Keep `ICommandPayload` compiling — extend it if needed.
4. **Optional `--topic <id>` scoping** on `build`: filter the deserialized document's operations to
   the topic before invoking the builder (the `AtomicClientSdkBuilder` path already builds
   per-topic; whole-document filtering keeps `client` mode consistent too).
5. **Tests** (`.../Cli/BuildSourcesTest.cs`): `FileSpecSource` + `build --output topic-client`
   against a fixture spec generates compilable-looking files (assert expected file names +
   contains the topic method); `MeshSpecSource` against a stubbed `HttpMessageHandler` serving a
   two-file mesh store; mutually-exclusive-source validation errors.
6. **Docs:** TODO marker for Phase 4 (or update the pages if Phase 4 already ran).

**Acceptance:** `benzene build --file Orders.spec.json --output topic-client --service-name Orders
--directory Generated/` works offline with no AWS SDK calls; `benzene spec --url https://…` prints
the spec; `--mesh` resolves via a manifest; tests green.

*(AMENDED 2026-08-12: step 4's singular `--topic <id>` is superseded by Phase 3b's `--topics`
include-list — implement the list form directly, not the singular first.)*

---

## Phase 3b — Client generation configuration *(added 2026-08-12, owner requirements)*

**Goal:** the two client shapes become first-class, configurable options, and a consumer can
generate clients for **only the topics they use** — deliberately minimising the coupling surface
between services. Configuration covers: mode (service-client vs per-topic), the generated
namespace, and a topic include-list.
**Depends on:** Phase 3 (same files: `BuildPayload`, `CodeBuilderFactory`; and `--file` makes any
of this worth using). **Effort:** S–M.

**What exists / what's absent (audited 2026-08-12):** both modes already exist —
`MessageClientSdkBuilder` (service class, one method per topic) and `AtomicClientSdkBuilder`
(self-contained client per topic, per-topic `RequiredTopics` + contract hash) — selected by
`--output client|topic-client`, with a `default:` switch case that silently produces the service
client on a typo. All builders take a `baseNamespace` ctor arg but hard-append `.{ServiceName}`
in three places (`MessageClientSdkBuilder.cs` lines 19/63/178); the CLI derives the namespace from
`--lambda-name` plus a hardcoded `"Client"`/`"Service"` literal, with no override. No topic
filtering exists anywhere except atomic mode's reserved-topic `bool`. No options class exists —
everything is positional ctor args.

Steps:

1. **`ClientSdkOptions`** (new, in `Benzene.CodeGen.Client`): `ServiceName`, `Namespace` (the FULL
   generated namespace — no magic suffix), `Topics` (include-list; null/empty = all),
   `IncludeReservedTopics` (default false). Builders gain an options ctor; existing ctors delegate
   to it with today's values so current call sites and golden-file tests stay green.
   `MessageHandlerBuilder` already treats its namespace as flat — the options object makes the two
   client builders match it, ending the three-way ambiguity about what "baseNamespace" means.
2. **Namespace semantics:** when `Namespace` is supplied it is used *exactly* — client, interface
   and DTOs all in one namespace, one source of truth (collapse the three hardcoded concat sites
   into one). Atomic mode appends `.{ClientName}` per client below the supplied root (its
   duplicated-DTO self-containment requires per-client namespaces). When absent, today's
   derivation stays, unchanged — this is additive.
3. **Topic include-list — filter the document, not the builders.** Apply `Topics` as ONE upstream
   projection of `EventServiceDocument.Requests` before any builder runs (Phase 3 step 4's
   whole-document approach), so the three per-builder iteration sites (methods, interface,
   `RequiredTopics`) can never disagree with each other. Rules, all fail-loud per house style:
   - A topic named in the list that is not in the document → non-zero exit listing the document's
     actual topics (a typo'd include-list that silently generates nothing is the worst outcome).
   - `benzene:healthcheck` is exempt: `HealthCheckAsync()` and its `RequiredTopics` entry are
     always emitted and never need naming in the list. Document this.
   - Reserved topics: excluded by default in BOTH modes (today service-client mode generates
     methods and `RequiredTopics` entries for `benzene:spec`/`benzene:mesh` etc. — treat that as
     the bug it is; `IncludeReservedTopics` opts back in). Flag as a deliberate output change in
     the commit.
4. **Mode becomes validated:** explicit `case "client":`; unknown `--output` → non-zero exit
   listing valid values (`client`, `topic-client`, `message-handlers`, `readme` — decide
   `api-gateway`'s fate per the 2026-08-12 Amendment B freeze before documenting it). No silent
   default.
5. **CLI wiring:** `--namespace` and `--topics <a,b,c>` (comma-delimited — `PayloadMapper` is
   string-only) on `BuildPayload` + `Constants` entries; thread through `CodeBuilderFactory` into
   `ClientSdkOptions`. `--service-name` arrives in Phase 3 step 3 and feeds `ServiceName`.
6. **Fix `CodeFileWriter`** to create subdirectories (`Path.GetDirectoryName` →
   `Directory.CreateDirectory` per file). Today it throws `DirectoryNotFoundException` on atomic
   mode's `{Client}/{File}.cs` names — `--output topic-client` cannot complete a write today, and
   Phase 6's `%(Mode)` default of `topic-client` lands on exactly this path. Test with nested
   names.
7. **Tests** (atomic style — config-permutation asserts on the filename→source dictionary, not new
   golden files per permutation): include-list scopes methods AND interface AND `RequiredTopics`
   together in `client` mode; include-list scopes which per-topic clients exist in `topic-client`
   mode; unknown topic in the list → error naming valid topics; explicit `Namespace` lands
   identically on client/interface/DTOs; reserved excluded by default in `client` mode /
   `IncludeReservedTopics` restores them; healthcheck exemption; `CodeFileWriter` nested paths;
   `CodeBuilderFactory` rejects unknown output values (first tests this factory has ever had).
8. **Docs:** extend `docs/client-sdks.md` with the two shapes side by side (when to pick which —
   per-topic clients scope `RequiredTopics` and the contract hash to one topic, so unrelated
   changes on the producing service neither drag in unused surface nor invalidate the client), the
   coupling-surface rationale for `--topics`, and both new flags. Document `topic-client` and
   `message-handlers` outputs at all (currently only the `client` mode is documented).

**Explicitly out of scope (recorded so they aren't invented mid-implementation):** generating DI
registration extensions (`services.AddUserServiceClient(...)`) — a real gap once per-topic clients
multiply interfaces, but its shape (which lifetime? which container abstraction?) deserves its own
decision; generating from `document.Events` (no client builder reads events today); CLI selection
of `IMethodName` naming strategies; any change to the generated method bodies or
`IBenzeneMessageSender` (transport injection stays exactly as-is: the generated code's only
dependency is `IBenzeneMessageSender`, and `AddOutboundRouting` binds topics to transports at the
consumer, out-of-band — verified, zero transport references in generated output).

**Acceptance:** `benzene build --file Orders.spec.json --output topic-client --namespace
Acme.Orders.Clients --topics "order:create,order:cancel" --directory Generated/` emits exactly two
self-contained clients in the requested namespace root, whose `RequiredTopics` cover only their own
topic plus healthcheck; the same `--topics` list on `--output client` emits one service client
whose methods, interface and `RequiredTopics` cover exactly those topics; a typo'd topic or mode
fails non-zero naming the valid values; all existing golden-file tests pass unmodified.

---

## Phase 4 — Docs pass (all DOC, code-free except snippets)

**Goal:** the tooling that exists is discoverable. **Depends on:** none to start; refresh after
Phases 1–3 land flags. **Effort:** S.

Deliverables (all in benzene-dotnet `docs/`):
1. **`docs/cli.md` — the `benzene` CLI reference.** Install
   (`dotnet tool install --global Benzene.CodeGen.Cli` — verify the actual package id from
   `src/Benzene.CodeGen.Cli/Benzene.CodeGen.Cli.csproj` before writing), every command with args
   table + one real invocation each: `build`, `spec`, `healthcheck`, `lambda-test-tool`,
   `profile-check`, and (post-Phase 2/3/5) `diff`, `compat-check`, `impact`, and the new sources.
   Link from `docs/index.md` under **Code Generation**.
2. **`docs/client-sdks.md`** — add a **topic-client** section: what `AtomicClientSdkBuilder` emits
   (one self-contained client per topic, topic-scoped hash), when to prefer it over the
   whole-service client, CLI invocation. Verify claims against
   `src/Benzene.CodeGen.Client/AtomicClientSdkBuilder.cs` first.
3. **`docs/client-sdks.md`** (or a short new cookbook) — **consumer-first scaffolding** with
   `MessageHandlerBuilder` (`--output message-handlers`): generate handler stubs from a published
   contract.
4. **Rewrite `docs/cookbooks/contract-testing.md` step 1** around the shipped tooling: baseline =
   Phase 1's `spec.json` artifact (exact command), gate = `benzene diff` in CI (exact YAML step) or
   the in-test `SchemaCompatibility.EnsureBackwardCompatible` for .NET-only shops. Keep the
   runtime-drift mechanism section as-is. Name the two-hash distinction explicitly
   (`CodeGenHelpers.GenerateHash`/`MeshHashing` over spec JSON vs `MeshDescriptorHashing` over the
   canonical descriptor) — structural comparer for gates, hashes for cheap change detection only.
5. **Cross-links:** `docs/contract-artifacts.md` (Phase 1) ↔ `cli.md` ↔ `contract-testing.md` ↔
   `client-sdks.md`. Every new page reachable from `docs/index.md`.

**Acceptance:** every command and flag that ships is documented; no dead links (the Benzene repo's
website build is the checker — if you have that repo, run its generator with `--dotnet-docs`
pointing at this docs tree and confirm 0 warnings).

---

## Phase 5 — `benzene compat-check` + `benzene impact` (mesh-joined)

**Goal:** the adoption differentiator: "this change breaks consumers X and Y" and "who consumes
this topic / is v1 safe to retire" as single commands.
**Depends on:** Phases 1–3 (artifact, exit codes, mesh source plumbing). **Effort:** M.

Shared plumbing (build once, in `Benzene.CodeGen.Cli.Core`): a small `MeshArtifactClient` that,
given a manifest URL, fetches and deserializes `manifest.json` (`MeshManifest`),
`services/{name}.json` (`MeshServiceSnapshot`), `topics.json` (`MeshTopicCatalog`), `usage.json`
(`MeshUsage`), `topology.json` (`MeshTopology`) — all shapes from `Benzene.Mesh.Contracts`
(add the `ProjectReference`). Resolve every path relative to the manifest URL; tolerate absent
optional artifacts (usage/topology) and *say so* in output rather than failing.

1. **`compat-check`** (`Commands/CompatCheck/…`):
   - Args: `--spec <candidate.spec.json>` (required), `--mesh <manifest-url>` (required),
     `--service <name>` (default: the candidate document's service name), `--fail-on` /
     `--warn-only` / `--format` as in `diff`.
   - Flow: baseline = mesh snapshot's `specJson`; run the comparer; for each breaking change,
     resolve the affected topic in `topics.json` and print declared consumers per version
     (`MeshTopicEntry` consumers), plus usage counts when `usage.json` exists (label the window
     honestly — copy the wording rules the Mesh UI uses; see
     [`service-mesh-roadmap-1.0.md`](../service-mesh-roadmap-1.0.md) on count honesty).
   - Output caveats (print, always): baseline freshness (`snapshotAtUtc` — warn if older than
     7 days); "declared consumers — an upcaster may bridge this change; confirm before shipping";
     for a non-.NET producer the comparer ran on round-tripped JSON (degrade wording).
2. **`impact`** (`Commands/Impact/…`):
   - Args: `--topic <id>` (required; accept `id@version`), `--mesh <manifest-url>` (required),
     `--fleet <collector-envelope-url>` (optional), `--fail-on-consumers` (exit non-zero if any
     consumer is found — for retirement gates), `--format`.
   - Flow: from `topics.json`: declared consumers/producers per version + version-compatibility
     flags (`MeshTopicVersionCompatibility`: produced-not-consumed / consumed-not-produced); from
     `usage.json`: per-version counts with window/source labels; from `manifest.json`:
     `owningTeam` per involved service; from annotations artifact if present: open threads on the
     topic. With `--fleet`, query the collector's `mesh:query:topic` for observed consumers and
     windowed counts (read `Benzene.Mesh.Collector`'s query read-model for the request shape) —
     tag those rows `observed`.
3. **Tests** (`.../Cli/CompatCheckCommandTest.cs`, `ImpactCommandTest.cs`): stub the mesh store
   with a `HttpMessageHandler` fake serving fixture JSON (build fixtures by hand from the
   `Benzene.Mesh.Contracts` types — or reuse the website demo fixtures' shapes); cases: breaking
   change names its consumers; missing usage.json degrades with a notice; `--fail-on-consumers`
   exits non-zero when consumers exist and zero when none; stale `snapshotAtUtc` warns.
4. **Docs:** add both commands to `docs/cli.md`; add a "gating retirement and breaking changes"
   section to `docs/cookbooks/contract-testing.md` with CI YAML for both.

**Acceptance:** demoable end-to-end against a local static mesh store (a directory of fixture
JSON served by any static server): a spec with a removed field names its consumers and exits 1;
`impact` on a consumed topic lists consumers/usage and honors `--fail-on-consumers`; tests green.

---

## Phase 6 — MSBuild one-liner client generation

**Goal:** `<BenzeneServiceContract Include="contracts/orders.spec.json" Mode="topic-client"/>` →
typed clients compiled into the project, regenerated incrementally.
**Depends on:** Phase 3 (`--file` source). **Effort:** M.

Steps:
1. New NuGet **`Benzene.CodeGen.Build`** (`src/Benzene.CodeGen.Build/`): no assembly needed — a
   *targets-only* package (`<IncludeBuildOutput>false</IncludeBuildOutput>`) carrying
   `build/Benzene.CodeGen.Build.targets` (+ `.props` declaring the item group). It shells out to
   the `benzene` tool (same invocation-resolution pattern as Phase 1's targets:
   `$(BenzeneCliCommand)` overridable, default `benzene`, document the local-tool-manifest setup).
2. **Targets logic:**
   - Item: `<BenzeneServiceContract Include="..." Mode="client|topic-client" ServiceName="..."
     Namespace="..." Topics="order:create,order:cancel"/>`
     (`Mode` default `topic-client`; `ServiceName` default = file stem; `Namespace`/`Topics`
     optional, passed through as `--namespace`/`--topics` — added 2026-08-12 with Phase 3b, whose
     CLI flags this metadata maps onto 1:1. `Mode` values must stay in sync with
     `CodeBuilderFactory`'s validated switch).
   - Target `BenzeneGenerateClients` running **before `CoreCompile`** (`BeforeTargets="CoreCompile"`),
     per item: `benzene build --file %(Identity) --output %(Mode) --service-name %(ServiceName)
     --directory $(IntermediateOutputPath)benzene/%(ServiceName)/`.
   - Add outputs to compilation: `<Compile Include="$(IntermediateOutputPath)benzene/**/*.cs"/>`
     inside the target (after generation), and declare `Inputs`/`Outputs` on the target
     (input = the contract files; output = a stamp file per contract written after generation) so
     incremental builds skip regeneration when contracts are unchanged.
   - Fail the build on tool failure (no `ContinueOnError`) — Phase 2's exit codes make this real.
3. **Sample + test:** wire one example (e.g. a new tiny `examples/CodeGen/Contracts.Consumer`
   project referencing a committed fixture spec) proving the end-to-end compile. An MSBuild
   integration test in the unit suite is impractical — the example project in
   `Benzene.Examples.sln` *is* the test; build it in CI.
4. **Docs:** a "one-line integration" section in `docs/client-sdks.md` + `docs/cli.md` cross-link;
   README in the package.

**Acceptance:** the example consumer project compiles with IntelliSense-visible generated clients;
touching the contract file regenerates; a second build with no changes skips generation
(verify with `dotnet build -v n` log); a broken contract file fails the build with the CLI's error.

---

## 2026-08-13 — owner decision: dogfood both tools in this repo's own build

Both `Benzene.Descriptor` (`benzene-descriptor`) and `Benzene.CodeGen.Cli` (`benzene`) are already
`PackAsTool` and already in `Benzene.sln`, so both already get packed and pushed to NuGet by
`deploy-benzene.yml` on the next release run — no new packaging work needed there. What's missing is
that nothing in this repo actually *exercises* either tool: no example imports
`Benzene.Descriptor.targets`, and Phase 6 (client generation via MSBuild) was never dispatched. The
owner approved closing both gaps now, as two sequential slices sharing this repo's established
worktree → personal-verify → merge discipline:

### Phase 6a — Dogfood `benzene-descriptor` on an existing example

**Goal:** `examples/AwsMesh/Payments` opts into `BenzeneEmitDescriptor` and its
`.spec.json`/`.service.json` get produced on every build, proving the Phase 1 tool and its MSBuild
target work outside a hand-run smoke test. **Depends on:** nothing (Phase 1 already merged).
**Effort:** S.

Constraint specific to this repo's CI topology: `build-benzene.yml`'s `examples-build` job (which
builds `Benzene.Examples.sln`) is a separate GitHub Actions job from the one that builds `Benzene.sln`
(where `Benzene.Descriptor` lives) — no build artifacts are shared between jobs by default, so
`benzene-descriptor` cannot be assumed to already be on `PATH` as a built tool when `Payments` builds.
Don't solve this by wiring cross-job artifact upload/download; simpler and self-contained is pointing
`BenzeneDescriptorCommand` at `dotnet run --project <path-to-Benzene.Descriptor.csproj> -c
$(Configuration) --`, which builds-then-runs the tool on demand from source, same-repo, no packaging
or PATH dependency required. Verify locally that a clean `dotnet build` of the Payments example alone
(not the whole solution) still produces both JSON files.

### Phase 6b — Implement Phase 6 (MSBuild one-liner client generation) as originally planned

Do Phase 6 above exactly as written, with one addition to its step 3: the new consumer example
project must be **wired into `Benzene.Examples.sln`** so `build-benzene.yml`'s existing
`examples-build` job (`dotnet build Benzene.Examples.sln`) compiles it on every push/PR without any
new CI workflow changes — that job is deliberately the regression gate already described in that
workflow's own comment ("a library change could break the copy-paste example surface... without CI
noticing"). Note `examples/CodeGen/Benzene.Examples.CodeGen.Client` already exists but is a unit-test-style
demonstration of calling `MessageClientSdkBuilder` in-process, not an MSBuild-driven generate-then-
compile project — it does not satisfy Phase 6's acceptance criterion and should be left alone (not
repurposed, not deleted) unless it's clearly in the way. **Depends on:** Phase 6a is not a strict
prerequisite (different files) but should land first per this repo's one-slice-at-a-time discipline.

---

## 2026-08-13 — dogfooding findings: orders-api on a generated payments client

The first real consumer of the generated-client tooling is `examples/AwsMesh/Orders`, which used to
hand-write an `OutboundPaymentCapture` DTO mirroring payments-api's `CapturePayment` and call
`_sender.SendAsync<…>("payments:capture", …)` with the topic id typed out by hand. It now generates a
per-topic client from payments-api's committed contract, using the **published** `benzene` CLI and
`Benzene.CodeGen.Build` from NuGet (`0.0.2-alpha.6`), not from source. Build-time generation works;
these are the things adoption surfaced, in priority order. *(The CLI half is temporarily back on the
from-source form: `0.0.2-alpha.6` predates the 7a fix and would silently regenerate the very defect
7a records. Restore `<BenzeneCliCommand>dotnet tool run benzene</BenzeneCliCommand>` in the Orders
csproj once a CLI carrying the fix is published and pinned.)*

### 7a. `benzene:healthcheck` should not be an unconditional requirement *(RESOLVED)*

**Resolved 2026-08-13:** generated clients no longer emit a health check at all — `benzene:*` reserved
endpoints are excluded from generation like any other reserved topic, so nothing appears in
`RequiredTopics` that the consumer did not ask for, and the example's `OutboundSend.HealthCheck(...)`
workaround is gone.

`MessageClientSdkBuilder` always emits `HealthCheckAsync()` and always puts `benzene:healthcheck` in
the client's `RequiredTopics`. `AddOutboundRouting` now auto-registers `OutboundRoutingStartUpCheck`,
and start-up checks default to `Enforce` — so **the moment a service adopts any generated client the
host refuses to start**, until it registers an outbound route for `benzene:healthcheck`:

```
outbound-routing: The following topics are required by a generated client but have no
registered outbound route: benzene:healthcheck. Register each via OutboundRoutingBuilder.Route.
```

That is a hard failure for a topic the consumer often cannot meaningfully call: orders → payments is
fire-and-forget SQS, which could never answer a health probe. Worked around in the example by routing
it over the same transport while excluding it from the spec's `events` (`OutboundSend.HealthCheck(...)`,
so the mesh graph doesn't gain a fake orders → payments health edge). Real fix: make the health check
opt-in (a `-health-check` flag / `ClientSdkOptions.IncludeHealthCheck`, default off), or keep emitting
the method but leave `benzene:healthcheck` out of `RequiredTopics` — it is framework plumbing, not part
of the consumer's declared contract surface. *Ruling taken: neither half is generated. Benzene's
reserved endpoints are deliberately separate from domain endpoints, and generated clients are for
domain concerns only. Whether to offer an opt-in flag later is a separate, still-open product
question.*

### 7b. Generated code should declare the packages it needs *(RESOLVED)*

**Resolved 2026-08-13:** dropping the emitted health check removed the only dependency a consumer did
not already have — the output now needs just `Benzene.Clients`/`Benzene.Abstractions`, which any
consumer of `IBenzeneMessageSender` references anyway.

The output references `IHasHealthCheck`/`ClientHealthCheckProcessor` from `Benzene.Clients.HealthChecks`
and `IBenzeneMessageSender` from `Benzene.Clients`, but nothing tells the consuming project that — you
find out from `CS0234`/`CS0246` after the first generation. Either have `Benzene.CodeGen.Build` carry
those as package dependencies (so a single `PackageReference` is genuinely sufficient), or have
`benzene build` report the required package set. The former is what the one-liner promises.

### 7c. Generate the DI registration *(RESOLVED)*

**Resolved 2026-08-13:** both client generators now emit a registration extension beside each client.

**Ruling taken — register against `IBenzeneServiceContainer`, not `IServiceCollection`.** A user may
be on Autofac or another container; if Benzene is the thing handling DI, an extension on Microsoft's
`IServiceCollection` would be useless to them. Benzene's own container abstraction is the one seam
every host has, whatever container sits underneath, and `Benzene.Abstractions` is already referenced
by generated code (`IBenzeneResult`), so this added **no new package dependency** to the output.

What ships:

- `client` mode (`MessageClientSdkBuilder`): a `{Service}ServiceClientRegistration.cs` with
  `Add{Service}ServiceClient(this IBenzeneServiceContainer)`.
- `topic-client` mode (`AtomicClientSdkBuilder`): **both** shapes — each per-topic client folder
  carries its own `Add{Client}ServiceClient()` (a self-contained atomic client stays droppable-in on
  its own), plus one root `{Service}ClientsRegistration.cs` whose `Add{Service}Clients()` calls each
  of them, for a consumer taking several topics off one service. The aggregate is named from
  `--service-name` (now passed through by `CodeBuilderFactory` for this mode) and is skipped when
  there is no service name, or no clients.
- Lifetime is **`AddScoped`**, with the reason emitted as a comment in the generated file:
  `AddOutboundRouting` registers `IBenzeneMessageSender` scoped, so a singleton client would be a
  captive dependency. (Phase 3b's open question "which lifetime?" is answered: match
  `IBenzeneMessageSender`.)
- `examples/AwsMesh/Orders` now dogfoods it — `services.UsingBenzene(x => x.AddPaymentsClients())`
  replaces the hand-written `AddScoped<IPaymentsCaptureServiceClient, …>()`.

### 7d. `decimal` does not survive the contract round trip *(PARKED — known limitation)*

payments-api's `CapturePayment.Amount` is `decimal`; the spec records `"type": "number"` with no
`format`, and `OpenApiSchemaCSharpTypeBuilder` maps that to `double`, so the generated DTO is `double`
and the call site casts. For money that is a lossy representation.

**Ruling taken 2026-08-13 — parked, not being fixed now.** *"Keep it to whatever is in the schema
definition, because that's what we're governed by."* The schema is the governing contract: codegen
maps what the schema says, and the schema says `number`. Some data types are simply known not to
travel well in JSON — a well-known problem in finance, not one Benzene invented and not one it can
solve unilaterally on the consumer side by second-guessing a contract it was handed. Not worth
fighting now.

**Recorded as a known limitation:** a `decimal` on a producer round-trips to `double` on a generated
consumer client, and consumers of money-carrying contracts should be aware of it.

**Its own future issue,** if and when it is picked up: emit `format` on the producer side (so the
schema carries the precision intent rather than losing it), *then* honour that `format` in
`OpenApiSchemaCSharpTypeBuilder`. Both halves, in that order — honouring a `format` no producer emits
would change nothing, and the schema stays the thing that governs.

### 7e. Nothing detects contract drift *(PARKED — future requirement)*

`Orders/contracts/payments.spec.json` is a committed copy. If payments-api changes its contract nothing
tells orders-api — the copy silently goes stale, which is the same failure mode the hand-written mirror
DTO had.

**Ruling taken 2026-08-13 — good idea, parked. Not being tackled now**; it should be built later as a
proper feature rather than bolted on here.

**Note for whoever builds it: the mesh already has this capability.** The mesh aggregator already
diffs a service's contract run over run (`MeshTopicEntry.Changes[]`/`RemovedTopics`, see Phase 7c's
`changelog.json` item) and the mesh UI already renders a drift badge; the runtime consumer-side check
(`Benzene.Clients.HealthChecks`' `AddServiceCheck` + `ClientHealthCheckProcessor`) compares a
consumer's expected hash against a provider's live one. A future implementation should **build on
that existing mechanism** — the mesh's diff and the published contract hash — rather than inventing a
parallel one. `benzene diff` (Phase 2) wired into CI against each producer's freshly-built spec is the
build-time half of the same idea.

---

## Phase 7 — Mesh data foundations

Three independent sub-items; each can be its own task. **Effort:** S each.

### 7a. `version` tag on `benzene.messages.processed` (approved)
- Emission site: call sites of `BenzeneDiagnostics.MessagesProcessed`
  (`src/Benzene.Diagnostics/BenzeneDiagnostics.cs` defines the counter; grep for
  `MessagesProcessed.Add` to find the middleware that tags `topic`/`transport`/`result`). Add a
  `version` tag sourced from the same place the pipeline knows the message version (the
  transport-agnostic context — see how `MeshTraceEvent` gets `topicVersion`). Untagged when no
  version is signalled (absent tag, not empty string — match how other tags handle absence).
- Update the standard: `docs/mesh-usage-feed.md` §1 (tag table) + note bounded cardinality.
- Update both backend adapters to *read* it: `Benzene.Mesh.Usage.CloudWatch` and
  `Benzene.Mesh.Usage.ApplicationInsights` map the new dimension into `MeshUsageEntry.Version`
  (nullable — absent stays null). Follow each adapter's existing dimension-mapping code.
- Tests: extend the existing usage-source tests (find them via
  `grep -rl "MeshUsageEntry" test/`) with a version-tagged sample.

### 7b. Collector→artifact observed-consumers bridge
- Mirror `src/Benzene.Mesh.Collector/CollectorUsageSource.cs` (which bridges collector counts into
  the aggregator's usage plane): add `CollectorTopologySource` implementing the aggregator's
  topology-source seam — read how `topology.json` edges are sourced today
  (`Benzene.Mesh.Aggregator`, `MeshTopology`/edge builders) and add observed consumer edges from
  the collector's trace-parentage read model, tagged `source: "collector"`.
- Honesty rule: never merge observed and declared edges into one count — they are separate rows
  with separate `source` labels (the UI already renders per-source).
- Tests: fixture collector store → aggregator run → `topology.json` contains the collector-sourced
  edges; absent collector → artifact unchanged.

### 7c. `changelog.json` (the time dimension)
- In `Benzene.Mesh.Aggregator`: after the existing run-over-run diff computes
  `MeshTopicEntry.Changes[]` / `RemovedTopics` (find the comparison in `MeshAggregator.cs`),
  append a dated entry `{ atUtc, changes: [...] }` to a rolling `changelog.json` via
  `IMeshArtifactStore` (`src/Benzene.Mesh.Aggregator/IMeshArtifactStore.cs`) — read-modify-write,
  **only when the diff is non-empty**, bounded (keep newest N=500 entries; prune by count, not
  date). Reuse the annotations artifact's corrupt-file parking discipline (find how
  `MeshAnnotationLog` handles unreadable files) — this artifact, like annotations, cannot be
  regenerated.
- Shape: define `MeshChangelog`/`MeshChangelogEntry` in `Benzene.Mesh.Contracts` (additive; the UI
  and spec's mesh.md treat unknown artifacts as optional — **no spec change needed**, but flag in
  the PR that the spec repo's mesh.md artifact table may want an optional row later; do NOT edit
  the spec in this phase).
- Tests: two aggregator runs with a changed topic → one changelog entry with that change; run with
  no changes → no new entry; corrupt existing file → parked + fresh start, run does not throw.
- The UI "Changes" view is **Phase 8** — do not touch the UI here.

---

## Phase 8 — Mesh UI "Consume this topic" + "Changes" view *(cross-repo)*

**Repo:** the cross-language **Benzene** repo (canonical UI: `mesh-ui/mesh-ui.html`), guide:
`docs/guides/mesh-ui.md`. **Depends on:** Phases 3 (CLI line to print), 7c (changelog artifact).
**Effort:** S–M.

Non-negotiable mechanics (from the guide): there is exactly **one** Mesh UI; the website demo
(`website/demos/mesh/index.html`) and any port copies are **byte-identical vendored copies** of
`mesh-ui/mesh-ui.html`. Any change = edit the canonical file, then re-vendor every copy verbatim,
and keep `docs/guides/mesh-ui.md` (the conformance contract) in sync.

1. **"Consume this topic" panel** on the topic drill-in: buttons to (a) copy/download the topic's
   inlined schema JSON (already in memory for rendering), (b) copy the owning service's `specUrl`,
   (c) copy a ready-made CLI line
   `benzene build --mesh <manifest-url> --service <name> --topic <id> --output topic-client`
   (manifest URL = the one the page loaded), (d) the existing AsyncAPI/Studio links. Pure
   links/clipboard — no fetches, static-floor-safe. Feature-detect nothing new (all inputs are
   already-loaded artifacts).
2. **"Changes" view**: feature-detect `changelog.json` (absent → the view says the artifact is
   missing, per the guide's honest-degradation rule); render dated plain-language change lines
   with a since-picker (7d default).
3. **Guide update:** add both to `docs/guides/mesh-ui.md` — the functional-requirements list and
   the artifact table (`changelog.json` as optional), matching its existing style. Add a
   `changelog.json` fixture to `website/demos/mesh/` so the demo exercises the view.
4. **Re-vendor**: copy the canonical file over `website/demos/mesh/index.html` byte-identically;
   check the TS/other ports' vendored copies exist and update them in their repos if this session
   has them (benzene-typescript vendors it — check `grep -rl "mesh-ui" /home/user/benzene-typescript`).
5. Build the website generator (Benzene repo: `dotnet run --project website/generator -- --out
   website/dist --dotnet-docs <benzene-dotnet>/docs`) — 0 broken-link warnings.

**Acceptance:** demo page shows the panel and the Changes view against the fixtures; canonical and
vendored copies byte-identical (`diff` them); guide updated; website build clean.

---

## Suggested agent task slicing

| Task | Phases | Parallel-safe with |
|---|---|---|
| T1 | Phase 1 | T2 |
| T2 | Phase 2 | T1 |
| T3 | Phase 3 | T4 after T2 merges |
| T3b | Phase 3b | after T3 (same files: `BuildPayload`, `CodeBuilderFactory`) |
| T4 | Phase 4 | anything (refresh at the end) |
| T5 | Phase 5 | T6 |
| T6 | Phase 6 | T5, after T3b (its `%(Mode)`/`Namespace`/`Topics` metadata maps onto 3b's flags) |
| T7 | 7a, 7b, 7c (three sub-tasks) | each other, and T5/T6 |
| T8 | Phase 8 | last (needs 3 + 7c shipped) |

Each task: read §0 + its phase + the assessment section it implements; verify cited files; build +
test; commit with a conventional message; report what was verified vs assumed.
