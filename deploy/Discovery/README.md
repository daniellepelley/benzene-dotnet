# Benzene.Mesh.Discovery.Host

A one-shot job that enumerates a cloud account or cluster (filtered by tag/label), writes what it
found as a `mesh.json`-shaped registry document to a shared artifact store, and exits. It is the
*discovery* half of mesh self-discovery; [`deploy/Mesh`](../Mesh) (`Benzene.Mesh.Host`) is the
*runtime* half that reads the document back and interrogates each service. See
[`work/archive/mesh-self-discovery-design-2026-07.md`](../../work/archive/mesh-self-discovery-design-2026-07.md) for the full design
and [`work/enterprise/slice-3-discovery.md`](../../work/enterprise/slice-3-discovery.md) for the brief
this package implements.

## Why this is a separate deployable, not a flag on the mesh host

An enterprise customer may want the mesh to discover services automatically - and may equally not,
because software that can enumerate a cloud account is a finding in a security review. The product
position this package implements:

> **The default is an explicit, hand-written service list, and the vanilla mesh host is *physically
> incapable* of enumerating a cloud account - not "off by default"; absent.**

The distinction is the whole point. In a security review, "the flag is off" invites the question "who
can turn it on?". "The image contains no code path that calls `ListFunctions`, and its role needs no
list permissions" ends the conversation. [`deploy/Mesh/Benzene.Mesh.Host`](../Mesh/Benzene.Mesh.Host)
has **no reference to any `Benzene.Mesh.Discovery.*` package**, enforced by
[`NoDiscoveryInVanillaHostTest`](../Mesh/Benzene.Mesh.Host.Test/NoDiscoveryInVanillaHostTest.cs) - a
build-breaking CI check, not a code review reminder.

So discovery runs here instead, as its own deployable, under its own least-privilege role (list/read
only - see the permission matrix below), and emits an inspectable registry document. The mesh host
reads that document like any other config
([`registryDocuments`](../Mesh/README.md#registrydocuments---discovery-generated-service-lists)).
**Discovery proposes; config disposes** - the registry document this job writes is not consumed
automatically by anything with write access to the estate; it is a proposal an operator's config
chooses to read, and can always override entry-by-entry (`services` in the mesh host's own config
always wins a name clash against a discovered entry).

## What it does, and does not do

- Runs every configured discovery provider (`providers[]`) once, unions their results, and writes the
  combined registry to the configured artifact store at `outputPath`.
- Exits `0` on success, non-zero on any failure - the only contract a scheduler needs to notice a
  broken run. There is no retry loop inside the job; retry is the scheduler's job (see "Scheduling"
  below).
- **Never invokes a discovered service.** Discovery only enumerates and reads tags/labels - the
  interrogation step (fetching each service's spec/health/descriptor) is `Benzene.Mesh.Host`'s job,
  running under a *different*, broader-but-still-scoped role. Blurring that line - e.g. granting this
  job `lambda:InvokeFunction` "while we're at it" - defeats the separation of roles that is the whole
  argument for two deployables.
- **Has no HTTP surface and no poll loop.** It is a job, not a server: a long-running process holding
  cloud-enumeration permissions is exactly what this design avoids. Nothing here listens on a port.

## Configuration

The primary path is a bind-mounted `discovery.json` (env var `DISCOVERY_CONFIG_PATH` points at it),
mirroring the mesh host's own `MESH_CONFIG_PATH` convention exactly. Individual top-level scalars can
also be overridden via plain environment variables (.NET's standard double-underscore nesting, e.g.
`Filter__TagKey=team-orders`).

**Credentials never go in `discovery.json`.** Every section below names an *endpoint* or a *filter* at
most, never a secret - the same stance the mesh host takes. Every provider and every artifact store
backend authenticates off the ambient credential chain: an IAM role (AWS), a managed identity (Azure,
via `DefaultAzureCredential`), the attached service account (Google Cloud), or the pod's in-cluster
service account (Kubernetes).

```jsonc
{
  "providers": [ "awsLambda" ],                          // "awsLambda" | "azureAppService" | "kubernetes"
  "filter": {
    "tagKey": "benzene",                                  // default - the tag/label a resource must carry
    "regions": [ "us-east-1" ],                           // optional - AWS/Azure region scoping
    "namespace": "orders-ns"                               // optional - Kubernetes namespace scoping
  },
  "artifactStore": { "type": "s3", "options": { "bucket": "mesh-artifacts-bucket", "prefix": "mesh/" } },
  "outputPath": "registry.json"
}
```

- `providers` - which discovery adapters to run, by name. Unknown names fail fast at startup, listing
  the valid values - the same rule every config surface in this work follows. Empty by default: a run
  with no providers configured writes an empty registry document rather than guessing at one.
- `filter` - the tag/label filter every configured provider is run with (task 3.3: previously
  hardcoded to `new MeshDiscoveryFilter()` everywhere discovery ran). `tagKey` defaults to `"benzene"`
  (presence-only match - the value is ignored); `regions`/`namespace` are optional scoping. An estate
  where discovery should see only part of the account is the normal enterprise case - this filter is
  the mechanism.
- `artifactStore` - **the same shape and option names as
  [the mesh host's own `artifactStore`](../Mesh/README.md#artifactstore---where-generated-catalog-artifacts-live)**
  (`file`, `s3`, `azureBlob`, `gcs`), so pointing both deployables' config at the same bucket/container
  is a copy-paste of one block. `artifactRootDirectory` (top-level, default `"discovery-artifacts"`)
  is used when `artifactStore.type` is `"file"`.
- `outputPath` - the relative path (within `artifactStore`) the registry document is written to.
  Defaults to `"registry.json"`. This is exactly the path a mesh host's `registryDocuments` entry
  should name.

## Least-privilege permission matrix

Discovery only ever **enumerates and reads tags**; it never invokes a discovered service. Grant only
what the providers/artifact store you actually enable need.

| Config value | Cloud API | Minimum permission | Explicitly NOT needed |
|---|---|---|---|
| `providers: [ "awsLambda" ]` | AWS Lambda | `lambda:ListFunctions`, `lambda:ListTags` | `lambda:InvokeFunction` - interrogation is `Benzene.Mesh.Host`'s job, under its own role |
| `providers: [ "azureAppService" ]` | Azure Resource Manager | `Reader` on the enumerated scope (subscription or resource group) | `listHostKeys` / any `Microsoft.Web/sites/*` write action - discovery never calls a site, only ARM |
| `providers: [ "kubernetes" ]` | Kubernetes API | `get`/`list` on `services` (RBAC `Role`/`ClusterRole`, bound to this job's ServiceAccount) | `get`/`list`/`watch` on `pods`/`secrets`, or any verb beyond `get`/`list` |
| `artifactStore.type: "s3"` | Amazon S3 | `s3:PutObject` (add `s3:GetObject`/`s3:ListBucket` only if you also want the job to read back its own prior output) | Broader S3 access than the one bucket/prefix in `options` |
| `artifactStore.type: "azureBlob"` | Azure Blob Storage | A custom role with blob write (+list if reading back prior output) | `Storage Blob Data Contributor`'s implicit delete/broader scope if a narrower role suffices |
| `artifactStore.type: "gcs"` | Google Cloud Storage | `storage.objects.create` (+`get`/`list` if reading back prior output) | `roles/storage.objectAdmin` (broader than needed) unless nothing narrower is available |

Compare this table with [the mesh host's own permission matrix](../Mesh/README.md#least-privilege-permission-matrix):
the two are deliberately non-overlapping on the interrogation axis (`lambda:InvokeFunction` and
friends belong only to the host's role, never to this job's).

## Worked example: AWS, S3-backed

```jsonc
{
  "providers": [ "awsLambda" ],
  "filter": { "tagKey": "benzene" },
  "artifactStore": { "type": "s3", "options": { "bucket": "mesh-artifacts-bucket", "prefix": "mesh/" } },
  "outputPath": "registry.json"
}
```

Paired with the mesh host reading it back:

```jsonc
{
  "registryDocuments": [ "registry.json" ],
  "artifactStore": { "type": "s3", "options": { "bucket": "mesh-artifacts-bucket", "prefix": "mesh/" } }
}
```

Both point at the same bucket/prefix - the host has no separate credential path for the discovered
list, it reads the artifact store it already has access to.

## Scheduling and the recommended review/PR-gating pattern

This job is meant to be triggered by your platform's own scheduler - an EventBridge scheduled rule
(AWS), a Cloud Scheduler job (Google Cloud) or timer-triggered Function invocation (Azure), or a
Kubernetes `CronJob`. Nothing in this repository runs that trigger for you; wiring one up is a
deployment concern for the consuming platform, the same way `deploy/Mesh` documents Compose usage
without shipping a Compose file for a real estate.

**The enterprise-grade pattern** is to not let this job's output reach the running mesh directly at
all: point `outputPath` at a location a pull request can review, rather than the live artifact store
the mesh host reads from -

1. Discovery runs on its schedule and writes `registry.json` to a staging location (a separate
   prefix, or a location a CI job then copies into a version-controlled file).
2. A pull request is opened (automatically or by a human) showing the diff against the previously
   reviewed registry - new services appearing, retired ones dropping off, are visible line-by-line
   like any other config change.
3. A human approves the PR, and *that* merge (not the discovery run itself) is what updates the
   location the mesh host's `registryDocuments` actually points at.

This costs the "new services appear automatically" promise some of its immediacy, in exchange for
every discovered-service addition being a reviewable, auditable change - the same trade-off code
review makes generally. Where that immediacy matters more than the review gate, point discovery's
`outputPath` directly at the artifact store the host reads and skip the PR step; both are valid, and
the config shape supports either without modification.

## Building the image locally

```bash
# from the repo root - the Dockerfile needs the whole repo as build context (sibling src/ ProjectReferences)
docker build -f deploy/Discovery/Benzene.Mesh.Discovery.Host/Dockerfile -t benzene-mesh-discovery:local .
docker run --rm -e DISCOVERY_CONFIG_PATH=/config/discovery.json -v "$(pwd)/discovery.json:/config/discovery.json:ro" benzene-mesh-discovery:local
```

The container runs, writes the registry document, and exits - there is no port to publish, unlike
`deploy/Mesh`'s image.

## Local development (without Docker)

```bash
cd deploy/Discovery
dotnet build Benzene.Mesh.Discovery.Host.sln
DISCOVERY_CONFIG_PATH="$(pwd)/discovery.sample.json" dotnet run --project Benzene.Mesh.Discovery.Host
```

Use an absolute path (`$(pwd)/...`), not a bare relative one - same reasoning as
[the mesh host's own README](../Mesh/README.md#local-development-without-docker): `dotnet run
--project <dir>` runs with its working directory set to `<dir>` itself.

## Publishing

`.github/workflows/build-discovery-host.yml` runs on every push/PR touching this folder, compiling
`Benzene.Mesh.Discovery.Host.sln`. `.github/workflows/deploy-discovery-host.yml` (manual
`workflow_dispatch` only, same trigger pattern as every other Benzene deploy workflow - nothing here
publishes automatically) builds and pushes the image to GHCR.

## Why this isn't part of `Benzene.sln`/`Benzene.Examples.sln`

Same reasoning as `deploy/Mesh/Benzene.Mesh.Host`: an independently-versioned, independently-built,
Docker-packaged artifact with its own release lifecycle, not compiled/tested as part of the main
library's build. See [`deploy/Mesh/README.md`](../Mesh/README.md)'s own section on this.
