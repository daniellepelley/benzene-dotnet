# Benzene.Mesh.Host

A config-driven, Docker/Compose-deployable Benzene Mesh Aggregator + UI - for running the mesh
dashboard against your *own real services* during local development, the same way you'd spin up a
local Postgres or Redis alongside your app. This is a genuinely different tier from
[`examples/Mesh/`](../../examples/Mesh): that folder is a demo with fake/canned data showing off
the feature; this is a real tool you point at real services.

See [`work/service-mesh-roadmap-1.0.md`](../../work/service-mesh-roadmap-1.0.md) for the full
multi-transport data collection design (Phases A-D) this host is the last phase of, and
[`work/enterprise/slice-1-config-schema.md`](../../work/enterprise/slice-1-config-schema.md) for the
config schema below (schema v1 - it supersedes §7.4's original `mesh.json`/`tempo` sketch in the
roadmap doc).

## What it does

- Polls every configured service on a timer (`pollIntervalSeconds`) via
  `Benzene.Mesh.Aggregator.MeshAggregator` - no external scheduler needed, unlike a hosted
  deployment where `mesh:aggregate` is typically triggered by a scheduled Lambda/Function
  invocation instead. This background poll loop (`MeshPollBackgroundService`) is new capability
  local to this Host app only - `MeshAggregateMessageHandler` itself stays invocation-triggered-only.
- Supports both HTTP-polled services (`Benzene.Mesh.Aggregator`'s `HttpMeshServiceSource`) and
  AWS-Lambda-Invoke-polled services (`Benzene.Mesh.Aws.Lambda`'s `LambdaMeshServiceSource`) via
  each service's `source`/`sourceOptions` in config - see below. Services with no synchronous entry
  point at all need to self-report instead (`Benzene.Mesh.Reporting`, linked into that service, not
  this host) and POST to this host's `/mesh/report` ingestion endpoint.
- Serves the Mesh UI dashboard at `/mesh-ui` and the per-service Spec UI at `/mesh-spec-ui.html`,
  self-served from the same host (`Benzene.Mesh.Ui`).
- **The whole catalog is reachable from `mesh.json` alone** (config schema v1): where generated
  artifacts live, additional usage/fleet/topology data sources, and opt-in live dispatch - the same
  capabilities the hand-wired examples under `examples/` (`AwsMesh`, `AzureMesh`,
  `AzureFunctionsMesh`, `GoogleCloudMesh`) demonstrate in C#, promoted to configuration. See
  [`CONFIG.md`](CONFIG.md) for the full schema - every key, its type/default, and worked examples.
- **What this host will never do, by design:** enumerate a cloud account and auto-discover
  services. That capability exists (`Benzene.Mesh.Discovery.*`, used by the `examples/` Lambda
  hosts), but this host has **no reference to any `Benzene.Mesh.Discovery.*` package** - not a config
  flag that happens to default off, a capability this binary is physically incapable of, enforced by
  `Benzene.Mesh.Host.Test/NoDiscoveryInVanillaHostTest.cs`. `services` is always a config-supplied,
  human-reviewed list - the only way discovered services reach this host is by config naming a
  document [`../Discovery`](../Discovery) already wrote, via `registryDocuments` below.

## Configuration

The primary path is a bind-mounted `mesh.json` (env var `MESH_CONFIG_PATH` points at it). Individual
top-level scalars can also be overridden via plain environment variables (`Host.CreateDefaultBuilder`
already wires those up, using .NET's standard double-underscore nesting, e.g. `Fleet__Source=xray`)
for single-value smoke-testing without a mounted file - `services` and the array/object sections
below are impractical to express that way, so `mesh.json` is the path for anything beyond a scalar
or two.

**[`CONFIG.md`](CONFIG.md) is the full reference** - every section (`artifactStore`, `services`,
`registryDocuments`, `usage`, `fleet`, `topology`, `dispatch`, `auth`), one table per section with
each key's type/default/required-when, worked examples for the filesystem+HTTP and S3+Lambda+X-Ray
deployment shapes, and the per-source least-privilege permission matrix. This section only states
the two invariants that hold across every section, because they're the two things worth knowing
before you open that document:

1. **Credentials never go in `mesh.json`.** Every section names an *endpoint* (a bucket, a workspace
   id, a Tempo URL) at most - never a secret. Cloud-backed sections authenticate off the container's
   ambient credential chain: an IAM role (AWS), a managed identity (Azure, via
   `DefaultAzureCredential`), or the attached service account (Google Cloud, via Application Default
   Credentials). Where a credential genuinely has to exist (auth's `basic`/`oidc` modes, the
   ingestion shared secret), it comes from a named environment variable instead.
2. **Unknown source/type names fail at startup, listing the valid values.** A typo'd `source` or
   `type` never silently falls back to a default. `--validate-config` (below) runs the identical
   check, so this is caught before a deploy, not after one.

`auth` (work/enterprise/slice-2-auth.md) is worth one callout here because it protects everything
else: one gate (`MeshAuthGate`) covers both `/artifacts/*` (served outside the Benzene pipeline when
`artifactStore.type` is `file`, the default) and everything inside the pipeline, in every mode.
`mode: "none"` (the default) leaves the host exactly as it was before slice 2: no login, everything
world-readable. **Do not expose this host on a network you don't trust with `mode: "none"`.**
`auth.dispatchRole` is bound and validated but **not yet enforced** - `mesh:dispatch` (gated behind
`dispatch.enabled`) has no HTTP route or envelope endpoint of its own yet (a pre-existing gap, see
`Benzene.Mesh.Dispatch/CLAUDE.md`'s "Follow-ups" and the comment above `UseMeshDispatch` in
`Startup.cs`), so there's no reachable request to attach a role check to.

### Known limitations (documented, not fixed by this slice)

- **`fleet` composes one trace source.** `CompositeMeshFleetReadModel` (`Benzene.Mesh.Collector`)
  takes a single `IMeshTraceSource`, not an `IEnumerable<>`, so `fleet` is one object, not an array -
  you cannot wire X-Ray and Tempo into the same host today.
- **On a composite (`xray`/`tempo`/`jaeger`) fleet plane, the service and topic drill-in pages don't
  work.** `CompositeMeshFleetReadModel.ServiceAsync`/`TopicAsync` return hardcoded `null`, so
  `mesh:query:service`/`mesh:query:topic` always answer "not found" - a pre-existing bug in
  `Benzene.Mesh.Collector`, not something this slice introduces or fixes. Fleet-wide data (the
  landing view, correlation search, individual trace lookup) all work; only the two per-entity
  drill-ins don't.

## `--validate-config`

Binds and validates `mesh.json` (or the env-vars-only equivalent) without starting the host - the
only way to catch a config mistake without deploying it. Exits `0` and prints a one-line summary per
section for a valid config; exits non-zero with the specific problem (an unknown source/type name
listing the valid values, or a missing required option naming the key) for an invalid one. Runs the
exact same validation rules the host itself uses at startup - a config that passes here is guaranteed
to start the same way.

```bash
MESH_CONFIG_PATH=./mesh.sample.json dotnet run --project Benzene.Mesh.Host -- --validate-config
```

```
mesh.json is valid.
  artifactStore: file
  services: 2
  usage: (none)
  fleet: none
  topology: none
  dispatch: disabled
  auth: none (ingestion=open)
```

## Worked examples

The filesystem+HTTP and S3+Lambda+X-Ray examples live in [`CONFIG.md`](CONFIG.md#worked-examples).
One more worth showing here, since it's a discovery-workflow example rather than a plain config
reference: **S3 + a discovered service list** (`../Discovery` writes `registry.json` to the same
bucket; this host reads it back and unions it with a hand-pinned entry):

```jsonc
{
  "services": [
    { "name": "legacy-billing", "specUrl": "http://legacy-billing.internal/spec?type=benzene", "healthUrl": "http://legacy-billing.internal/healthcheck" }
  ],
  "registryDocuments": [ "registry.json" ],
  "artifactStore": { "type": "s3", "options": { "bucket": "mesh-artifacts-bucket", "prefix": "mesh/" } }
}
```

`legacy-billing` (not discoverable - it predates the `benzene` tag convention) stays pinned by
`services`; every other entry in `registry.json` is discovered automatically.

## Running it via Docker Compose

Not something this repo runs itself (there are no real services here to compose against beyond the
demo in `examples/Mesh/`) - this is what a *consuming* solution's own `docker-compose.yml` would add
alongside its own services:

```yaml
services:
  mesh:
    image: ghcr.io/<org>/benzene-mesh:latest
    ports:
      - "8090:8080"
    environment:
      - MESH_CONFIG_PATH=/config/mesh.json
    volumes:
      - ./mesh.json:/config/mesh.json:ro
      - mesh-artifacts:/data/mesh-artifacts
    depends_on: [orders-api, payments-api]
volumes:
  mesh-artifacts:
```

Then browse `http://localhost:8090/mesh-ui`.

## Deploying with Helm

[`helm/benzene-mesh/`](helm/benzene-mesh) is a chart for running the same image in Kubernetes -
the Deployment mounts a ConfigMap at `/config/mesh.json` and sets `MESH_CONFIG_PATH` to it, the
same wiring [`examples/K8sMesh/compose/docker-compose.yml`](../../examples/K8sMesh/compose/docker-compose.yml)'s
`mesh` service does with a bind mount. `values.yaml`'s `meshConfig` is that same `mesh.json` shape
(see [`CONFIG.md`](CONFIG.md)) rendered to the ConfigMap as JSON.

```bash
helm install my-mesh deploy/Mesh/helm/benzene-mesh \
  --set meshConfig.services[0].name=orders-api \
  --set meshConfig.services[0].specUrl=http://orders-api/spec?type=benzene \
  --set meshConfig.services[0].healthUrl=http://orders-api/healthcheck
# or: helm install my-mesh deploy/Mesh/helm/benzene-mesh -f my-values.yaml
```

**Secrets never go into the chart's ConfigMap** - the same invariant CONFIG.md states for
`mesh.json` itself. Auth client secrets and any source credentials come from a Kubernetes `Secret`
you create separately and name in `values.existingSecretName`; the chart wires it into the mesh
container via `envFrom`/`secretRef`, never templates it into the ConfigMap. See the chart's
`values.yaml` for which environment variable each `auth.mode`/`auth.ingestion.mode` needs.

The chart also supports (all optional, off by default): a `PersistentVolumeClaim` for
`mesh-artifacts` instead of an `emptyDir` (`persistence.enabled`), an `Ingress`
(`ingress.enabled`), and a `ServiceAccount` with cloud-identity annotations for IRSA/Workload
Identity (`serviceAccount.annotations`) - matching [`CONFIG.md`](CONFIG.md#per-source-least-privilege-permission-matrix)'s
permission matrix.

## Local development (without Docker)

```bash
cd deploy/Mesh
dotnet build Benzene.Mesh.Host.sln
MESH_CONFIG_PATH="$(pwd)/mesh.sample.json" dotnet run --project Benzene.Mesh.Host
```

Use an absolute path (`$(pwd)/...`), not a bare relative one: `dotnet run --project <dir>` runs with
its working directory set to `<dir>` itself, not the directory you invoked it from - a relative
`MESH_CONFIG_PATH` silently resolves against `Benzene.Mesh.Host/`, not `deploy/Mesh/`, which is easy
to get wrong (and, since a set-but-missing path now fails loudly, would surface as a confusing
"no file exists there" rather than quietly starting empty). Copy `mesh.sample.json` and edit it for
your own services once you're past the first run.

## Building the image locally

```bash
# from the repo root - the Dockerfile needs the whole repo as build context (sibling src/ ProjectReferences)
docker build -f deploy/Mesh/Benzene.Mesh.Host/Dockerfile -t benzene-mesh:local .
docker run -p 8090:8080 -e MESH_CONFIG_PATH=/config/mesh.json -v "$(pwd)/mesh.json:/config/mesh.json:ro" benzene-mesh:local
```

## Publishing

`.github/workflows/deploy-mesh-host.yml` (manual `workflow_dispatch`, same trigger pattern as every
other Benzene deploy workflow) builds and pushes this image to GHCR. `.github/workflows/build-mesh-host.yml`
runs on every push/PR touching this folder, compiling `Benzene.Mesh.Host.sln` - this deployable gets
real CI coverage, unlike `examples/`, since it's a production-ready primitive, not a demo.

## Why this isn't in `Benzene.sln`/`Benzene.Examples.sln`

Same reasoning as `templates/Benzene.Templates.sln`: an independently-versioned, independently-built
artifact (here, Docker-packaged instead of NuGet-packaged) with its own release lifecycle, not
compiled/tested as part of the main library's build.
