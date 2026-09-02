# Ergonomics review: rungs 4–5 (default service standard + mesh), .NET port @ f3f1be5

**Reviewer seat:** cross-language ergonomics champion (spec repo), enforcing
`docs/specification/design-principles.md` §4.1 "The shorthand ladder" from the **service author's**
and the **platform operator's** seats.
**Method:** every claim below is traced against source at commit `f3f1be5`. **No .NET SDK was
available; nothing was built or run.** Every finding is trace-only; where a finding rests on a
runtime outcome I say so and name the lines the trace rests on.
**Scope read in full:** design-principles §1/§2/§4.1/§5, mesh.md, cloud-service-profile.md;
`src/Benzene.CloudService` (all files), `Benzene.Mesh.Wire/Extensions.cs`, `Benzene.Mesh.Dispatch/Extensions.cs`,
`Benzene.Mesh.Ui/MeshUiExtensions.cs`, every mesh package's `CLAUDE.md` and public `static` surface;
`deploy/Mesh/**` (README, CONFIG, sample, Host source, Helm chart); `examples/Mesh`, `AwsMesh`,
`AzureMesh`, `AzureFunctionsMesh`, `GoogleCloudMesh`, `K8sMesh` (service side and mesh-host side,
Terraform variables/env blocks, manifests, READMEs); `docs/mesh-ui.md`, `docs/mesh-usage-feed.md`,
`docs/hosting.md`, `docs/contract-artifacts.md`, `docs/capability-matrix.md`, `docs/reference/packages.md`.

---

## Executive verdict

1. **Go-live blockers: 3 · should-fix: 10 · polish: 5.**
2. The **service-author shorthand is excellent** — `UseBenzeneCloudService("orders")` is one call,
   composed from public `Use*` builders, with an honest self-report — but its mesh half
   (`WithCollector`) **sends to an endpoint the shipped operator tool cannot receive**, and its
   register/heartbeat leg has **no public explicit form** (B2, B3).
3. The **operator shorthand (`Benzene.Mesh.Host` + `mesh.json`) is the best-validated config surface in
   the estate**, but its Test Console posts dispatch to `/benzene/invoke` while dispatch is mounted at
   `/mesh/dispatch` — the button cannot work as wired (B1), and the sample/Helm defaults still point at
   the pre-§5 paths (`/spec?type=benzene`, `/healthcheck`) that no `UseBenzeneCloudService` service serves (S4).
4. **Duplication is the loudest signal:** the Cloud-Service preamble is hand-copied 4×, the mesh-host
   preamble 5–6×, `BuildRefreshGuardOptions` 5×, `MeshRefreshHandler` 4× (already drifted), the
   observability prelude 7+× — each copy is a missing seam in the library, not an example smell (S2, S3).
5. **Verdict: NEEDS CHANGES before go-live** — B1 and B2 are user-visible dead ends in the two
   shorthands this rung exists to provide; B3 is a rule-2 hostage. Everything else is should-fix/polish
   and could ship behind an honest note.

---

## Findings (by severity)

### B1 — BLOCKER · Host's Test Console sends `mesh:dispatch` to a path where nothing dispatches  `[magic · ladder-broken]`

**§4.1 clause:** "A shorthand MUST be composed from the public explicit form" / "The price of a
convention is a start-up check" — two defaults for one endpoint disagree, and nothing checks them against each other.

**Evidence (trace-only):**

- `deploy/Mesh/Benzene.Mesh.Host/Startup.cs:365-372` — the UI is told to POST dispatch to
  `MeshUiExtensions.DefaultDispatchUrl`:
  ```csharp
  asp.UseMeshUi(
      path: "/mesh-ui",
      manifestUrl: manifestUrl,
      envelopeUrl: _fleetEnabled ? "/benzene/invoke" : null,
      dispatchUrl: _config.Dispatch.Enabled ? MeshUiExtensions.DefaultDispatchUrl : null,
  ```
- `src/Benzene.Mesh.Ui/MeshUiExtensions.cs:164` — `public const string DefaultDispatchUrl = "/benzene/invoke";`
- `deploy/Mesh/Benzene.Mesh.Host/Startup.cs:417-444` — the dispatch envelope and its guard are mounted at
  `dispatchGuardOptions.Path`, i.e. `MeshDispatchGuardOptions.Path` = `"/mesh/dispatch"`
  (`src/Benzene.Mesh.Dispatch/MeshDispatchGuardOptions.cs:30`), and `MeshAuthGate.DispatchPath`
  (`MeshAuthGate.cs:69`) enforces `dispatchRole` on that same `/mesh/dispatch` only.
- What answers at `/benzene/invoke` in the Host: nothing when `fleet.source` is `none` (the default),
  or — when a fleet source is set — an envelope whose inner pipeline routes **only**
  `MeshCollectorHandlers.Queries` (`Startup.cs:384-385`); `benzene:mesh:dispatch` is not among them.
- The acceptance test pins the wrong value rather than a round trip:
  `deploy/Mesh/Benzene.Mesh.Host.Test/MeshUiWiringAcceptanceTest.cs:116`
  `Assert.Contains("data-dispatch-url=\"/benzene/invoke\"", …)` while the dispatch tests in the same folder
  hard-code `DispatchPath = "/mesh/dispatch"` (`MeshDispatchRoleAcceptanceTest.cs:37`, `MeshDispatchSizeGuardAcceptanceTest.cs:32`).
- The constant's own doc (`MeshUiExtensions.cs:157-163`) says dispatch "typically rides the same message
  endpoint fleet queries already use" — but **both** shipped consumers (`Benzene.Mesh.Host` and
  `examples/AwsMesh/Mesh/Startup.cs:224-239`, "DISPATCH GETS ITS OWN DOOR, deliberately") give it its own
  path. The default's premise is contradicted by every caller.

**What the operator experiences:** sets `dispatch.enabled: true`, an auth mode, passes `--validate-config`
(which cannot see this), opens the Test Console, presses Send — gets a 404 (no fleet) or a `not-found`
envelope result (fleet on). Nothing in `--validate-config`, the startup summary, or the UI names the
mismatch. This is exactly "finding out late".

**Proposed change:**
```csharp
// Benzene.Mesh.Ui/MeshUiExtensions.cs — make the two defaults one value
public const string DefaultDispatchUrl = "/mesh/dispatch";   // == MeshDispatchGuardOptions default Path

// deploy/Mesh/Benzene.Mesh.Host/Startup.cs:369 — derive from the guard options actually mounted
dispatchUrl: _config.Dispatch.Enabled ? dispatchGuardOptions.Path : null,
```
and replace the `MeshUiWiringAcceptanceTest:116` attribute assertion with a test that POSTs a
`mesh:dispatch` envelope to the injected `data-dispatch-url` and asserts it reaches `MeshDispatchMessageHandler`.

---

### B2 — BLOCKER · The service shorthand's destination does not exist in the operator shorthand  `[ceremony · parity]`

**§4.1 clause:** "Every capability a service needs routinely MUST have a shorthand" — on **both** ends of
the wire (§4 "every convention, both sides"). Today the producer side is one line and the consumer side is 142 lines.

**Evidence:**

- Service side, one call: `ICloudServiceBuilder.WithCollector(url)` (`src/Benzene.CloudService/CloudServiceBuilder.cs:35`)
  → `MeshAnnouncer` posts `benzene:mesh:register` / `benzene:mesh:heartbeat` and `HttpMeshTraceExporter`
  posts `benzene:mesh:traces` to that envelope URL (`Extensions.cs:72-86`). The examples point it at the
  mesh's `/benzene/invoke` (`examples/K8sMesh/k8s/services.yaml:27`, `examples/Mesh/*/Startup.cs`).
- Operator side, `Benzene.Mesh.Host` has **no push collector**:
  `MeshSourceRegistrar.cs:42` `ValidFleetSources = { "none", "xray", "tempo", "jaeger" }`; `RegisterFleet`
  has no `collector` case; `MeshCollectorStore` is never registered; the Host's only `/benzene/invoke`
  routes `MeshCollectorHandlers.Queries` (`Startup.cs:384-385`) — register/heartbeat/traces would be rejected
  by that pipeline. The Host's only ingestion is the legacy `/mesh/report` (`MeshReportMessageHandler`).
- The explicit form the operator must therefore hand-roll: `examples/K8sMesh/Mesh/Startup.cs` (142 lines),
  specifically `:80-81` (`AddSingleton<MeshCollectorStore>` + `AddSingleton<IMeshFleetReadModel>`) and
  `:111-112` (`UseBenzeneMessage(new BenzeneMessageHttpOptions { Path = "/benzene/invoke" }, collector =>
  collector.UseMessageHandlers(MeshCollectorHandlers.All))`), plus `examples/Mesh/Benzene.Examples.Mesh.Aggregator/Startup.cs:18-25,50`
  which does the same with a bespoke `EnvelopeHost`.
- `deploy/Mesh/README.md` never mentions `WithCollector`, `benzene:mesh:register`, or the push feeds; it
  steers pull-less services to `Benzene.Mesh.Reporting` + `/mesh/report` (README "What it does", bullet 2).
- `docs/mesh-ui.md` ("The live Fleet plane → Serving it") documents the collector wiring **as code the
  operator writes**, not as a Host config key.

**What the operator experiences:** "I ran the shipped mesh image, my services say they're meshed
(`WithCollector`), the Fleet tab says collector unreachable / nothing registers." The two shorthands
were built against different mesh designs (spec §9 acknowledges the convergence is "the natural
integration follow-up") — but from the seat of someone who took both steers, it is simply a hole.

**Proposed change** — add the missing rung to `mesh.json` (composed from the exact public calls K8sMesh uses):
```jsonc
// mesh.json — before: impossible; after:
{ "fleet": { "source": "collector" } }
```
```csharp
// MeshSourceRegistrar.RegisterFleet — new case
case "collector":
    services.AddSingleton<MeshCollectorStore>();
    services.AddSingleton<IMeshFleetReadModel>(r => r.GetService<MeshCollectorStore>());
    services.AddSingleton<IMeshUsageSource, CollectorUsageSource>();   // usage.json from the same store
    return true;
// Startup.Configure — when the fleet source is the collector, route All (ingest + queries), not Queries:
asp.UseBenzeneMessage(new BenzeneMessageHttpOptions { Path = "/benzene/invoke" },
    fleet => fleet.UseMessageHandlers(_collectorEnabled ? MeshCollectorHandlers.All : MeshCollectorHandlers.Queries));
```
Reuse `auth.ingestion` for the ingest topics (they are services self-reporting, exactly the case that
config section exists for). Then delete `K8sMesh/Mesh/Startup.cs:76-81,108-112` and `examples/Mesh/.../Aggregator/Startup.cs:18-25,50`
in favour of the key, and document `WithCollector` ↔ `fleet.source: collector` as a pair in `deploy/Mesh/README.md`.

---

### B3 — BLOCKER · Register + heartbeat has no public explicit form; the shorthand holds it hostage  `[ladder-broken]`

**§4.1 clause:** "A shorthand that can do something no composition of public API can do has taken a
capability hostage." Rule-2 test 1 fails: a user cannot write `WithCollector`'s announce loop from public API.

**Evidence:**

- `src/Benzene.CloudService/MeshAnnouncer.cs:22` — `internal sealed class MeshAnnouncer`;
  `CloudServiceDescriptorSource.cs:16` — `internal sealed class`. Neither `Benzene.CloudService` nor
  `Benzene.Mesh.Wire` exposes a `UseMeshAnnounce(...)` / `MeshAnnouncer` / heartbeat builder.
  `Benzene.Mesh.Wire`'s public surface is `UseMeshDescriptor`, `UseMeshTrace`, `UseMeshIssues`,
  `MeshDescriptorFactory.Create`, `HttpMeshTraceExporter` (`Extensions.cs:25,63,139`) — the descriptor and
  trace legs are public; the register/heartbeat leg (spec R6, mesh.md §4–§5) is not.
- Proof a user needs it and re-derives it: `examples/Mesh/Benzene.Examples.Mesh.Shared/EnvelopeHost.cs:106-151`
  (`StartAnnouncing` + `SendAsync`, 45 lines) is a hand copy of `MeshAnnouncer.RunAsync/SendAsync`
  (`MeshAnnouncer.cs:98-152`) — same retry-until-registered loop, same 10 s heartbeat, same swallow-everything.
  That is the second copy; the rule says the second copy is the signal.
- Consequence for rung 2 (design-principles §2: "a rung-2 service still participates in the mesh's trace
  feed (reduced)"): a middleware-only service can `UseMeshTrace` (public) but cannot heartbeat without
  hand-rolling, because `UseBenzeneCloudService` is `TContext : IHttpContext` + always `UseMessageHandlers`
  (`Extensions.cs:52-56,173,182`) — the rung-3 shorthand, not usable at rung 2.

**Proposed change** — promote the announcer to the wire layer as the public explicit form and compose the shorthand from it:
```csharp
// Benzene.Mesh.Wire (already depends on HealthChecks.Core) — public explicit form
public static IMiddlewarePipelineBuilder<TContext> UseMeshAnnounce<TContext>(
    this IMiddlewarePipelineBuilder<TContext> app,
    MeshServiceInfo info, MeshServiceDescriptor descriptor, string collectorEnvelopeUrl,
    IEnumerable<IHealthCheck> healthChecks, TimeSpan? heartbeatInterval = null);

// Benzene.CloudService/Extensions.cs — the shorthand composes it (no behaviour change)
UseAnnouncerStart(envelope, announcer, descriptorSource);   // today: internal type
// becomes: envelope.UseMeshAnnounce(info, descriptorSource.Get(...), builder.CollectorEnvelopeUrl, healthChecks);
```
Then `EnvelopeHost.StartAnnouncing` is deleted and the rung-2 story becomes three public lines:
`UseMeshTrace` + `UseMeshDescriptor` (optional) + `UseMeshAnnounce`.

---

### S1 — SHOULD-FIX · The rung-4/5 ladder is invisible from the docs site  `[invisible-ladder]`

**§4.1 clause:** "The ladder MUST be visible from the top. A shorthand's documentation MUST name the explicit form it composes."

**Evidence (counts):**
- `UseBenzeneCloudService` appears in **0 guide pages**. Whole-docs grep: `docs/capability-matrix.md:138,142`
  (two table cells) and `docs/reference/packages.md:273` (one row). No getting-started page, not
  `docs/hosting.md` (grep for `/benzene`, `well-known`, `Cloud Service`: 0 hits), not `docs/health-checks.md`,
  not `docs/spec.md`.
- `WithCollector`, `WithoutMesh`, `WithConsumes`, `WithInvokePath` etc.: **0 docs hits**. The only prose naming
  the explicit form (`UseBenzeneMessage` + `UseSpec` + `UseHealthCheck` + `UseMeshTrace` + `UseMeshDescriptor`)
  is `src/Benzene.CloudService/CLAUDE.md` — an agent file, not user documentation.
- `docs/getting-started-kubernetes.md:360` links to `examples/K8sMesh` as "a full mesh" but never says how a
  service joins one.
- Rung-2 participation: the spec promises it (§2, §3 table "Mesh trace feed — works with middleware alone");
  no .NET page shows `UseMeshTrace` on a middleware-only pipeline.

**What the user experiences:** the newcomer path (getting-started → hosting) never reaches rung 4. A reader
who finds `UseBenzeneCloudService` in the capability matrix cannot find out what it did, how to relocate a
path, or how to drop one level. Per §4.1 that is indistinguishable from no escape hatch.

**Fix:** a `docs/cloud-service.md` page ("Becoming a Benzene Cloud Service", linked from `hosting.md` and every
getting-started page's "next steps") with three sections in ladder order: (1) the one-liner and what it
provisions (the R1–R8 list already in `Extensions.cs:24-30`); (2) *the explicit form* — the six public calls it
composes, verbatim; (3) *joining the mesh* — `WithCollector` ↔ `fleet.source: collector` (B2), `WithoutMesh`,
and the rung-2 `UseMeshTrace`-only variant with code. Add `[MeshTopic]`/profile self-report reading via
`benzene profile-check` (already exists per `docs/capability-matrix.md`).

---

### S2 — SHOULD-FIX · The Cloud-Service preamble is hand-copied across every example  `[duplication ×4, +7]`

**§4.1 clause:** "Duplicated plumbing across examples is a framework bug … the third is a backlog item.
Copying it a fourth time is choosing not to fix it."

**The count** (byte-near-identical blocks, service side):

| Block | Copies | Where |
|---|---|---|
| `.UseBenzeneCloudService($"{name}-api", c => c.WithServiceVersion("1.0.0").WithInstanceId(name).WithPlacement(<cloud>, <env-region>).WithHealthChecks(hc).WithHandlers(handlers))` | **4** | `examples/AwsMesh/Shared/MeshServiceWiring.cs:240-245`, `examples/GoogleCloudMesh/Shared/MeshServiceWiring.cs:64-69`, `examples/AzureFunctionsMesh/Shared/MeshServiceWiring.cs:86-91`, `examples/K8sMesh/Service/Startup.cs:156-163` |
| `SetApplicationInfo(name, "1.0.0", …).AddDiagnostics().AddMessageHandlers(handlers).AddHttpMessageHandlers()` | **4** | Google `:42-45`, AzureFunctions `:63-66`, K8s Service `:73-76`, AwsMesh `:84-93` (variant) |
| `.UseW3CTraceContext().UseBenzeneEnrichment().UseBenzeneMetrics()` observability prelude | **7+** | AwsMesh `Observe()` `:303-308` (applied to 5 pipelines), K8s Service `:144-146`, AzureMesh Mesh `:115-117`, K8sMesh Mesh `:89-91`, AwsMesh Mesh `:155-157,165-167` |
| Region-from-env for `WithPlacement` | **3 different env vars** | `AWS_REGION` (AwsMesh `:233`), `REGION` (Google `:58`), `REGION_NAME` (AzureFunctions `:80`), literal `"in-cluster"` (K8s `:161`) |
| `services.AddOpenTelemetry().ConfigureResource(...).WithTracing(...).WithMetrics(...)` with the same "only attach the exporter if the env var is set" dance | **4** | K8s Service `:53-65`, AzureMesh Mesh `:49-61`, K8sMesh Mesh `:40-51`, AzureFunctions Shared `:50-56` |

**Which category:** missing shorthands, not deliberate demonstrations — none carries a "this is the explicit
form" comment; every copy has a *different* comment explaining the same thing.

**Fixes (each a public-API proposal, not a merge):**
1. **Placement detection with a start-up log.** Spec §2 says placement is "detected from the platform's
   documented environment or configured explicitly". Ship `WithPlacement()` (no args) = detect from
   `AWS_REGION`/`AWS_EXECUTION_ENV`, `FUNCTIONS_WORKER_RUNTIME`+`REGION_NAME`, `K_SERVICE`/`KUBERNETES_SERVICE_HOST`,
   default `self-hosted`, and **log what was detected and from which variable** (rule 3). Keep the explicit
   overload.
2. **`UseBenzeneObservability()`** = `UseW3CTraceContext().UseBenzeneEnrichment().UseBenzeneMetrics()` — the
   three-call prelude appears before every mesh pipeline in the repo and nowhere is it a choice.
3. **Fold service identity into one place.** `SetApplicationInfo(name, version, …)` and
   `UseBenzeneCloudService(name, c => c.WithServiceVersion(version))` say the same thing twice; the shorthand
   already calls `SetApplicationInfo(serviceName, "", "")` (`Extensions.cs:95`) and *overwrites the version with
   empty*. Let `WithServiceVersion` flow into `IApplicationInfo.Version` so the examples drop the duplicate line.

---

### S3 — SHOULD-FIX · The mesh-host preamble is hand-copied across every example, and one framework default is wrong for 4 of 5 consumers  `[duplication ×5 · parallel-path]`

**The count** (mesh-host side):

| Block | Copies | Where |
|---|---|---|
| `BuildRefreshGuardOptions()` — parse `MESH_REFRESH_MIN_INTERVAL_SECONDS`, override `Topic = "mesh:refresh"` | **5** (4 identical + AwsMesh variant) | `AzureMesh/Mesh/Startup.cs:144-163`, `K8sMesh/Mesh/Startup.cs:122-141`, `GoogleCloudMesh/Mesh/Startup.cs:72-91`, `AzureFunctionsMesh/Mesh/StartUp.cs:123-142`, `AwsMesh/Mesh/Startup.cs:279-292` |
| `MeshRefreshHandler` (`[Message("mesh:refresh")] [HttpEndpoint("POST","/mesh/refresh")]` over `MeshAggregationPass`) | **4, already drifted** | AzureMesh/K8sMesh/GoogleCloudMesh/AzureFunctionsMesh `Mesh/MeshRefreshHandler.cs` — `diff` shows K8sMesh lost the try/catch→503 the others have; Google renamed `Discovered`→`Aggregated` |
| `MeshAggregationBackgroundService` (30 s loop over the pass) | **2 identical + 2 variants** | `AzureMesh`/`K8sMesh` `Mesh/MeshAggregationBackgroundService.cs` (byte-identical bar namespace), `deploy/Mesh/.../MeshPollBackgroundService.cs`, AzureFunctions timer trigger `StartUp.cs:109-114` |
| `AddMeshAggregator*(new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>()), …)` — a dummy registry because the overload demands one | **5** | AwsMesh `:85`, AzureMesh `:70`, K8sMesh `:58`, Google `:42`, AzureFunctions `:50` |
| `.UseMeshUi("/mesh-ui","manifest.json"…).UseMeshSpecUi("/mesh-spec-ui.html","manifest.json").UseMeshArtifacts(new CorsSettings { AllowedDomains = new[] { "https://studio.asyncapi.com" } })` | **5 + Host** | every `Mesh/Startup.cs` + `deploy/Mesh/.../Startup.cs:365-376` (Host omits the Studio CORS) |
| `MESH_USAGE_WINDOW_HOURS` parse | **3** | AwsMesh `:125-127`, AzureMesh `:86-89`, AzureFunctions `:66-69` |

**The wrong default (rule 2, parallel path):** `Benzene.Mesh.Aggregator` ships `MeshAggregateMessageHandler`
on `benzene:mesh:aggregate` / `POST /mesh/aggregate` (`MeshAggregateMessageHandler.cs:16-17`), and
`MeshRefreshGuardOptions.Topic` defaults to that topic. Four of five example hosts **avoid the shipped
handler** (comments at `AzureMesh/Mesh/Startup.cs:112-113`, `K8sMesh:86-87`: "Scope handler discovery to this
assembly so Benzene.Mesh.Aggregator's own MeshAggregateMessageHandler isn't discovered too"), hand-roll a
parallel `mesh:refresh`, and then each must override the guard's topic (`:146-152` in AzureMesh and the same
paragraph in three siblings). A default that every consumer overrides, with an identical 6-line comment
explaining why, is the framework telling you its default is wrong.

**Fixes:**
1. Ship the refresh rung in `Benzene.Mesh.Aggregator`: a `MeshRefreshMessageHandler` over
   `MeshAggregationPass` on `benzene:mesh:refresh` / `POST /mesh/refresh`, with the try/catch→503 the examples
   converged on; make `MeshRefreshGuardOptions.Topic` default to it; and give `MeshAggregationPass` a
   `AddMeshAggregationLoop(TimeSpan)` hosted-service registration (the `BackgroundService` ×2 and the Host's
   third copy collapse into it).
2. `AddMeshAggregator(artifactRootDirectory)` / `AddMeshAggregatorWithS3(bucket, prefix)` overloads without a
   registry, for discovery-driven hosts.
3. `MeshRefreshGuardOptions.FromEnvironment()` (or have the guard read `MESH_REFRESH_MIN_INTERVAL_SECONDS`
   itself with a logged parse), and a `MeshArtifactExtensions.AsyncApiStudioCors` constant or a
   `UseMeshArtifacts(allowAsyncApiStudio: true)` flag.
4. `UseMeshDashboard(manifestUrl, …)` = `UseMeshUi + UseMeshSpecUi + UseMeshArtifacts` — the three are never
   used apart.

---

### S4 — SHOULD-FIX · Host `services[]` entries: documented-required keys are unvalidated, and the shipped samples point at pre-standard paths  `[magic · convention without a check]`

**§4.1 clause:** "The price of a convention is a start-up check … A convention that can first fail on the
message path has not paid for itself." Also §5.3: "New framework-provided HTTP surfaces MUST default to a
`/benzene/`-prefixed path."

**Evidence:**
- `deploy/Mesh/CONFIG.md` "services" table: `specUrl`/`healthUrl` "**Required** for `source: "Http"`".
  `MeshHostConfig.cs:98` `ToEntry() => new(Name, SpecUrl ?? string.Empty, HealthUrl ?? string.Empty, …)` —
  nothing in `MeshConfigValidator.Validate` (`MeshConfigValidator.cs:28-54`) or `Startup` checks them.
  `HttpMeshServiceSource.cs:27` then calls `GetStringAsync("")` on the **first poll**; the service is
  recorded `Unreachable` with an exception type name. `--validate-config` prints `services: 2` and exits 0.
- `services[].name` empty: unvalidated; `MeshServiceRegistryEntry` has no constructor checks (grep: 0 throws
  in `Benzene.Mesh.Contracts/MeshServiceRegistry*.cs`).
- Duplicate `services[].name`: `Startup.cs:125-128` `byName[service.Name] = service.ToEntry()` — last wins, silently.
- `pollIntervalSeconds: 0` or negative: `MeshPollBackgroundService.cs:32` `Math.Max(1, …)` — silently becomes a
  1-second hammer on every service; no warning.
- **Pre-standard paths in every operator sample:** `deploy/Mesh/mesh.sample.json:7-8`
  (`/spec?type=benzene`, `/healthcheck`), `deploy/Mesh/helm/benzene-mesh/values.yaml:34-35` (same),
  `deploy/Mesh/CONFIG.md` "Worked examples" (same), `deploy/Mesh/README.md` Helm `--set` example (same).
  A `UseBenzeneCloudService` service serves `/benzene/spec` and `/benzene/health` (`CloudServicePaths`);
  `examples/K8sMesh/compose/mesh.json` correctly uses those. An operator who copies the sample against a
  profile-conformant service sees `Unreachable` at first poll, not a startup error.
- The §5 defaults make `specUrl`/`healthUrl` *derivable* from one base URL — and the estate already derives
  them in two places: `examples/GoogleCloudMesh/Mesh/MeshRegistry.cs:27-30`
  (`$"{root}/benzene/spec?type=benzene"`, `$"{root}/benzene/health"`) and
  `Benzene.Mesh.Discovery.Kubernetes` (`http://{name}.{ns}.svc.cluster.local[:port]/benzene/spec|health`, per its CLAUDE.md).

**Proposed change:**
```jsonc
// before (what every sample shows)
{ "name": "orders-api", "specUrl": "http://orders-api:8080/spec?type=benzene", "healthUrl": "http://orders-api:8080/healthcheck" }
// after (the §5 steer; specUrl/healthUrl remain as the explicit override)
{ "name": "orders-api", "url": "http://orders-api:8080" }
```
Validator additions (all run by `--validate-config`): `name` non-empty and unique (name the duplicate);
`source: Http` requires `url` **or** both `specUrl`+`healthUrl`, and each must parse as an absolute URI;
`AwsLambdaInvoke` requires `sourceOptions.functionName` at bind time, not at first invoke;
`pollIntervalSeconds < 1` rejected naming the key. Update sample/Helm/CONFIG to the `/benzene/` defaults.

---

### S5 — SHOULD-FIX · `WithCollector(url)` never validates, never logs, and reports R6 satisfied regardless  `[magic · silent forever]`

**§4.1 clause:** rule 3 — the failure must name what was looked for, where, and what to add. Spec §6
requires the *service* be unaffected; it does not require the operator be kept in the dark.

**Evidence:**
- `CloudServiceBuilder.cs:159-163` stores the string as-is. `HttpMeshTraceExporter` ctor
  (`IMeshTraceExporter.cs:35-50`) stores it as-is. `MeshAnnouncer.SendAsync` (`MeshAnnouncer.cs:138-152`)
  `catch { return false; }` — no `ILogger` exists anywhere in `Benzene.CloudService` (grep: 0). A relative
  path, a typo'd scheme, or a wrong port is retried every 2 s forever with no line in any log.
- `CloudServiceProfileReport.Evaluate` (`CloudServiceProfileReport.cs:94-97`): `R6 = mesh && collector` where
  `collector = CollectorEnvelopeUrl != null` — the descriptor claims R6 satisfied for `WithCollector("nonsense")`.
  (Profile §5 says the report reflects *provisioning*, which is defensible for an unreachable collector; it is
  not defensible for a value that could never be a URL.)

**Fix:** `WithCollector` throws `ArgumentException` naming the method and the value unless
`Uri.TryCreate(url, Absolute)` with an http(s) scheme; `MeshAnnouncer` and `HttpMeshTraceExporter` take an
optional `ILogger` (resolved from the container in `RealizeMeshDisposables`) and log **once** on first
failure and once on first success: `mesh: registration with {url} failed ({reason}); retrying — the service is
unaffected (mesh.md §6)`. That one line is the difference between "reduced by design" and "silently broken".

---

### S6 — SHOULD-FIX · Turning on dispatch is three calls sharing a path that must agree, and the doc names one of them  `[ceremony · invisible-ladder]`

**§4.1 clause:** rule 1 (routine capability needs a shorthand) and rule 4 (the doc must name the form).

**Evidence:**
- `docs/mesh-ui.md` (`dispatchUrl` row): "pair with `Benzene.Mesh.Dispatch`'s `UseMeshDispatch()` on the host".
- `UseMeshDispatch` (`src/Benzene.Mesh.Dispatch/Extensions.cs:29-46`) registers the handler definition,
  gate and HTTP dispatcher **only** — no route, no envelope. The Host's own comment says so:
  `deploy/Mesh/.../Startup.cs:391-394` "UseMeshDispatch alone only registers the handler DEFINITION - no
  [HttpEndpoint] route, no envelope - so mesh:dispatch had no HTTP path reaching it at all."
- The real recipe, in both consumers: `UseMeshDispatchGuard(options)` + `UseMeshDispatch(opts)` +
  `UseBenzeneMessage(new BenzeneMessageHttpOptions { Path = options.Path, TopicFilter = t => t == DispatchTopic },
  d => d.UseMessageHandlers(typeof(MeshDispatchMessageHandler)))` + a `MeshServiceRegistry` in DI
  (`Host Startup.cs:417-444`, `AwsMesh/Mesh/Startup.cs:184-185,233-239`), and in the Host a manual
  `IMeshServiceDispatcher` re-registration to reach `MaxResponseBytes` (`:420-434`, "Rather than change that
  package … register OUR OWN 'Http' IMeshServiceDispatcher … BEFORE calling UseMeshDispatch").

**What the operator experiences:** following the doc yields a Send button with no endpoint; mounting an
envelope without the guard (the doc never mentions the guard) yields an **unguarded** endpoint that runs real
handlers. The safe default (`AllowInProduction=false`, unset env = Production) is genuinely good — but the
ladder to it is three rungs with a shared invariant nobody checks.

**Fix:** one `UseMeshDispatchEndpoint(MeshDispatchGuardOptions? guard = null, MeshDispatchOptions? dispatch = null, int? maxResponseBytes = null)`
in `Benzene.Mesh.Dispatch` (it already depends on the envelope types) that composes the three public calls
above, and documents them as its explicit form. Give `UseMeshDispatch` a `maxResponseBytes` option so the
Host's dispatcher override disappears. Update `mesh-ui.md`'s `dispatchUrl` row to name the endpoint call
*and* the guard.

---

### S7 — SHOULD-FIX · Documentation that does not trace against the current code  `[doc honesty — all trace-only]`

| Claim | Where | What the code says |
|---|---|---|
| `curl -XPOST localhost:8080/mesh/refresh   # {"discovered":3}` | `examples/K8sMesh/README.md:145`; also CI `.github/workflows/deploy-k8s-mesh-example.yml:51` and `deploy-eks-mesh-example.yml:158` (`curl -fsS -XPOST … /mesh/refresh`, no header) | `K8sMesh/Mesh/Startup.cs:96` mounts `UseMeshRefreshGuard`; `MeshRefreshGuardMiddleware.cs:137-140` denies **403** when `X-Benzene-Refresh` is absent. `curl -f` turns that into a CI failure. **If these workflows are currently green, my trace is wrong and the guard is not on the path — either way one of the two is stale.** |
| "Force a discovery pass immediately — `curl -XPOST http://localhost:8090/mesh/refresh`" | `examples/K8sMesh/compose/README.md` "Useful URLs" | `Benzene.Mesh.Host` has no `/mesh/refresh` (grep `refresh` in `deploy/Mesh/Benzene.Mesh.Host/*.cs`: one comment). The shipped route is `POST /mesh/aggregate` (`MeshAggregateMessageHandler.cs:16`). |
| "The six service Lambdas are full Cloud Service Profile (R1–R8) services" | `examples/AwsMesh/README.md:40` (and `:635`); `Shared/MeshServiceWiring.cs:46`, `Orders/Startup.cs:21` | No `WithCollector`/`WithTraceExporter` anywhere in `examples/AwsMesh` (grep: 0). `CloudServiceProfileReport.cs:94-103`: R6 = `mesh && collector` → **false**; R8 = `mesh && traceExporter != null` → **false**. The descriptor these services serve on `benzene:mesh` says `profile.missing: ["R6","R8"]`. Same for GoogleCloudMesh (`README.md:5`) and AzureFunctionsMesh (`README.md:8,42`). |
| "Serves the Mesh UI … the endpoint the page polls is the same wire-envelope endpoint that services use to register, heartbeat, and export traces" | `docs/mesh-ui.md` "The live Fleet plane → Serving it" | True for the hand-wired K8sMesh/examples-Mesh hosts; false for `Benzene.Mesh.Host` and AwsMesh, whose `/benzene/invoke` is queries-only (B2). |
| `deploy/Mesh/README.md` "Serves the Mesh UI dashboard at `/mesh-ui`" — correct; but the README never says the Host cannot receive `WithCollector` traffic | `deploy/Mesh/README.md` "What it does" | See B2. |

**Fix:** correct the three README lines and the two workflow `curl`s (`-H 'X-Benzene-Refresh: 1'`); either
wire the AwsMesh services' trace feed to the X-Ray plane via `WithTraceExporter` (an OTLP-backed
`IMeshTraceExporter` would be a real, reusable adapter) or change the README to "R1–R5, R7; R6/R8 are served
by the X-Ray/CloudWatch plane instead of the push feeds" — which is what the descriptor already says.

---

### S8 — SHOULD-FIX · Config parity across the three deployment shapes  `[parity]`

The **Helm chart is parity-by-construction** (`values.meshConfig` is `mesh.json` rendered verbatim,
`configmap.yaml:12-13`) — that is the right design and the standard the other shapes should meet. The
divergences are between `Benzene.Mesh.Host` (`mesh.json`) and the AwsMesh Lambda (Terraform → env), which
implement the same operator capabilities with different keys, different semantics, and in one case a
different package. Full table in §"Config-parity table" below; the load-bearing rows:

1. **Two OIDC implementations for one capability.** Host: ASP.NET OIDC via `auth.mode: oidc` +
   `auth.oidc.authority/clientId/clientSecretEnvVar/callbackPath(/signin-oidc)/scopes/requireHttpsMetadata`,
   authorisation by `allowedEmailDomains` **and/or** `requiredGroups`, `dispatchRole`. Lambda:
   `Benzene.Mesh.Auth.Oidc.UseMeshOidcAuth` via `GOOGLE_OAUTH_CLIENT_ID/SECRET` + `MESH_OIDC_SIGNING_KEY` +
   `MESH_ALLOWED_EMAILS` (exact emails only — `EmailAllowlist.IsAllowed` "no domain matching"), routes under
   `/mesh/auth/{login,callback,logout}`, no groups, no `dispatchRole`. An operator moving a mesh from Compose
   to Lambda re-learns auth from scratch, and neither shape can express the other's policy
   (Host: cannot allow one exact email; Lambda: cannot require a group).
2. **Dispatch knobs, three spellings:** `dispatch.maxPerMinutePerIdentity` (Host) ·
   `mesh_dispatch_max_per_minute` (Terraform, drops "per identity") · `MESH_DISPATCH_MAX_PER_MINUTE` (env);
   `dispatch.maxPerMinutePerTarget` · `mesh_dispatch_max_per_target_per_minute` (word order swapped) ·
   `MESH_DISPATCH_MAX_PER_TARGET_PER_MINUTE`. `maxRequestBytes`/`maxResponseBytes`/`dispatchRole`: Host only.
   `dispatch.enabled`/`allowInProduction`: Host only — on Lambda dispatch is always wired
   (`AwsMesh/Mesh/Startup.cs:185`) and turned on by `DOTNET_ENVIRONMENT` (Terraform default `"Development"`,
   `variables.tf:190-194`) — so the demo estate has dispatch **on by default**, the opposite of the Host's default.
3. **Bounds handling diverges:** Host rejects a negative/oversized limit at startup
   (`MeshSourceRegistrar.cs:270-299`); Lambda **silently ignores** a negative value and keeps the default
   (`AwsMesh/Mesh/Startup.cs:262-268` "only a non-negative parse wins").
4. **Refresh throttle:** every example host has `UseMeshRefreshGuard` + `MESH_REFRESH_MIN_INTERVAL_SECONDS`; the
   Host has **no refresh guard at all** — its `POST /mesh/aggregate` is reachable to any authenticated caller
   (or anyone, under `auth.mode: none`) with no CSRF header and no throttle. The Host's `MeshUiExtensions.DefaultRefreshUrl`
   is `/mesh/refresh`; the Host never passes `refreshUrl` so the UI's Refresh control is absent — consistent, but
   it means the shipped tool has no on-demand refresh the examples all have.
5. **Artifact prefix has four names:** `artifactStore.options.prefix` · `MESH_ARTIFACT_PREFIX` · `MESH_BLOB_PREFIX` · `MESH_PREFIX`.
6. **Allowed-emails empty = deny everyone, silently** (`Benzene.Mesh.Auth.Oidc/CLAUDE.md`: "empty = deny everyone,
   not an error"). Rule 3: this should be a startup warning naming `MESH_ALLOWED_EMAILS`. The AwsMesh README
   itself (`:451-460`, "this is the single most common cause") documents that users hit it.

**Fix:** (a) make `Benzene.Mesh.Auth.Oidc` accept `AllowedEmailDomains`/`RequiredGroups` and the Host accept
`allowedEmails`, so one policy vocabulary spans both; (b) rename the Terraform variables to the Host's key
names (`mesh_dispatch_max_per_minute_per_identity`, …) and have the Lambda reject a bad value like the Host
does; (c) give the Host a `refresh` section (`refresh.minIntervalSeconds`) mounting `UseMeshRefreshGuard` in
front of `/mesh/aggregate` and passing `refreshUrl` to `UseMeshUi`; (d) one name for prefix (`prefix`).

---

### S9 — SHOULD-FIX · The mesh host is not itself a Cloud Service  `[ceremony · the tool does not take its own steer]`

**§5.2:** "collectors are ordinary Benzene services, so the standard applies to them too".
`deploy/Mesh/Benzene.Mesh.Host/Startup.cs` never calls `UseBenzeneCloudService`, `UseHealthCheck`, or
`UseSpec`; the Helm chart therefore probes with `tcpSocket` (`deployment.yaml:51-60`) instead of
`/benzene/health`; an operator cannot ask the mesh what it serves (`/benzene/spec`) or whether its stores are
reachable. AwsMesh's mesh Lambda likewise (`Startup.cs:74-78` sets only `SetApplicationInfo`).
**Fix:** `asp.UseBenzeneCloudService("benzene-mesh", c => c.WithoutMesh().WithHealthChecks(<artifact store reachability>, <fleet source reachability>))`
at the end of the Host's HTTP pipeline (it is terminal, so it replaces the trailing `asp.UseMessageHandlers()`),
and switch the Helm probes to `httpGet /benzene/health`.

---

### S10 — SHOULD-FIX · Non-HTTP transports re-wire health + spec + handlers by hand; services grow a second envelope endpoint  `[ceremony · missing shorthand]`

**Evidence:**
- `UseBenzeneCloudService` is `where TContext : IHttpContext` (`Extensions.cs:56`). A Lambda's direct-invoke
  surface therefore repeats the profile by hand: `AwsMesh/Shared/MeshServiceWiring.cs:248-251`
  `aws.UseBenzeneMessage(bm => Observe(bm).UseHealthCheck("benzene:healthcheck", healthChecks).UseSpec().UseMessageHandlers(handlers, …))`.
- `K8sMesh/Service/Startup.cs:152-154` hand-mounts a second envelope at `/benzene-message` with its own
  `UseHealthCheck` **and** gets `/benzene/invoke` from the shorthand three lines later; the manifests point
  peers at the non-standard one (`k8s/services.yaml:30` `DOWNSTREAM_MSG_URL=http://payments/benzene-message`).
  Two envelope endpoints, one of them off-standard, in the example meant to show the standard.
- `UseSpecUi("/benzene/spec-ui", "/benzene/spec?type=benzene")` ×2 (AwsMesh `:239`, K8s `:155`) — the two
  paths must be typed because `UseSpecUi`'s defaults are still `/spec-ui`/`/spec` (spec §5.3 lists them as
  1.0 migration candidates).

**Fix:** a `UseBenzeneCloudService` overload for `BenzeneMessageContext` pipelines (health interception +
descriptor + trace + handlers, no HTTP paths) so the Lambda direct-invoke and the raw envelope share the
shorthand; strip the K8s `/benzene-message` mount and point `DOWNSTREAM_MSG_URL` at `/benzene/invoke`; a
`WithSpecUi()` builder option (or `UseSpecUi()` defaults) at `/benzene/spec-ui` for 1.0.

---

### P1 — POLISH · Examples steer users into a shared `instanceId` and an unordered `serviceVersion`

`WithInstanceId(serviceName)` ×4 (AwsMesh `:242`, Google `:66`, AzureFunctions `:88`, K8s `:160`) — every
replica of `orders` reports `instanceId: "orders"`; mesh.md §2/§5 keys heartbeats and hash-mismatch by
instance. `WithServiceVersion("1.0.0")` ×5 with no scheme — `ICloudServiceBuilder` has no `versionScheme`
parameter (`CloudServiceBuilder.cs:18`), so per mesh.md §2.5 every example declares an identity with no order.
**Fix:** drop `WithInstanceId` from the examples (the generated `{name}-{4 hex}` default is right);
add `WithServiceVersion(string version, string versionScheme)` and reject an unparseable pair at wire-up
(§2.5: "MUST be rejected at the point of declaration").

### P2 — POLISH · Host with zero services starts silently useful-empty

With `MESH_CONFIG_PATH` unset and no env overrides, the Host starts, logs `MeshConfigSummary` (good), polls
nothing, and serves an empty dashboard. `MeshConfigLoader.cs:29-32` treats unset as the "legitimate env-var-only
path". **Fix:** when `services.Length == 0 && registryDocuments.Length == 0`, log a **warning** naming both
keys and `MESH_CONFIG_PATH` ("0 services configured — the dashboard will be empty").

### P3 — POLISH · Host relies on all-assembly handler scanning with no start-up listing

`deploy/Mesh/.../Startup.cs:447` `asp.UseMessageHandlers()` → `Benzene.Core.MessageHandlers/Extensions.cs:58-60`
scans `AppDomain.CurrentDomain.GetAssemblies()`. Every example host comments that this scan is dangerous
(collision with the aggregator's handler) and scopes it; the Host depends on it to find
`MeshAggregateMessageHandler`/`MeshReportMessageHandler`/`MeshDispatchMessageHandler`. Rule 3: log the
discovered `(topic, route, handler)` triples at startup next to `MeshConfigSummary`.

### P4 — POLISH · `UseMeshUi(environment:)` exists, is documented, and no host passes it

`docs/mesh-ui.md` documents `environment` ("so a dev mesh and a production mesh aren't identical on screen");
neither `Benzene.Mesh.Host` nor any of the six example hosts passes it, and `mesh.json` has no key for it.
**Fix:** `environment` top-level key in `mesh.json` → `UseMeshUi(environment:)`; Terraform `mesh_environment`
already exists on AwsMesh but feeds `DOTNET_ENVIRONMENT` only.

### P5 — POLISH · Wire-field naming drift to check against the spec (outside this review's remit; filing)

Spec mesh.md §2 names the outbound-registration field **`produces`** and its degradation marker
`"outbound-registry"`; the .NET builder is `WithConsumes(MeshOutboundRegistry)` and `Benzene.Mesh.Wire/CLAUDE.md`
describes a **`consumes`** field. Either the port's naming predates the spec revision or the spec drifted;
`conformance/mesh-descriptor-cases.json` is the arbiter. Filed for the spec owner, not resolved here.

---

## Capability → explicit form → shorthand → documented?

### Service author's seat (rung 3 → 4 → 5)

| Capability | Explicit form (public API) | Shorthand | Documented (names the explicit form?) |
|---|---|---|---|
| Well-known surfaces `/benzene/invoke`, `/benzene/spec`, `/benzene/health` (R3/R4/R5/R7) | `UseBenzeneMessage(new BenzeneMessageHttpOptions { Path }, e => …)` + `UseSpec()` + `UseHealthCheck(topic, checks)` + two `IHttpEndpointDefinition` singletons — `Benzene.CloudService/Extensions.cs:97-100,159-181` | `UseBenzeneCloudService("name")` | **No** — `capability-matrix.md:138,142`, `reference/packages.md:273` only; explicit form named only in the package `CLAUDE.md` (S1) |
| Reserved `benzene:mesh` descriptor (R6a) | `MeshDescriptorFactory.Create(lookUp, info, outbound)` + `UseMeshDescriptor(descriptor)` (`Mesh.Wire/Extensions.cs:25`) | included | `CLAUDE.md` only |
| Trace feed (R6d, R8) | `new HttpMeshTraceExporter(http, url)` + `UseMeshTrace(info, exporter, new BenzeneMessageMeshStatusReader())` (`:63`) | `.WithCollector(url)` / `.WithTraceExporter(x)` | `CLAUDE.md` only; `WithCollector` 0 docs hits |
| Register + heartbeat (R6b/c) | **none public** — `MeshAnnouncer` internal; hand-rolled in `examples/Mesh/Shared/EnvelopeHost.cs:106-151` | `.WithCollector(url)` | No (B3) |
| Issue feed (mesh §4.1) | `UseMeshIssues(info, exporter, reader)` (`:139`) | none (`ICloudServiceBuilder` has no `WithIssues`) | `CLAUDE.md` |
| Outbound registration (§2.3) | `new MeshOutboundRegistry().Register<T>(topic, version)` | `.WithConsumes(registry)` | `CLAUDE.md`; naming vs spec: P5 |
| Profile self-report | none (`CloudServiceProfileReport.Evaluate` internal) | automatic; `.WithProfileReport(cb)` | spec profile §5 ✓ |
| Relocate a surface / decline mesh | `.WithInvokePath/.WithSpecPath/.WithHealthPath`, `.WithoutMesh()` | ✓ (flagged honestly in report) | `CLAUDE.md` only |
| Placement | `.WithPlacement(cloud, region)` | none (default `self-hosted`; each example re-derives from a different env var) | — (S2) |
| Rung-2 (middleware-only) reduced participation | `UseMeshTrace` on any pipeline ✓ public | n/a by design (shorthand is rung 3+) | **Not documented with code** (S1); heartbeat impossible without B3 |
| Legacy self-report (pull-less transports) | `AddMeshHttpReporting(o)` + `AddMeshSelfReport(o)` + `UseMeshSelfReport()` — three calls, order-sensitive, publisher resolution fails at DI time | none | `Reporting/CLAUDE.md`, `deploy/Mesh/README.md` |
| Spec UI at the §5 path | `UseSpecUi("/benzene/spec-ui", "/benzene/spec?type=benzene")` | none (defaults still `/spec-ui`) | `spec-ui.md` (pre-standard paths) |
| Non-HTTP transport (direct invoke / queue) profile | hand-wired `UseHealthCheck + UseSpec + UseMessageHandlers` (`AwsMesh/Shared/MeshServiceWiring.cs:248-251`) | none (S10) | — |

### Platform operator's seat

| Capability | Explicit form | Shorthand | Documented? |
|---|---|---|---|
| Pull-mode mesh (poll spec/health, render catalog) | `AddMeshAggregator(registry, dir)` + poll loop + `UseMeshUi` + `UseMeshSpecUi` + `UseMeshArtifacts`/`UseStaticFiles` + `UseMessageHandlers` | `Benzene.Mesh.Host` + `mesh.json` `services[]` | ✓ `deploy/Mesh/README.md`, `CONFIG.md` (excellent) |
| Receive push feeds (register/heartbeat/traces/issues) | `AddSingleton<MeshCollectorStore>` + `AddSingleton<IMeshFleetReadModel>` + `UseBenzeneMessage("/benzene/invoke", c => c.UseMessageHandlers(MeshCollectorHandlers.All))` (`K8sMesh/Mesh/Startup.cs:80-81,111-112`) | **none** (B2) | `mesh-ui.md` ✓ as code, not as config |
| Live fleet plane from a trace store | `Add{XRay,Tempo,Jaeger}FleetReadModel(o)` + queries envelope + `UseMeshUi(envelopeUrl)` | `fleet.source` | ✓ `CONFIG.md` |
| Discovery | `AddMesh{AwsLambda,Azure,Kubernetes}Discovery()` + `MeshDiscoveryRunner` + `MeshAggregationPass` + hosted service + refresh handler | none **by stated design** (Host refuses; `registryDocuments` + `../Discovery`) | ✓ README states the decision plainly — this is the good kind of "no shorthand" |
| On-demand refresh + guard | handler + `UseMeshRefreshGuard(o)` + `UseMeshUi(refreshUrl)` | none; Host lacks it entirely (S8-4); examples hand-roll ×5 (S3) | `mesh-ui.md` names `UseMeshRefreshGuard` ✓ |
| Dispatch / Test Console | `UseMeshDispatchGuard(o)` + `UseMeshDispatch(opts)` + `UseBenzeneMessage(Path=o.Path, TopicFilter)` + registry in DI + `UseMeshUi(dispatchUrl)` | `dispatch.enabled` (Host; broken by B1) | `mesh-ui.md` names only `UseMeshDispatch` (S6) |
| Auth | Host: `auth.mode` (ASP.NET); Lambda: `UseMeshOidcAuth(options)` (own package) | two parallel implementations (S8-1) | ✓ `CONFIG.md` matrix / `Oidc/CLAUDE.md` |
| Usage feed | `AddCloudWatchUsage(o)` / `AddApplicationInsightsUsage(o)` | `usage[]` | ✓ `mesh-usage-feed.md` |
| Artifact store | `AddMeshAggregatorWith{S3,Blob,Gcs}(…)` | `artifactStore.type` | ✓ `CONFIG.md` |
| Validate before deploy | — | `benzene-mesh --validate-config` (same rule set as startup) | ✓ README |
| Mesh host as a Cloud Service (§5.2) | `UseBenzeneCloudService(...)` | not applied to the Host (S9) | — |

---

## Config-parity table (Docker host `mesh.json` · Helm · AwsMesh Lambda · other example hosts)

Legend: ✓ same key/meaning as the Host · ≠ different name or semantics · — absent · (code) fixed in code, not configurable

| Concern | `Benzene.Mesh.Host` (`mesh.json`) | Helm chart | AwsMesh Lambda (Terraform var → env) | AzureMesh / AzureFunctionsMesh / GoogleCloudMesh / K8sMesh |
|---|---|---|---|---|
| Service list | `services[]{name,specUrl,healthUrl,source,sourceOptions,owningTeam}` + `registryDocuments[]` | ✓ verbatim | ≠ discovery (code) + `mesh_extra_services` → `MESH_EXTRA_SERVICES` (same JSON shape, delivered as a blob) | discovery (code) / discovery / `MESH_{NAME}_URL` base URLs / discovery |
| Artifact store | `artifactStore.type` = file/s3/azureBlob/gcs + `options.bucket/prefix/blobServiceUri/container` | ✓ | ≠ `MESH_ARTIFACT_BUCKET`, `MESH_ARTIFACT_PREFIX` | ≠ `MESH_BLOB_URI`/`MESH_BLOB_CONTAINER`/`MESH_BLOB_PREFIX` · same · `MESH_BUCKET`/`MESH_PREFIX` · `MESH_ARTIFACT_DIR` |
| Poll cadence | `pollIntervalSeconds` (default 60; ≤0 silently → 1) | ✓ | ≠ `aggregate_schedule` (EventBridge, default `rate(15 minutes)`) | 30 s hard-coded ×2 / timer trigger / Cloud Scheduler / 30 s |
| Well-known service paths | sample uses `/spec?type=benzene`, `/healthcheck` (pre-§5) | same (pre-§5) | Lambda-Invoke (n/a) | Azure discovery + K8s discovery emit `/benzene/*` ✓; Google derives `/benzene/*` ✓; **compose/mesh.json uses `/benzene/*`** ✓ — only the Host's own samples are off-standard |
| Usage feed | `usage[].source` + `options.windowHours` | ✓ | ≠ `usage_window_hours` → `MESH_USAGE_WINDOW_HOURS` (CloudWatch fixed in code) | `MESH_LOG_ANALYTICS_WORKSPACE_ID` + `MESH_USAGE_WINDOW_HOURS` (Azure ×2) / — / — |
| Fleet plane | `fleet.source` none/xray/tempo/jaeger (+ options) — **no push collector** | ✓ | X-Ray fixed in code | — / — / — / push collector fixed in code |
| Dispatch on/off | `dispatch.enabled` (default **off**) + `dispatch.allowInProduction` | ✓ | ≠ always wired; gated only by `mesh_environment` → `DOTNET_ENVIRONMENT` (default `"Development"` ⇒ **on**) | — (none wire dispatch) |
| Dispatch limits | `dispatch.maxPerMinutePerIdentity`, `.maxPerMinutePerTarget`, `.maxRequestBytes` (≤128 KiB), `.maxResponseBytes` — bad values **rejected** | ✓ | ≠ `mesh_dispatch_max_per_minute` → `MESH_DISPATCH_MAX_PER_MINUTE`; `mesh_dispatch_max_per_target_per_minute` → `MESH_DISPATCH_MAX_PER_TARGET_PER_MINUTE`; no request/response caps; bad values **silently ignored** | — |
| Dispatch role | `auth.dispatchRole` (needs group-bearing mode) | ✓ | — (not expressible) | — |
| Refresh guard/throttle | **none** (no `/mesh/refresh`; `/mesh/aggregate` unguarded) | ✓ (same gap) | `refresh_min_interval_seconds` → `MESH_REFRESH_MIN_INTERVAL_SECONDS` + API-GW throttles | `MESH_REFRESH_MIN_INTERVAL_SECONDS` ×4 |
| Auth mode | `auth.mode` none/proxy/basic/oidc | ✓ + `existingSecretName` | ≠ OIDC only, different package (`Benzene.Mesh.Auth.Oidc`) | none ×4 (documented "demo-only posture") |
| OIDC settings | `auth.oidc.authority/clientId/clientSecretEnvVar(MESH_OIDC_CLIENT_SECRET)/callbackPath(/signin-oidc)/scopes/requireHttpsMetadata` | ✓ | ≠ issuer fixed in code (Google); `GOOGLE_OAUTH_CLIENT_ID`, `GOOGLE_OAUTH_CLIENT_SECRET`, `MESH_OIDC_SIGNING_KEY`; routes `/mesh/auth/{login,callback,logout}` | — |
| Authorisation | `auth.allowedEmailDomains[]` (domains), `auth.requiredGroups[]` | ✓ | ≠ `mesh_allowed_emails` → `MESH_ALLOWED_EMAILS` (exact emails; empty = deny all, silent) | — |
| Logout | `POST /mesh/auth/logout` + `X-Benzene-Logout` | ✓ | same path/header ✓ (converged) | — |
| Ingestion secret | `auth.ingestion.mode` + `MESH_INGEST_SECRET` | ✓ | — (`/mesh/report` not mounted) | — |
| Environment label (`UseMeshUi(environment:)`) | — (never passed) | — | — (`mesh_environment` feeds `DOTNET_ENVIRONMENT` only) | — |
| Secrets discipline | env-var names only in config ✓ | Secret via `envFrom` ✓ | Terraform `sensitive` + GH Environment secrets ✓ | ✓ |
| Validate before deploy | `--validate-config` ✓ | `helm template` + `--validate-config` on the rendered JSON (not documented as a step) | `terraform validate` + `validation {}` blocks on 2 of 14 vars | — |

**Do config mistakes fail at start-up naming the key?**
- Host: **yes** for unknown `type`/`source` names (lists valid values), missing required options
  (`RequireOption`), auth satisfiability (`MeshAuthGate.Validate`, every message names the key and mode),
  dispatch/fleet bounds (`ValidateInRange`), `MESH_CONFIG_PATH` set-but-missing, `registryDocuments` all-unreadable.
  **No** for `services[].specUrl/healthUrl/name` (S4), duplicate names, `pollIntervalSeconds ≤ 0`,
  `sourceOptions.functionName` (first invoke), and the dispatch-URL mismatch (B1).
- Lambda: **yes** for `MESH_ARTIFACT_BUCKET`, `GOOGLE_OAUTH_*`, `MESH_OIDC_SIGNING_KEY` (throw with the name),
  malformed `MESH_EXTRA_SERVICES` (throws naming the shape), `MeshOidcOptions.Validate` (https issuer, key
  entropy). **No** for empty `MESH_ALLOWED_EMAILS` (deny-all, silent), negative dispatch limits (ignored).
- Service side (`UseBenzeneCloudService`): **yes** only for an empty service name; **no** for the collector URL (S5).

---

## What is genuinely good (keep these; they are the pattern the fixes should copy)

- **`--validate-config` runs the identical registrar the host runs** (`MeshConfigValidator.cs:28-54`,
  `MeshSourceRegistrar` remarks) — one rule set, no drift possible. Every unknown name lists its valid values
  (`Unknown()`); every missing option names its key (`RequireOption`); every ceiling names the key and the
  range (`ValidateInRange`). This is rule 3 done properly and should be the template for S4.
- **`MeshAuthGate.Validate`'s satisfiability matrix** (`CONFIG.md` "Which options work under which auth modes")
  — "no inert options" is exactly the anti-magic stance §4.1 asks for, and it is enforced, not just documented.
- **`MeshConfigSummary` logged once at startup with secret-shaped keys redacted** — the operator can see what the
  framework did on their behalf. Extend it (P2, P3) rather than replace it.
- **Helm `meshConfig` is `mesh.json` verbatim** — parity by construction; `NOTES.txt` names the exact env vars a
  chosen auth mode needs.
- **`UseBenzeneCloudService` is composed, not parallel**: every rung it lands on (`UseBenzeneMessage`, `UseSpec`,
  `UseHealthCheck`, `UseMeshTrace`, `UseMessageHandlers`) is public, and the profile report reflects overrides
  honestly ("relocated → flagged, never refused"; R8 tied to the *actual* exporter, `Extensions.cs:68-79`).
  With B3 fixed it is a model shorthand.
- **`UseMeshUi`'s per-feature opt-ins** are separate parameters, each documented in a table with what it turns
  on and what server-side call it pairs with (`mesh-ui.md`) — the ladder is visible from the top there.
- **`Benzene.Mesh.Artifacts`' origin note** ("extracted from five near-identical copies … the K8sMesh copy had
  drifted") and `MeshAggregationPass` ("the drift that made this seam worth extracting") — the repo already
  knows duplication is a framework bug and has the muscle to fix S2/S3 the same way.
- **The Host's refusal to embed discovery** is a design decision stated as one, with a test
  (`NoDiscoveryInVanillaHostTest.cs`) — the right way to have no shorthand.
- **Safe-by-default dispatch** (`AllowInProduction=false`, unset environment = Production, CSRF header,
  fail-closed identity, per-identity and per-target limits, actual-bytes request cap) — the *policy* is right;
  only the *wiring* (B1, S6) lets it down.
- **`Benzene.CloudService.Probe`'s tri-state verdicts** — an external check that refuses to overclaim is the
  operator's start-up check for a service they do not own.

---

## What I could not do

- Build or run anything (no .NET SDK). B1 rests on two constants and a `TopicFilter`; B2 on the absence of
  any `collector` registration path in the Host; S7's 403 on the guard's header check. If the K8s workflows
  are green today, the guard is not on that path and S7 row 1 flips to "the guard is not mounted where the
  Startup says it is" — either way a defect.
- Verify P5 against the conformance fixture (spec-repo action, not this port's).
- Count lines in `docs/` rendered site navigation — S1's "0 guide pages" is a grep over `docs/**/*.md`.
