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
  "Configuration" below for the full schema.
- **What this host will never do, by design:** enumerate a cloud account and auto-discover
  services. That capability exists (`Benzene.Mesh.Discovery.*`, used by the `examples/` Lambda
  hosts), but this host has **no reference to any `Benzene.Mesh.Discovery.*` package** - not a config
  flag that happens to default off, a capability this binary is physically incapable of. `services`
  is always a config-supplied, human-reviewed list. (A discovery-capable *separate* deployable is
  slice 3's job, not this host's.)

## Configuration

The primary path is a bind-mounted `mesh.json` (env var `MESH_CONFIG_PATH` points at it). Individual
top-level scalars can also be overridden via plain environment variables (`Host.CreateDefaultBuilder`
already wires those up, using .NET's standard double-underscore nesting, e.g. `Fleet__Source=xray`)
for single-value smoke-testing without a mounted file - `services` and the array/object sections below
are impractical to express that way, so `mesh.json` is the path for anything beyond a scalar or two.

**Credentials never go in `mesh.json`.** Every section below names an *endpoint* (a bucket, a
workspace id, a Tempo URL) at most - never a secret. Cloud-backed sections authenticate off the
container's ambient credential chain: an IAM role (AWS), a managed identity (Azure, via
`DefaultAzureCredential`), or the attached service account (Google Cloud, via Application Default
Credentials) - the same stance the AWS-Lambda-Invoke service source already took before this slice,
now generalized to every new section.

### `services` (existing, unchanged)

```jsonc
"services": [
  {
    "name": "orders-api",
    "specUrl": "http://orders-api:8080/spec?type=benzene",
    "healthUrl": "http://orders-api:8080/healthcheck"
  },
  {
    "name": "payments-fn",
    "source": "AwsLambdaInvoke",
    "sourceOptions": { "functionName": "payments-fn", "region": "us-east-1" }
  }
]
```

- `source` defaults to `"Http"` (so it can be omitted, as in the `orders-api` entry above) - see
  `Benzene.Mesh.Contracts.MeshServiceSource` for known values.
- `specUrl`/`healthUrl` are optional for non-`"Http"` sources - the fetch itself doesn't use them,
  but they're worth setting anyway purely so the Mesh UI's "spec"/"health" links have somewhere to
  point.
- `owningTeam` (optional) - the "who do I talk to" field the Mesh UI renders on each service card.

### `artifactStore` - where generated catalog artifacts live

```jsonc
"artifactStore": { "type": "file" }                                                            // default
"artifactStore": { "type": "s3", "options": { "bucket": "my-mesh-bucket", "prefix": "mesh/" } }
"artifactStore": { "type": "azureBlob", "options": {
  "blobServiceUri": "https://myaccount.blob.core.windows.net", "container": "mesh-artifacts", "prefix": "" } }
"artifactStore": { "type": "gcs", "options": { "bucket": "my-mesh-bucket", "prefix": "" } }
```

Valid `type` values: `file` (default), `s3`, `azureBlob`, `gcs`. `file` uses
`artifactRootDirectory` (below) on local disk, served at `/artifacts/*`; the other three read/write
the generated `manifest.json`/`services/*.json`/`topology.json`/etc. from the named cloud store
instead, served at the artifact's own path (e.g. `/manifest.json`) via `Benzene.Mesh.Artifacts`.
`s3`/`gcs` require `bucket`; `azureBlob` requires `blobServiceUri` and `container`; `prefix` is
optional on all three (default: none).

### `usage` - per-topic traffic feeds (array - zero or more)

```jsonc
"usage": [
  { "source": "cloudwatch", "options": { "namespace": "Benzene/Mesh", "windowHours": "24" } },
  { "source": "applicationInsights", "options": { "workspaceId": "00000000-0000-0000-0000-000000000000" } }
]
```

Valid `source` values: `cloudwatch`, `applicationInsights`. Both read the
`benzene.messages.processed` counter back from the named backend and merge it into `usage.json`;
`cloudwatch` needs nothing (every option has a default); `applicationInsights` requires
`workspaceId` (the Log Analytics workspace id, not the instrumentation key). Optional options on
either: `metricName`, `windowHours`, `topicDimension`, `transportDimension`, `resultDimension`
(CloudWatch also takes `periodSeconds`). An empty/omitted `usage` list means no usage feed - honestly
empty, not fabricated.

### `fleet` - the live-traffic view's data source (an object, not an array)

```jsonc
"fleet": { "source": "none" }                                                                  // default
"fleet": { "source": "xray", "options": { "correlationLookbackHours": "24" } }
"fleet": { "source": "tempo", "options": { "url": "http://tempo:3200", "recentFlowsLookbackHours": "1" } }
"fleet": { "source": "jaeger", "options": { "url": "http://jaeger:16686", "services": "orders-api,payments-api" } }
```

Valid `source` values: `none` (default - no live Fleet plane, the dashboard shows only the declared
catalog), `xray`, `tempo`, `jaeger`. Deliberately an object: `CompositeMeshFleetReadModel` composes a
single trace source, so only one fleet source can be configured (see "Known limitations" below).
`xray` needs nothing (every option has a default - the AWS execution role/environment supplies
credentials and region). `tempo`/`jaeger` require `url`. Optional on all three:
`correlationLookbackHours`, `recentFlowsLookbackHours`; `jaeger` additionally takes `services`
(comma-separated, pins the search to specific service names instead of discovering them) and
`searchLimitPerService`.

When `fleet.source` is anything but `none`, the host also wires the read-only
`mesh:query:*` handlers over an inner `/benzene/invoke` BenzeneMessage endpoint and points the mesh
UI's live Fleet plane at it - the same shape `examples/AwsMesh` hand-wires.

### `topology` - the service-graph view's extra (observed-traffic) edges

```jsonc
"topology": { "source": "none" }                                                               // default
"topology": { "source": "tempo", "options": { "prometheusUrl": "http://prometheus:9090/api/v1/query", "windowMinutes": "5" } }
```

Valid `source` values: `none` (default - only the structural edges the aggregator always derives
from each service's declared providers/consumers), `tempo` (adds `source: "tempo"` edges with real
traffic stats, from Tempo's service-graph metrics via a Prometheus-compatible query endpoint).
`tempo` requires `prometheusUrl`; `windowMinutes` is optional (default 5).

### `dispatch` - opt-in live dispatch

```jsonc
"dispatch": { "enabled": false, "allowInProduction": false }                                   // default
```

Off by default: it invokes a registered service's REAL handler with a chosen payload (real
side-effects execute). Two gates, both must pass: `enabled` (wires the feature at all) and, in a
Production environment, `allowInProduction` too (an unset environment counts as Production).

### `auth` - reserved

```jsonc
"auth": { "mode": "none" }                                                                     // the only value this slice implements
```

The key is carried through config binding by this slice but not acted on - `mode` must be `"none"`
today (the host requires no authentication, same as before this slice). A follow-up slice adds real
modes here; **the mesh dashboard has no login today - do not expose this host on a network you don't
trust until that lands.**

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

## Least-privilege permission matrix

Every credential comes from the container's ambient credential chain (AWS IAM role, Azure managed
identity, Google Cloud service account) - never from `mesh.json`. Grant only what the sections you
actually enable need.

| Config section / value | Cloud API | Minimum permission | Scope it to |
|---|---|---|---|
| `services[].source: "AwsLambdaInvoke"` | AWS Lambda `Invoke` | `lambda:InvokeFunction` | The specific function ARNs named in `sourceOptions.functionName` across all entries |
| `artifactStore.type: "s3"` | Amazon S3 | `s3:GetObject`, `s3:PutObject`, `s3:ListBucket` | The one bucket in `options.bucket` (and its `options.prefix`, if narrowing further) |
| `artifactStore.type: "azureBlob"` | Azure Blob Storage | `Storage Blob Data Contributor` (or a custom role with blob read+write+list) | The one storage account/container in `options.blobServiceUri`/`options.container` |
| `artifactStore.type: "gcs"` | Google Cloud Storage | `roles/storage.objectAdmin` (or a custom role with `storage.objects.get`/`create`/`list`) | The one bucket in `options.bucket` |
| `usage[].source: "cloudwatch"` | Amazon CloudWatch | `cloudwatch:GetMetricData` | The namespace in `options.namespace` (default `Benzene/Mesh`) |
| `usage[].source: "applicationInsights"` | Azure Monitor Logs | `Log Analytics Reader` | The one workspace in `options.workspaceId` |
| `fleet.source: "xray"` | AWS X-Ray | `xray:GetTraceSummaries`, `xray:BatchGetTraces` | Account-wide (X-Ray has no per-trace-source scoping) |
| `fleet.source: "tempo"` / `topology.source: "tempo"` | Grafana Tempo / Prometheus HTTP API | Read-only HTTP access to the query endpoint in `options.url`/`options.prometheusUrl` | Network-level (these are self-hosted HTTP services, not IAM-scoped) |
| `fleet.source: "jaeger"` | Jaeger Query HTTP API | Read-only HTTP access to the query endpoint in `options.url` | Network-level (self-hosted, not IAM-scoped) |
| `dispatch.enabled: true` | Whatever `services[].source` dispatches through (today: AWS Lambda `Invoke`) | Same as the matching `services[].source` row above | The specific dispatchable services - and see the "off by default, real side-effects" warning above before granting this at all |

This is a first version, meant to be reviewed by `aws-product-owner` before it reaches customers -
slice 5 moves it into a dedicated `CONFIG.md`.

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
  auth: none
```

## Worked examples

**Filesystem + HTTP** (the default - no cloud credentials needed, matches
[`mesh.sample.json`](mesh.sample.json)):

```jsonc
{
  "artifactRootDirectory": "mesh-artifacts",
  "pollIntervalSeconds": 60,
  "services": [
    { "name": "orders-api", "specUrl": "http://orders-api:8080/spec?type=benzene", "healthUrl": "http://orders-api:8080/healthcheck" },
    { "name": "payments-fn", "source": "AwsLambdaInvoke", "sourceOptions": { "functionName": "payments-fn", "region": "us-east-1" } }
  ]
}
```

**S3 + Lambda + X-Ray** (mirrors `examples/AwsMesh/Mesh/Startup.cs`'s wiring - see
`deploy/Mesh/Benzene.Mesh.Host.Test/AwsMeshParityTest.cs` for the test that proves every one of these
resolves from config):

```jsonc
{
  "services": [
    { "name": "orders-api", "source": "AwsLambdaInvoke", "sourceOptions": { "functionName": "orders-api" } }
  ],
  "artifactStore": { "type": "s3", "options": { "bucket": "mesh-artifacts-bucket", "prefix": "mesh/" } },
  "usage": [ { "source": "cloudwatch" } ],
  "fleet": { "source": "xray" }
}
```

The one AwsMesh capability this cannot reach: `AddMeshAwsLambdaDiscovery()` (auto-discovering the
Lambda functions to poll). That is deliberate - see "What this host will never do" above.

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
