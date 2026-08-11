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
  (local disk) or `Benzene.Mesh.Artifacts.UseMeshArtifacts()` (any other configured store).
- `MeshHostConfig`/`MeshHostServiceConfig` - the `mesh.json` binding shape (mutable properties, for
  `IConfiguration.Get<T>()` - this repo's first use of that pattern to bind a *list* of objects, see
  `work/service-mesh-roadmap-1.0.md`'s Phase D note). `MeshHostServiceConfig.ToEntry()` converts to
  the immutable `Benzene.Mesh.Contracts.MeshServiceRegistryEntry` the rest of the mesh feature uses,
  including the optional `OwningTeam` (the "who do I talk to" field the UI renders).
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
  registers its cloud client lazily.
- `MeshPollBackgroundService : BackgroundService` - runs `MeshAggregator.RunOnceAsync` on a timer
  (`MeshHostConfig.PollIntervalSeconds`) - new capability local to this Host app only, since a bare
  Compose deployment has no external scheduler the way a real deployment's `mesh:aggregate`
  invocation-trigger assumes. A failed pass is logged and does not stop future passes or crash the
  host.
- `Program.cs` - `Host.CreateDefaultBuilder(args)` with an extra `ConfigureAppConfiguration` step
  that delegates to `MeshConfigLoader.ConfigureMeshConfig`, layered on top of
  `Host.CreateDefaultBuilder`'s own default sources (env vars, etc.). `--validate-config` short-circuits
  before any of that: it calls `MeshConfigValidator.Validate` directly and exits.
- `MeshConfigLoader` - loads `MESH_CONFIG_PATH` (if set) as an additional JSON config source.
  Pulled out of `Program.cs`'s top-level statements so it's directly unit-testable
  (`Benzene.Mesh.Host.Test`). A **set but missing** path throws `FileNotFoundException` naming the
  path at startup, rather than silently starting an empty mesh (`optional: true`'s old failure mode)
  - unset stays a no-op, the legitimate env-var-only local-dev path this package's README documents.

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

## Dependencies on other Benzene packages
Config schema v1 pulls in every `Benzene.Mesh.*` adapter package this host can select at runtime -
see `Benzene.Mesh.Host.csproj` for the full list. **Deliberately absent: any
`Benzene.Mesh.Discovery.*` package** - this host must stay physically incapable of enumerating a
cloud account (see `../README.md`). The ones worth calling out by name:
- **Benzene.AspNet.Core**, **Benzene.Microsoft.Dependencies** - the ASP.NET Core host wiring.
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

## Why this isn't part of `Benzene.sln`/`Benzene.Examples.sln`
See `../README.md`'s own section on this - same reasoning as `templates/Benzene.Templates.sln`.

## Tests
`../Benzene.Mesh.Host.Test/` (xUnit), added to `Benzene.Mesh.Host.sln` (not `test/Benzene.Mesh.Test/`
or `Benzene.sln` - a `ProjectReference` to this host from there would pull the host into
`Benzene.sln`'s build graph, contradicting the independent-lifecycle reasoning above). It references
this project directly and exercises `MeshConfigLoader`, `MeshHostServiceConfig.ToEntry()`,
`MeshHostConfig` binding (`MeshHostConfigTest`), every `MeshSourceRegistrar` source/type name and its
fail-fast paths (`MeshSourceRegistrarTest`), `MeshConfigValidator` (`MeshConfigValidatorTest`), and
the AwsMesh capability-for-capability acceptance test (`AwsMeshParityTest`). Every AWS/Azure/GCS-backed
assertion checks a *registration*, never a resolved cloud client - see `AwsMeshParityTest`'s remarks
for why (constructing an unconfigured AWS/GCS SDK client throws immediately in any environment without
an ambient region/ADC, which would make the test's pass/fail depend on the CI runner's environment
rather than on this code).
