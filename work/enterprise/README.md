# Mesh enterprise readiness — implementation briefs

**Living document.** These are plans still being worked; see [`work/README.md`](../README.md) for
the living-vs-dated rule.

## What this is

The research that produced these slices lives in the specification repo:
[`Benzene/work/mesh-enterprise-readiness.md`](https://github.com/daniellepelley/Benzene/blob/main/work/mesh-enterprise-readiness.md).
Read it once for the *why*. Everything here is the *how* — one brief per slice, written to be picked
up cold by an implementer who has not read this conversation and does not know Benzene's internals.

The one-line finding it rests on: **the flexibility already exists in code — four hand-built mesh
servers prove it — but none of it is reachable from configuration, and there is no auth anywhere.**
These slices promote existing flexibility from code to configuration, and put a door on the building.

Relationship to [`work/service-mesh-roadmap-1.0.md`](../service-mesh-roadmap-1.0.md): that document
is the living mesh roadmap and stays the owner of mesh direction. §7.4 there sketches a `mesh.json`
carrying `services` plus a `tempo` block; slice 1 **supersedes that sketch** with a fuller schema and
says so in the roadmap when it lands. §4.4 (service registry) is the section this work extends.

## The slices

| # | Slice | Depends on | Ready to build? |
|---|---|---|---|
| 0 | [Make the shipped adapters composable](slice-0-composable-adapters.md) | — | Yes |
| 1 | [Config schema v1 — the whole catalog from `mesh.json`](slice-1-config-schema.md) | 0 | Yes |
| 2 | [Auth in the host](slice-2-auth.md) | 1 | Yes |
| 3 | [Discovery as a separate deployable](slice-3-discovery.md) | 1 | Yes |
| 4 | [New sources — Prometheus/OTel and Elasticsearch](slice-4-sources.md) | 1 | **No — design first** |
| 5 | [Packaging polish](slice-5-packaging.md) | 1, 2 | Yes |

Slice 0 did not appear in the research document's roadmap. It was extracted from the "engineering
pre-work slice 1 depends on" list, because those items are mechanical refactors — the ideal first
pickup, and they must land before slice 1 or slice 1 fights them.

**Defects found while writing these briefs**, each folded into a slice rather than left loose:

| Defect | Fixed in |
|---|---|
| Eight adapters' XML docs claim they register a client "unless one is already registered"; the code uses plain `AddSingleton` and always registers | 0.1 |
| `MESH_CONFIG_PATH` set to a missing file starts a silently empty mesh (`optional: true`), and the local-dev command in `deploy/Mesh/README.md` points at a `mesh.json` that does not exist in the repo | 0.3(a), 1.6 |
| `MeshHostServiceConfig` has no `owningTeam`, so a field the UI renders can never be set from config | 0.3(b) |
| `build-mesh-host.yml` path filters omit `src/Benzene.Mesh.Dispatch/**`, which the host references — a breaking change there would not trigger the build. It also runs no tests | 0.3(c) |
| `/artifacts` is served by ASP.NET `UseStaticFiles`, **outside** the Benzene pipeline — so auth applied only to the pipeline would leave the entire estate world-readable | 2.2, 2.7 |
| `MeshRegistryJson.Deserialize` has no production caller: discovery writes `registry.json` and nothing reads it back | 3.1 |

Slice 4 is deliberately marked not-ready: it needs decisions from `observability-product-owner`
(source shapes, the cross-port usage-metric convention) before anyone writes code. Do not start it
from this brief alone.

## Picking up a slice — the contract

1. **Read the whole brief first.** Each is self-contained: file paths, current code quoted verbatim,
   the exact change, and a verification command per task. You should not need to go exploring.
2. **One slice per branch, one logical change per commit.** Branch `claude/mesh-enterprise-slice-N`.
3. **Work the tasks in order.** They are sequenced so the build stays green between them.
4. **Verify with the stated command after every task.** Not at the end.
5. **Stop and report — do not improvise — if any of these happen:**
   - The code you find does not match what the brief quotes. The brief was written on 2026-08-10;
     if it has drifted, the plan may be wrong, and guessing compounds it.
   - A task needs a design decision the brief does not make.
   - A test that passed before your change now fails and the fix is not obvious in one step.
   - You find yourself editing a file the brief's **Do NOT touch** list names.
6. **Do not widen scope.** A brief that says "three files" means three files. If you spot an
   unrelated bug, write it down in the report; do not fix it in this branch.

## House rules for every slice

**Build and test**

```bash
dotnet build Benzene.sln
dotnet test test/Benzene.Mesh.Test/Benzene.Mesh.Test.csproj    # the mesh tests specifically
dotnet test test/Benzene.Core.Test/Benzene.Test.csproj         # the main suite — note the filename
```

**Filename trap:** `test/Benzene.Core.Test/` contains a csproj called **`Benzene.Test.csproj`**, not
`Benzene.Core.Test.csproj`. Getting this wrong is the most common way to conclude "there are no
tests".

`deploy/Mesh/Benzene.Mesh.Host` is **outside `Benzene.sln`** by design (see `deploy/Mesh/README.md`).
Build it explicitly:

```bash
dotnet build deploy/Mesh/Benzene.Mesh.Host/Benzene.Mesh.Host.csproj
```

If `dotnet` is unavailable in your environment, say so and fall back to CI
(`.github/workflows/build-benzene.yml`) as the verification loop — do not guess whether it compiles.

**Test conventions** — verified against the existing suite, not guessed:

- **xUnit** with the built-in `Assert`. `Moq` is referenced but the house style in the mesh tests is
  a **hand-written private fake** (`FakeReadModel`, `RecordingMeshReportPublisher`) — prefer that.
- Test class `<TypeUnderTest>Test` — singular "Test". Method `Scenario_ExpectedResult`, e.g.
  `SecondAddMessageHandlersCall_HandlersAreStillDiscoverable`.
- A comment above the class explaining *why the test exists* (what regression it guards) is a strong
  convention here. Follow it.
- `test/Benzene.Mesh.Test/MultipleAddMessageHandlersCompositionTest.cs` is the best single model for
  a DI/wiring test — read it before writing one.
- **A test touching a new `src/` package needs a `ProjectReference` added to the test `.csproj`.**
  This is the most common trap in this repo.
- `.claude/agents/test-writer.md` encodes the repo's test-writing rules if you want them in full.

**Code conventions**

- Match the surrounding code's style, naming and comment density. Read the neighbouring file before
  writing a new one.
- Every `src/<Package>/` has a `CLAUDE.md`. Read the relevant one before working in that package,
  and update it in the same change.
- `version.txt` is the single version source. **No `.csproj` under `src/` may set `Version` or
  `PackageVersion`.** (`deploy/` artifacts version independently and deliberately do set theirs.)
- New NuGet dependencies need approval per `AGENTS.md`. Exactly one is pre-approved across this
  whole set: `Microsoft.AspNetCore.Authentication.OpenIdConnect`, in slice 2.
- Comments state constraints the code cannot show. Do not write comments explaining what the next
  line does, or justifying the change to a reviewer — that is noise the moment it merges.
- Public API additions are additive. **Do not change an existing public signature** in slices 0–3;
  every one of these slices can be built additively, and a breaking change needs its own decision.

**Configuration**

- **Credentials never go in `mesh.json`.** Config names endpoints and options; secrets come from the
  environment or the platform's secret store. The host's existing AWS-credential-chain stance
  generalizes: if a brief seems to ask you to put a secret in config, you have misread it — stop.
- **Unknown names fail fast at startup, listing the valid values.** A silent fallback to a default
  when someone typos a source name is the single worst failure mode this work can ship.

**Do NOT, in any slice**

- Do not add an assembly-loading plugin mechanism (`"plugin": "/path/to.dll"`). It is a security
  hole in exactly the product being hardened. Custom sources are the code path — copy the host.
- Do not edit `src/Benzene.Mesh.Ui/mesh-ui.html` or `mesh-spec-ui.html`. They are **generated**
  build outputs vendored from `benzene-ui`, guarded by a drift check in CI. Change
  [`benzene-ui`](https://github.com/daniellepelley/benzene-ui)'s `src/`, rebuild, re-vendor.
- Do not edit the conformance fixtures under `docs/specification/conformance/` (or the vendored
  copies) to make an implementation pass.
- Do not put mesh-server configuration, auth requirements, or discovery mechanics into the
  language-neutral spec. The research document's §5 rules on this and the answer is no; the one
  accepted spec change is an informative security paragraph, which is not part of any slice here.

## Deferred — deliberately in no slice yet

These came out of the audit and are real, but none blocks the five slices. Each needs its own
decision before it becomes a brief.

- **`CompositeMeshFleetReadModel` takes one `IMeshTraceSource`, not `IEnumerable<>`.** Config v1
  selects a single fleet source, so this is not blocking. It becomes blocking the day someone wants
  X-Ray and Tempo in one host.
- **There is no `IMeshIssueSource` port.** Issues are welded to the in-memory collector store; the
  composite plane marks them permanently missing. Needed before an issue feed can come from
  Elasticsearch — so it is a prerequisite for part of slice 4, not for slice 1.
- **`CompositeMeshFleetReadModel.ServiceAsync`/`TopicAsync` return hardcoded `null`.** Two of the
  five `benzene:mesh:query:*` topics are dead on every non-push deployment — the service and topic
  drill-in pages cannot work on an X-Ray/Tempo-backed mesh. This is a **bug, not a gap**, and it
  should be fixed on its own merits rather than folded into a config slice.
- **Timing warning that outlives all of these:** the `benzene:mesh:query:*` topics are deliberately
  *not* in the spec (`mesh.md` §4: they "join the spec if a second collector or third-party view
  needs them pinned"). A shipped, configurable mesh host is exactly that second consumer. Any
  reshaping of those contracts is cheap now and expensive after slice 1 ships.
