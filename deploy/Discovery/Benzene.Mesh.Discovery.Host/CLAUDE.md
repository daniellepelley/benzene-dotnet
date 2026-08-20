# Benzene.Mesh.Discovery.Host

## What this package does
The **separate deployable** half of mesh self-discovery
(`work/archive/mesh-self-discovery-design-2026-07.md`, `work/enterprise/slice-3-discovery.md`): a one-shot job that
enumerates a cloud account/cluster (filtered by tag/label), writes what it found as a
`mesh.json`-shaped registry document to a shared artifact store, and exits. It never serves traffic
and it never invokes a discovered service - see `../README.md` for the full "why a separate
deployable" argument. `deploy/Mesh/Benzene.Mesh.Host`'s `registryDocuments` config
(`work/enterprise/slice-3-discovery.md` task 3.1) is the *read* side of the seam this job writes.

## Key types
- `Program.cs` - top-level statements, no `Host.CreateDefaultBuilder`, no ASP.NET Core: builds
  config, runs every configured `IMeshDiscoveryProvider` via `MeshDiscoveryRunner`, serializes the
  result with `MeshRegistryJson.Serialize`, writes it through the configured `IMeshArtifactStore`, and
  returns a process exit code (`0` success, `1` on any exception) - the only contract a scheduler
  (an EventBridge rule, a Cloud Scheduler job, a Kubernetes `CronJob`) needs.
- `DiscoveryHostConfig`/`DiscoveryFilterConfig`/`DiscoveryArtifactStoreConfig` - the `discovery.json`
  binding shape (mutable properties, for `IConfiguration.Get<T>()` - the same binder-driven style as
  `Benzene.Mesh.Host.MeshHostConfig`, copied rather than shared). `Providers` names which discovery
  adapters to run; `Filter` surfaces the tag key/regions/namespace `MeshDiscoveryFilter` supports
  (task 3.3 - previously hardcoded to `new MeshDiscoveryFilter()` everywhere); `ArtifactStore`/
  `ArtifactRootDirectory`/`OutputPath` say where the registry document is written.
- `DiscoveryProviderFactory` - maps a `providers[]` name (`awsLambda`, `azureAppService`,
  `kubernetes`) to the matching `Benzene.Mesh.Discovery.*` package's provider, constructed off the
  ambient credential chain. An unknown name throws `InvalidOperationException` naming the valid
  values - the fail-fast-on-typo rule every config surface in this work follows.
- `DiscoveryArtifactStoreFactory` - maps `ArtifactStore.Type` (`file`, `s3`, `azureBlob`, `gcs`) to
  the matching `IMeshArtifactStore`, using the exact same option names as
  `Benzene.Mesh.Host.MeshSourceRegistrar.RegisterArtifactStore` (`bucket`/`prefix`,
  `blobServiceUri`/`container`/`prefix`) so the same `artifactStore` config block is meaningful on
  both sides of the discovery/host seam, even though the two factories share no code.
- `DiscoveryConfigLoader` - loads `DISCOVERY_CONFIG_PATH` (if set) as an additional JSON config
  source. A **set but missing** path throws `FileNotFoundException` naming the path, rather than
  silently running zero providers and writing an empty registry document.

## Deliberately no shared code with `Benzene.Mesh.Host`
This package could reuse `Benzene.Mesh.Host`'s `MeshHostConfig`/`MeshArtifactStoreConfig`/
`MeshSourceRegistrar` almost verbatim - it doesn't, on purpose. The two deployables' entire reason to
exist separately is that `Benzene.Mesh.Host` must remain physically incapable of enumerating a cloud
account (`../../Mesh/README.md`; enforced by
`deploy/Mesh/Benzene.Mesh.Host.Test/NoDiscoveryInVanillaHostTest.cs`). A shared config/wiring package
referenced by both would not itself violate that invariant (it carries no `Benzene.Mesh.Discovery.*`
dependency), but it would be the first thread pulling the two deployables' build graphs back
together - exactly the coupling the security position argues against. Each side's config/factory
classes are small (see above) and are copied, not extracted, for that reason.

## IAM / RBAC - least privilege per provider
Discovery only ever **enumerates and reads tags**. It never invokes a discovered service - that is
the aggregator's job, running as `Benzene.Mesh.Host` under a *different* role. Granting this job
invoke/write permissions on top of list/read would blur the separation of roles that is the entire
security argument for having two deployables.

| Provider | Cloud API | Minimum permission | Explicitly NOT needed |
|---|---|---|---|
| `awsLambda` | AWS Lambda | `lambda:ListFunctions`, `lambda:ListTags` | `lambda:InvokeFunction` - interrogation is `Benzene.Mesh.Host`'s job, under its own role |
| `azureAppService` | Azure Resource Manager | `Reader` on the enumerated scope (subscription or resource group) | `listHostKeys` / any `Microsoft.Web/sites/*` write action - discovery never calls a site, only ARM |
| `kubernetes` | Kubernetes API | `get`/`list` on `services` (RBAC `Role`/`ClusterRole`) | `get`/`list`/`watch` on `pods`/`secrets`, or any verb beyond `get`/`list` |
| `artifactStore` (any type) | S3 / Blob / GCS | write-only is sufficient (`s3:PutObject`, blob write, `storage.objects.create`) plus list/read if the job should also detect its own prior output | Nothing beyond the one bucket/container/prefix in `options` |

See `../README.md` for the full write-up (why this matters, the review/PR-gating pattern) and
`deploy/Mesh/README.md`'s own permission matrix for the *interrogation* side these permissions are
deliberately kept apart from.

## Dependencies on other Benzene packages
- **Benzene.Mesh.Contracts** - `IMeshDiscoveryProvider`, `MeshDiscoveryFilter`, `MeshDiscoveryRunner`,
  `MeshRegistryJson`, `MeshServiceRegistry`.
- **Benzene.Mesh.Discovery.Aws** / **Benzene.Mesh.Discovery.Azure** / **Benzene.Mesh.Discovery.Kubernetes** -
  the per-cloud enumeration adapters `DiscoveryProviderFactory` selects between.
- **Benzene.Mesh.Aggregator** - `IMeshArtifactStore`, `FileSystemMeshArtifactStore`.
- **Benzene.Mesh.Aws.S3** / **Benzene.Mesh.Azure.Blob** / **Benzene.Mesh.GoogleCloud.Storage** - the
  `artifactStore` write backends, the same packages `Benzene.Mesh.Host` reads them back with.
- **Deliberately absent: `Benzene.AspNet.Core`/`Microsoft.AspNetCore.*`** - no HTTP surface, so no web
  framework. This is a plain console app (`Microsoft.NET.Sdk`, `OutputType=Exe`), not
  `Microsoft.NET.Sdk.Web`.

## Why this isn't part of `Benzene.sln`/`Benzene.Examples.sln`
Same reasoning as `deploy/Mesh/Benzene.Mesh.Host`: an independently-versioned, independently-built,
Docker-packaged artifact with its own release lifecycle, not compiled/tested as part of the main
library's build. See `../../Mesh/README.md`'s section on this.

## Tests
`../Benzene.Mesh.Discovery.Host.Test/` (xUnit), added to `Benzene.Mesh.Discovery.Host.sln` (not
`test/Benzene.Mesh.Test/` or `Benzene.sln`, for the same reason as above). Exercises
`DiscoveryHostConfig`/`DiscoveryFilterConfig` binding and `ToFilter()`, `DiscoveryConfigLoader`, and
both factories' fail-fast paths on an unknown name. Deliberately does **not** exercise a *known*
`awsLambda`/`azureAppService`/`kubernetes`/`s3`/`gcs` name end-to-end: per `AwsMeshParityTest`'s own
remarks in the mesh host's test project, constructing an unconfigured AWS/Azure/GCS/Kubernetes SDK
client can throw immediately in an environment with no ambient region/credentials/in-cluster config
(verified true here too), which would make a test's pass/fail depend on the runner's environment
rather than on this code. The unknown-name fail-fast switch arms never reach the SDK, so those are
safe to assert directly; `DiscoveryArtifactStoreFactory`'s `file` case is exercised for the same
reason (no cloud SDK involved).
