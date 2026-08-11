# Slice 0 — Make the shipped adapters composable, and the host testable

**Status:** ready to build. **This is the first pickup.**
**Depends on:** nothing.
**Branch:** `claude/mesh-enterprise-slice-0`
**Shape:** three independent tasks, ~10 files touched, one new `src/` package, one new test project.

## Why

Slice 1 turns the mesh host into something that registers several adapters at once, chosen by
configuration. Three things stop that working today, and all three are cheaper to fix now than to
fight later:

1. Adapters fight over shared infrastructure registrations. Their own XML docs claim they don't.
2. Artifact serving only works from local disk, because the middleware that reads from an
   `IMeshArtifactStore` was copy-pasted into five examples rather than packaged.
3. The host has **zero unit tests**, so slice 1 would be adding a large config surface with nothing
   to catch it.

None of this changes behaviour a user can see. That is the point — it is the safe groundwork.

## Before you start

Read [`README.md`](README.md) in this folder first; its house rules apply to every task.

```bash
dotnet build Benzene.sln
dotnet test  test/Benzene.Mesh.Test/Benzene.Mesh.Test.csproj
dotnet build deploy/Mesh/Benzene.Mesh.Host.sln
```

All three must be green before you start. Note the test-project filename trap elsewhere in this repo:
`test/Benzene.Core.Test/` contains a csproj named **`Benzene.Test.csproj`**, not
`Benzene.Core.Test.csproj`.

---

## Task 0.1 — Make shared client registrations `TryAdd`

**The bug:** these adapters' XML documentation says they register a client *"unless one is already
registered"*. The code uses plain `AddSingleton`, which always registers. So the documented
behaviour is false, and two adapters in one container fight — for example `Benzene.Mesh.Fleet.Tempo`
and `Benzene.Mesh.Tracing.Tempo` each register their own `HttpClient`.

`TryAddSingleton` already exists in `src/Benzene.Abstractions/DI/BenzeneServiceContainerExtensions.cs`
and is already used correctly by `Benzene.Mesh.Dispatch` and by `AddMeshLambdaDispatcher` — so this
task is making eight places match a pattern the repo already has, not inventing one.

**Change `AddSingleton` → `TryAddSingleton` in exactly these places:**

| File | Registration |
|---|---|
| `src/Benzene.Mesh.Aggregator/Extensions.cs` | `services.AddSingleton<HttpClient>();` |
| `src/Benzene.Mesh.Aws.S3/Extensions.cs` | `IAmazonS3` |
| `src/Benzene.Mesh.Fleet.Aws.XRay/Extensions.cs` | `IAmazonXRay` |
| `src/Benzene.Mesh.Fleet.Tempo/Extensions.cs` | `HttpClient` |
| `src/Benzene.Mesh.Fleet.Jaeger/Extensions.cs` | `HttpClient` |
| `src/Benzene.Mesh.Usage.CloudWatch/Extensions.cs` | `IAmazonCloudWatch` |
| `src/Benzene.Mesh.Usage.ApplicationInsights/Extensions.cs` | `LogsQueryClient` |
| `src/Benzene.Mesh.Tracing.Tempo/Extensions.cs` | `HttpClient` |
| `src/Benzene.Mesh.Aws.Lambda/Extensions.cs` | `IAmazonLambda` and `IAwsLambdaClient` — **in `AddMeshLambdaSource` only**; `AddMeshLambdaDispatcher` already does this correctly |

**Do NOT change these — they are feature registrations, not infrastructure, and `TryAdd` would break them:**

- `AddSingleton<IMeshServiceSource>(...)` and `AddSingleton<IMeshUsageSource, ...>()` — both are
  resolved as `IEnumerable<>` and are deliberately **additive**. `TryAdd` would silently drop the
  second usage source, which is exactly the capability slice 1 depends on.
- `AddSingleton<IMeshTraceSource, ...>()` and `AddSingleton<IMeshFleetReadModel, ...>()`.
- `AddSingleton(options)` — the options instances.
- `AddSingleton<MeshAggregator>()`, `PrometheusQueryClient`, `TempoServiceGraphTopologyBuilder`.

If you cannot tell whether a registration is infrastructure or a feature, the test is: *would a
second adapter legitimately want to add its own?* If yes it is a feature — leave it.

**Check the overload before you start.** `Benzene.Mesh.Dispatch` calls
`x.TryAddSingleton(_ => new HttpClient())` and `x.TryAddSingleton<IMeshDispatchEnvironment>(_ => ...)`.
If a no-factory `TryAddSingleton<T>()` overload does not exist, use the factory form rather than
adding an overload to `Benzene.Abstractions`.

**Verify:** add `test/Benzene.Mesh.Test/AdapterRegistrationTest.cs`. House style is xUnit with
`Assert`, hand-written fakes over Moq, class named `<Type>Test`, methods
`Scenario_ExpectedResult`; copy the shape of
`test/Benzene.Mesh.Test/MultipleAddMessageHandlersCompositionTest.cs`. Cover:

- A caller's own pre-registered `HttpClient` instance survives `AddTempoFleetReadModel` — resolve it
  and `Assert.Same` against the instance you registered. This is the documented promise, now true.
- `AddCloudWatchUsage` followed by `AddApplicationInsightsUsage` yields **two** `IMeshUsageSource`
  registrations. This is the regression guard for over-applying `TryAdd`.

```bash
dotnet test test/Benzene.Mesh.Test/Benzene.Mesh.Test.csproj
```

**Done when:** both tests pass and the full mesh suite is still green.

---

## Task 0.2 — Package the artifact-serving middleware

**The problem:** `MeshArtifactMiddleware.cs` exists five times, in `examples/AwsMesh/Mesh/`,
`AzureMesh`, `AzureFunctionsMesh`, `GoogleCloudMesh` and `K8sMesh`. They are near-identical — the two
131-line copies differ only in namespace and one doc-comment word; two are 118 lines. It serves
`manifest.json` / `services/*.json` out of an `IMeshArtifactStore`.

Slice 1 needs this. The host currently serves artifacts with ASP.NET `UseStaticFiles` over a
`PhysicalFileProvider`, which works **only for the filesystem store**. The moment config selects S3,
Blob or GCS, there is nothing serving the artifacts.

**Create a new package: `src/Benzene.Mesh.Artifacts/`.**

Put it there rather than in `Benzene.Mesh.Ui`, because `IMeshArtifactStore` lives in
`Benzene.Mesh.Aggregator`, and folding this into `Ui` would make the UI package depend on the
aggregator — breaking the "Contracts and Ui stay portable" discipline recorded in
`work/service-mesh-roadmap-1.0.md` §8. This decision is made; do not re-litigate it.

**Steps:**

1. Diff all five copies first. If they differ in anything beyond namespace, doc comments, and CORS
   defaults, **stop and report** — that means one of them carries behaviour the others lack, and
   collapsing them would silently change an example.
2. Write `MeshArtifactMiddleware<TContext> where TContext : IHttpContext` plus a
   `UseMeshArtifacts<TContext>(...)` extension. Match `MeshUiExtensions.UseMeshUi`'s shape exactly —
   `app.Register(x => x.AddSingleton(resolver => new ...(...)))` then
   `app.Use<TContext, ...>()`. That file is the template; read it before writing.
3. The csproj needs `<Description>` and `GenerateDocumentationFile=true` (repo convention for a
   packable project; `src/Directory.Build.props` handles the rest). **Do not set `Version` or
   `PackageVersion`** — `version.txt` is the single source.
4. Add the project to `Benzene.sln`. `AGENTS.md` says not to change solution structure without
   approval; adding a new `src/` package to the main solution is the expected exception and is
   approved here. Adding anything else is not.
5. Add a `CLAUDE.md` to the new package folder — every `src/` package has one, and `AGENTS.md`
   requires it be written in the same change.
6. Replace all five example copies with a reference to the package. Keep each example's behaviour
   identical, including its CORS settings.

**Verify:**

```bash
dotnet build Benzene.sln
dotnet build Benzene.Examples.sln
dotnet test  test/Benzene.Mesh.Test/Benzene.Mesh.Test.csproj
```

Add a test for the new middleware: a known path returns the stored content; an unknown path falls
through to `next`; a path attempting directory traversal (`../`) is refused. Use a hand-written fake
`IMeshArtifactStore`.

**Done when:** no `MeshArtifactMiddleware.cs` remains under `examples/`, and both solutions build.

---

## Task 0.3 — Give the host a test project, and fix three defects in it

`deploy/Mesh/Benzene.Mesh.Host` has **no unit tests at all**. Its only coverage is
`.github/workflows/smoke-mesh-compose.yml`, which is a genuine end-to-end test but exercises one
happy path. Slices 1 and 2 both need somewhere to put tests.

**Create `deploy/Mesh/Benzene.Mesh.Host.Test/` and add it to `deploy/Mesh/Benzene.Mesh.Host.sln`.**

Put it there, **not** in `test/Benzene.Mesh.Test/`. Adding a `ProjectReference` to the host from
that project would pull the host into `Benzene.sln`'s build graph, directly contradicting the
"why this isn't in `Benzene.sln`" rationale that both `deploy/Mesh/README.md` and the host's
`CLAUDE.md` state deliberately. The independent lifecycle is the point. This decision is made.

Match the repo's test conventions exactly (xUnit, `Assert`, `IsPackable=false`, class `<Type>Test`,
method `Scenario_ExpectedResult`).

**Then fix these three defects, each with a test:**

**(a) A missing config file starts a silently empty mesh.** `Program.cs` loads
`MESH_CONFIG_PATH` with `optional: true`. If the operator sets the variable and the path is wrong,
the host starts happily with zero services and an empty dashboard, saying nothing. `deploy/Mesh/README.md`
even documents a local-dev command pointing at a `mesh.json` that does not exist in the repo.

Fix: if `MESH_CONFIG_PATH` is **set** and the file is **missing**, fail at startup with a message
naming the path. Unset stays optional — that is the legitimate env-var-only path the README
documents.

**(b) `owningTeam` is unreachable from config.** `MeshServiceRegistryEntry` has an `OwningTeam`
property; `MeshHostServiceConfig` has no matching property and `ToEntry()` never passes one. So the
"who do I talk to before I change this" field the UI renders can never be populated from `mesh.json`.
Add `OwningTeam` to `MeshHostServiceConfig` and pass it through.

**(c) The CI path filter has a hole.** `.github/workflows/build-mesh-host.yml` triggers on changes to
`src/Benzene.Mesh.{Aggregator,Aws.Lambda,Contracts,Ui}` — but the host's csproj also references
`src/Benzene.Mesh.Dispatch`. A breaking change there would not trigger this build. Add it, plus
`src/Benzene.Mesh.Artifacts/**` from task 0.2.

While you are in that file, add a test step — it currently only restores and builds:

```yaml
    - name: Test
      run: dotnet test deploy/Mesh/Benzene.Mesh.Host.sln --no-build
```

**Verify:**

```bash
dotnet build deploy/Mesh/Benzene.Mesh.Host.sln
dotnet test  deploy/Mesh/Benzene.Mesh.Host.sln
```

Tests to write: `MESH_CONFIG_PATH` set + missing file → throws naming the path; unset → starts;
`MeshHostServiceConfig.ToEntry()` carries `OwningTeam`; `Source` defaults to `"Http"` when omitted.

---

## Definition of done

- [ ] `dotnet build Benzene.sln`, `dotnet build Benzene.Examples.sln`, and
      `dotnet build deploy/Mesh/Benzene.Mesh.Host.sln` all green.
- [ ] `dotnet test test/Benzene.Mesh.Test/...` and `dotnet test deploy/Mesh/Benzene.Mesh.Host.sln` green.
- [ ] A pre-registered `HttpClient` survives an adapter registration, proven by a test.
- [ ] Two usage sources still register as two, proven by a test.
- [ ] `Benzene.Mesh.Artifacts` exists with a `CLAUDE.md`; no copy remains under `examples/`.
- [ ] The host fails loudly on a missing `MESH_CONFIG_PATH` file.
- [ ] `build-mesh-host.yml` watches Dispatch and Artifacts, and runs tests.
- [ ] No public API signature changed. No behaviour a user can see changed, except (a).

## Do NOT

- Do not apply `TryAdd` to `IMeshServiceSource`, `IMeshUsageSource`, `IMeshTraceSource` or
  `IMeshFleetReadModel`. Read task 0.1's "do NOT change" table again before you commit.
- Do not change `CompositeMeshFleetReadModel` to take `IEnumerable<IMeshTraceSource>`. It is a real
  limitation, deliberately deferred — see the backlog in [`README.md`](README.md). Config v1 selects
  one fleet source, so it is not blocking, and doing it here widens this slice.
- Do not add an `IMeshIssueSource`, and do not touch
  `CompositeMeshFleetReadModel.ServiceAsync`/`TopicAsync`. Both are on the deferred list for reasons.
- Do not add NuGet dependencies. Everything here uses what is already referenced.
- Do not restructure `Benzene.sln` beyond adding the one new project.

## Report back with

The exact list of registrations you changed to `TryAdd`; the diff summary of the five artifact
middlewares (confirming they collapsed cleanly, or what differed); and the output of all four
build/test commands.
