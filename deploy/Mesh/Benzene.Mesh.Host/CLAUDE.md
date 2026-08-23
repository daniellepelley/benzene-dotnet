# Benzene.Mesh.Host

## What this package does
A config-driven, Docker/Compose-deployable Benzene Mesh Aggregator + UI - Phase D of
`work/service-mesh-roadmap-1.0.md`'s multi-transport data collection work. Lets a developer run the
mesh dashboard against their own real services during local development (`docker-compose up`
alongside their other infra), rather than only against `examples/Mesh/`'s demo/fake data. See
`../README.md` for the config shape, Docker/Compose usage, and publishing.

## Key types
- `Startup` - mirrors `examples/Mesh/Benzene.Examples.Mesh.Aggregator/Startup.cs`'s wiring shape
  (static-file mount at `/artifacts`, `UseMeshUi` + `UseMeshSpecUi` so the per-service "spec"
  drill-in resolves, `.UseMessageHandlers()`), but binds `MeshHostConfig` from `IConfiguration`
  (constructor-injected) instead of hardcoded wiring, delegates every section's registration to
  `MeshSourceRegistrar` (config schema v1), and additionally wires `AddMeshLambdaSource()` and a
  `MeshPollBackgroundService` hosted service. Tracks whether `RegisterFleet` registered a live plane
  (`_fleetEnabled`) so `Configure()` knows whether to point the mesh UI's `envelopeUrl` at
  `/benzene/invoke` and wire the inner `MeshCollectorHandlers.Queries` pipeline there, and whether
  `ArtifactStore.Type == "file"` so it knows whether to mount `UseStaticFiles`/`PhysicalFileProvider`
  (local disk) or `Benzene.Mesh.Artifacts.UseMeshArtifacts()` (any other configured store). Its
  constructor also builds the effective `MeshServiceRegistry` (`BuildRegistryEntries`,
  `BuildArtifactStoreForReading` - `work/enterprise/slice-3-discovery.md` task 3.1):
  `MeshHostConfig.RegistryDocuments` (if any) are read back through a throwaway `IMeshArtifactStore`
  matching `ArtifactStore`'s backend and unioned with `Services`, with `Services` always winning a
  name clash. The read is synchronous (`.GetAwaiter().GetResult()`) because it has to complete inside
  the constructor, before the registry becomes a fixed DI singleton for the rest of the host's life -
  it is not re-read per poll. This is the *read* half of the discovery seam; the *write* half
  (`Benzene.Mesh.Discovery.Host`, a separate deployable under `deploy/Discovery/`) shares no code
  with this class - see "Dependencies on other Benzene packages" below for why that separation is
  load-bearing, not incidental.
- `MeshHostConfig`/`MeshHostServiceConfig` - the `mesh.json` binding shape (mutable properties, for
  `IConfiguration.Get<T>()` - this repo's first use of that pattern to bind a *list* of objects, see
  `work/service-mesh-roadmap-1.0.md`'s Phase D note). `MeshHostServiceConfig.ToEntry()` converts to
  the immutable `Benzene.Mesh.Contracts.MeshServiceRegistryEntry` the rest of the mesh feature uses,
  including the optional `OwningTeam` (the "who do I talk to" field the UI renders).
  `MeshHostConfig.RegistryDocuments` (paths resolved through `ArtifactStore`) is the discovery-output
  read list `Startup` unions with `Services` - see above.
- `MeshHostConfigSections.cs` (`MeshArtifactStoreConfig`, `MeshUsageSourceConfig`, `MeshFleetConfig`,
  `MeshTopologyConfig`, `MeshDispatchConfig`, `MeshAuthConfig`) - config schema v1's section types,
  all mutable/defaulted for the same binder reason as `MeshHostConfig` itself. Every source-selecting
  section (`artifactStore`, `usage[]`, `fleet`, `topology`) models its backend-specific settings as a
  loose `Dictionary<string, string>? Options`, matching `MeshHostServiceConfig.SourceOptions`'s
  existing precedent - one shape to learn, and no new C# type needed every time a source is added.
- `MeshSourceRegistrar` - the one place a config section's `source`/`type` name is mapped to the
  matching Benzene package's `Add*` call (a plain `switch` over lowercased names, deliberately not
  reflection-driven). **Never binds `mesh.json` directly to a `src/` options class** - every options
  class in the mesh binds inconsistently (some ctor-only, some settable, one normalizes its URL in
  the constructor - see `work/enterprise/slice-1-config-schema.md`'s cheat sheet), so this file
  builds each one from the mirror config POCOs above instead. An unknown name throws
  `InvalidOperationException` naming the valid values; a missing required option throws naming the
  missing key. Both `Startup` and `MeshConfigValidator` call the same methods, so the running host
  and `--validate-config` cannot silently disagree about what a config means.
- `MeshConfigValidator` - backs `--validate-config` (see `Program.cs`): binds `mesh.json` and runs
  every `MeshSourceRegistrar` registration against a throwaway container, so a config mistake surfaces
  before a deploy rather than after one. No network call happens either way - every adapter package
  registers its cloud client lazily. Also runs `MeshAuthGate.Validate` (work/enterprise/slice-2-auth.md),
  the same fail-fast auth checks `Startup.Configure` runs.
- `MeshAuthGate` - the single ASP.NET Core middleware every auth mode (`none`/`proxy`/`basic`/`oidc`)
  goes through, registered in `Startup.Configure` immediately after `app.UseRouting()` and before
  `app.UseStaticFiles(...)` so it covers both artifact-serving branches (the file store's
  `UseStaticFiles` mount, entirely outside the Benzene pipeline, and the non-file stores'
  `UseMeshArtifacts()` inside it) with one placement - see its own remarks for why. On success it sets
  both `Benzene.Auth.Core.AuthenticationHolder.Principal` (resolved off `HttpContext.RequestServices`)
  AND `context.User`. Only the latter actually crosses into the Benzene pipeline below: `UseBenzene`
  (the embedding overload `Startup.Configure` uses) resolves the pipeline's handlers/middleware
  through a SEPARATE, cloned `IServiceProvider`, so a scoped type like `AuthenticationHolder` set via
  `RequestServices` is a different instance inside the pipeline - a downstream check reading it would
  see no principal at all. `context.User`, via `IHttpContextAccessor`'s process-wide `AsyncLocal`
  backing store, is visible from any container that resolves it, including the pipeline's own cloned
  one - see `MeshAuthGate`'s own class remarks for the full explanation. Also decides `auth.ingestion`
  for `/mesh/report` - exempt from every `auth.mode` above, since a self-reporting service is not a
  browser session. `EnvBasicAuthCredentialValidator` (same file) is mode `basic`'s
  `Benzene.Auth.Basic.IBasicAuthCredentialValidator`, backed by `MESH_BASIC_USER`/`MESH_BASIC_PASSWORD`.
  `auth.dispatchRole`, when set, is enforced here too - directly against `HttpContext`, matched to
  `MeshAuthGate.DispatchPath` (`mesh:dispatch`'s fixed, well-known envelope path) - not as an
  `AuthorizationExtensions.RequireRole` on the envelope itself, since that pipeline's own
  freshly-created DI scope never sees the `AuthenticationHolder` this gate populates.
- `MeshPollBackgroundService : BackgroundService` - runs `MeshAggregator.RunOnceAsync` on a timer
  (`MeshHostConfig.PollIntervalSeconds`) - new capability local to this Host app only, since a bare
  Compose deployment has no external scheduler the way a real deployment's `mesh:aggregate`
  invocation-trigger assumes. A failed pass is logged and does not stop future passes or crash the
  host.
- `Program.cs` - `Host.CreateDefaultBuilder(args)` with an extra `ConfigureAppConfiguration` step
  that delegates to `MeshConfigLoader.ConfigureMeshConfig`, layered on top of
  `Host.CreateDefaultBuilder`'s own default sources (env vars, etc.). `--validate-config` short-circuits
  before any of that: it calls `MeshConfigValidator.Validate` directly and exits. After `Build()`
  and before `host.Run()` it also logs `MeshConfigSummary.Format` of the resolved `MeshHostConfig`
  once, at `Information` - work/enterprise/slice-5-packaging.md task 5.1, the single highest-value
  support tool in the package ("it isn't picking up my Tempo URL" is otherwise unanswerable without
  a debugger).
- `MeshConfigLoader` - loads `MESH_CONFIG_PATH` (if set) as an additional JSON config source.
  Pulled out of `Program.cs`'s top-level statements so it's directly unit-testable
  (`Benzene.Mesh.Host.Test`). A **set but missing** path throws `FileNotFoundException` naming the
  path at startup, rather than silently starting an empty mesh (`optional: true`'s old failure mode)
  - unset stays a no-op, the legitimate env-var-only local-dev path this package's README documents.
- `MeshConfigSummary` - formats a `MeshHostConfig` as the multi-line, human-readable summary
  `Program.cs` logs at startup (above). Redacts by key name across every options dictionary
  (`Services[].SourceOptions`, `ArtifactStore.Options`, `Usage[].Options`, `Fleet.Options`,
  `Topology.Options`): any key containing `password`/`secret`/`token`/`key`/`credential`/
  `connectionstring` (case-insensitive) prints its value as `***` - the key itself always prints, so
  an operator can see a value was supplied without seeing what it was. A second line of defence, not
  the primary one: credentials should never reach `mesh.json` at all (see `../README.md`'s house
  rule) - this covers a value pasted in against that advice, or a connection string that embeds one.

## Deviations from the original design sketch
- **No `selfReportIngestion.enabled` config toggle.** The original design considered gating whether
  the push ingestion endpoint (`Benzene.Mesh.Aggregator.MeshReportMessageHandler`) is reachable via
  a config flag. In practice, Benzene's reflection-based `.UseMessageHandlers()` discovers every
  `[Message]`/`[HttpEndpoint]`-attributed handler in every *referenced* assembly - since this Host
  must reference `Benzene.Mesh.Aggregator` for its core aggregation functionality anyway, the
  ingestion endpoint is unavoidably discovered and reachable at `/mesh/report` the same way
  `/mesh/aggregate` always is. Gating it would need an explicit `.UseMessageHandlers(types: ...)`
  allow-list instead of the default assembly-scan, judged not worth the added complexity for v1 -
  flagged here rather than silently dropped.

## Opt-in live dispatch (off by default)
`MeshHostConfig.Dispatch.Enabled` (default false) wires `Benzene.Mesh.Dispatch`'s `UseMeshDispatch()`
+ `AddMeshLambdaDispatcher()` and registers the `MeshServiceRegistry` so the `mesh:dispatch` handler
can invoke a registered service's **real** handler with a chosen payload (the mesh UI composer's
"send" leg). It's a deliberate, non-default choice because real side-effects execute. Two gates, both
must pass: `Dispatch.Enabled` (this wiring) **and** the runtime environment gate — dispatch is
refused in a Production environment unless `Dispatch.AllowInProduction` is *also* set (an unset
environment counts as Production). Because `MeshDispatchMessageHandler` carries no `[Message]`
attribute, the default `.UseMessageHandlers()` scan does **not** expose it — unlike `/mesh/report`,
it is genuinely absent until `Dispatch.Enabled` is set.

**Fixed 2026-08-22 (was previously a reachability gap, flagged and left for a later pass):**
`UseMeshDispatch()` alone only registers the handler *definition* into DI - it needs its own
reachable endpoint, the same way the fleet-query plane does. `Startup.Configure` now gives it one: a
dedicated `UseBenzeneMessage` envelope at `MeshDispatchGuardOptions`'s default path
(`/mesh/dispatch`), with `UseMeshDispatchGuard` mounted directly ahead of it (CSRF header, fail-closed
identity, payload bound, per-identity rate limit). `auth.dispatchRole` is enforced against that same
path by `MeshAuthGate` (see its doc entry above) - both `MeshAuthGate.DispatchPath` and the envelope's
guard read the same `MeshDispatchGuardOptions` instance, so the two can never drift apart on what path
they mean.

## Dependencies on other Benzene packages
Config schema v1 pulls in every `Benzene.Mesh.*` adapter package this host can select at runtime -
see `Benzene.Mesh.Host.csproj` for the full list. **Deliberately absent: any
`Benzene.Mesh.Discovery.*` package** - this host must stay physically incapable of enumerating a
cloud account (see `../README.md`). The ones worth calling out by name:
- **Benzene.AspNet.Core**, **Benzene.Microsoft.Dependencies** - the ASP.NET Core host wiring.
- **Benzene.Auth.Basic** (transitively **Benzene.Auth.Core**) - `IBasicAuthCredentialValidator`
  (`EnvBasicAuthCredentialValidator` implements it) and `AuthenticationHolder`, reused by
  `MeshAuthGate` rather than rebuilt. `Microsoft.AspNetCore.Authentication.OpenIdConnect` (NuGet, not
  a Benzene package) is `auth.mode: "oidc"`'s cookie + authorization-code wiring.
- **Benzene.Mesh.Aggregator** - `AddMeshAggregator`, `MeshAggregator`, `MeshServiceRegistry`.
- **Benzene.Mesh.Artifacts** - `UseMeshArtifacts()`, serving the catalog from any non-filesystem
  `IMeshArtifactStore` (`UseStaticFiles`/`PhysicalFileProvider` only ever covers the `file` case).
- **Benzene.Mesh.Aws.Lambda** - `AddMeshLambdaSource`, for `AwsLambdaInvoke`-sourced entries.
- **Benzene.Mesh.Aws.S3** / **Benzene.Mesh.Azure.Blob** / **Benzene.Mesh.GoogleCloud.Storage** - the
  `artifactStore` backends.
- **Benzene.Mesh.Usage.CloudWatch** / **Benzene.Mesh.Usage.ApplicationInsights** - the `usage`
  sources.
- **Benzene.Mesh.Fleet.Aws.XRay** / **Benzene.Mesh.Fleet.Tempo** / **Benzene.Mesh.Fleet.Jaeger** -
  the `fleet` sources.
- **Benzene.Mesh.Tracing.Tempo** - the `topology` source (`AddTempoTopology`).
- **Benzene.Mesh.Collector** - `MeshCollectorHandlers.Queries`, `IMeshFleetReadModel`, the fleet
  read-side wiring `fleet`-enabled configs use.
- **Benzene.Mesh.Ui** - `UseMeshUi`, `UseMeshSpecUi`, the dashboard itself.

## Packaging (work/enterprise/slice-5-packaging.md)
Three ways to consume this package beyond building it from source, all documented in `../README.md`:
the Docker image (`Dockerfile`, unchanged by slice 5), a `dotnet tool` (`Benzene.Mesh.Host.csproj`'s
`PackAsTool`/`ToolCommandName: benzene-mesh` - `Microsoft.NET.Sdk.Web` sets `IsPackable` false by
default, unlike the plain-SDK precedents `templates/Benzene.Templates.csproj`/
`tools/Benzene.Descriptor.csproj` use, so that has to be turned back on explicitly), and a Helm
chart (`../helm/benzene-mesh/`, for Kubernetes - mounts the same `mesh.json` shape as a ConfigMap
volume instead of a bind mount, models the wiring on
`examples/K8sMesh/compose/docker-compose.yml`'s `mesh` service). `.github/workflows/deploy-mesh-host.yml`
publishes the image and the tool from two independent jobs, both gated by the same manual
`workflow_dispatch`/built-in-`GITHUB_TOKEN` pattern the image job already used. `../CONFIG.md` is the
full `mesh.json` schema reference every one of these three paths reads the same way; the
per-source least-privilege permission matrix lives there too (moved out of `../README.md`, not
duplicated).

## Why this isn't part of `Benzene.sln`/`Benzene.Examples.sln`
See `../README.md`'s own section on this - same reasoning as `templates/Benzene.Templates.sln`.

## Tests
`../Benzene.Mesh.Host.Test/` (xUnit), added to `Benzene.Mesh.Host.sln` (not `test/Benzene.Mesh.Test/`
or `Benzene.sln` - a `ProjectReference` to this host from there would pull the host into
`Benzene.sln`'s build graph, contradicting the independent-lifecycle reasoning above). It references
this project directly and exercises `MeshConfigLoader`, `MeshHostServiceConfig.ToEntry()`,
`MeshHostConfig` binding (`MeshHostConfigTest`), every `MeshSourceRegistrar` source/type name and its
fail-fast paths (`MeshSourceRegistrarTest`), `MeshConfigValidator` (`MeshConfigValidatorTest`),
`registryDocuments`' union/precedence/failure rules (`StartupRegistryDocumentsTest`), the AwsMesh
capability-for-capability acceptance test (`AwsMeshParityTest`, which also asserts - at the assembly
level - that this host references no `Benzene.Mesh.Discovery.*` package; the exhaustive transitive
version of that check is `NoDiscoveryInVanillaHostTest`), and `MeshConfigSummary`'s redaction rule
(`MeshConfigSummaryTest` - a config with a secret-shaped option key prints the key, never the value).
Every AWS/Azure/GCS-backed assertion checks a *registration*, never a resolved cloud client - see
`AwsMeshParityTest`'s remarks for why (constructing an unconfigured AWS/GCS SDK client throws
immediately in any environment without an ambient region/ADC, which would make the test's pass/fail
depend on the CI runner's environment rather than on this code).

`MeshAuthGateTest` (unit-level, `MeshAuthGate` invoked directly against a `DefaultHttpContext` - no
Kestrel needed for modes `proxy`/`basic`, the ingestion path, or oidc's "already authenticated"
branch) and `MeshAuthAcceptanceTest` (slice 2's task 2.7 acceptance test - boots the *real* `Startup`
on a real Kestrel-hosted pipeline via `UseStartup<Startup>()`, the same as `Program.cs`, and proves an
unauthenticated request to every one of `/mesh-ui`, `/mesh-spec-ui.html`, the manifest, and a service
artifact is refused in every non-`none` mode, against **both** artifact-store branches). See
`MeshAuthAcceptanceTest`'s remarks for how it avoids needing a real OIDC provider or AWS credentials.
