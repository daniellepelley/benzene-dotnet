# Slice 1 — Config schema v1: the whole catalog from `mesh.json`

**Status:** ready to build. **This is the centrepiece of the set.**
**Depends on:** slice 0 (merged). Without 0.1 the adapters fight; without 0.2 non-filesystem
artifact stores have nothing serving them; without 0.3 there is nowhere to put the tests.
**Branch:** `claude/mesh-enterprise-slice-1`

## Why

Four hand-built mesh servers exist under `examples/` — AWS, Azure, Azure Functions, Kubernetes. Each
is ~200 lines of expert wiring, and each proves the mesh is flexible. None of it is reachable
without internal Benzene knowledge, which filters the audience down to people who already work on
the framework.

`deploy/Mesh/Benzene.Mesh.Host` already proves the config path works: it binds `mesh.json` to
`MeshHostConfig`, and it already selects a service's fetch source **by name** (`"Http"` /
`"AwsLambdaInvoke"`) with a `sourceOptions` dictionary. But it references only the aggregator,
Lambda source, UI and dispatch. **No usage source, no fleet plane, no artifact store beyond local
disk, no topology is reachable from configuration.**

This slice extends the pattern that already exists to the other four component axes. It adds no new
capability to Benzene — it makes the capability Benzene already has reachable without C#.

**The acceptance test is the whole point of the slice:** reproduce `examples/AwsMesh/Mesh/Startup.cs`
capability-for-capability from configuration alone.

## Before you start

Read, in order:

1. [`README.md`](README.md) in this folder — house rules.
2. `deploy/Mesh/Benzene.Mesh.Host/Startup.cs`, `MeshHostConfig.cs`, `Program.cs` — what you are extending.
3. `examples/AwsMesh/Mesh/Startup.cs` — what you are reproducing.
4. `deploy/Mesh/README.md` and `deploy/Mesh/Benzene.Mesh.Host/CLAUDE.md` — both must be true at the end.

```bash
dotnet build Benzene.sln
dotnet build deploy/Mesh/Benzene.Mesh.Host.sln
dotnet test  deploy/Mesh/Benzene.Mesh.Host.sln
```

### The single most important thing to know before you write any binding code

**No `Add*` extension in the mesh accepts an `IConfiguration` or an `Action<TOptions>`.** Every one
takes a fully-constructed options object, or loose primitives. Binding is entirely the caller's job:
bind, then pass.

And the options classes **bind inconsistently**. This table is the result of reading every one of
them; trust it over intuition:

| Options class | Parameterless ctor | Get-only (ctor-only) | Settable | Options arg |
|---|---|---|---|---|
| `XRayTraceSourceOptions` | yes | — | `CorrelationLookback`, `RecentFlowsLookback`, `RecentFlowsServiceEnrichmentMax` (`init`) | **optional** |
| `TempoTraceSourceOptions` | **no** — `(string tempoUrl)` | `TempoUrl` | `CorrelationLookback`, `RecentFlowsLookback` | required |
| `JaegerTraceSourceOptions` | **no** — `(string jaegerUrl)` | `JaegerUrl` | `Services`, both lookbacks, `SearchLimitPerService` | required |
| `CloudWatchUsageOptions` | no, but every param has a default | `Namespace`, `MetricName`, `TimeWindow` | `TopicDimension`, `TransportDimension`, `ResultDimension`, `PeriodSeconds` | required |
| `ApplicationInsightsUsageOptions` | **no** — `workspaceId` required | `WorkspaceId`, `MetricName`, `TimeWindow` | three dimension names | required |
| `TempoTopologyOptions` | **no** — `(string prometheusUrl, TimeSpan?)` | `PrometheusUrl`, `TimeWindow` — **nothing settable** | none | required |
| `MeshDispatchOptions` | yes | — | `AllowInProduction` | optional |

`TempoTraceSourceOptions` and `JaegerTraceSourceOptions` also **normalize their URL in the
constructor** (`TrimEnd('/')`), so anything that bypasses the constructor skips normalization.

**Because of this, do NOT bind the `src/` options classes directly, and do NOT modify them.**
Modifying them is a public API change, which the house rules forbid in slices 0–3. Instead:

> **Define fully-bindable mirror POCOs in the host project** (plain `{ get; set; }`, parameterless),
> bind config to those, then construct the real options from them. Additive, safe, reversible, and
> it keeps constructor normalization intact.

## The target schema

```jsonc
{
  "artifactRootDirectory": "/data/mesh-artifacts",   // existing; still the default filesystem root
  "pollIntervalSeconds": 60,                          // existing
  "services": [ /* existing shape, unchanged */ ],
  "artifactStore": { "type": "file", "options": { "bucket": "…", "prefix": "…", "container": "…", "blobServiceUri": "…" } },
  "usage":    [ { "source": "cloudwatch", "options": { "namespace": "Benzene/Mesh", "windowHours": 24 } } ],
  "fleet":    { "source": "none", "options": { "url": "…", "correlationLookbackHours": 24 } },
  "topology": { "source": "none", "options": { "prometheusUrl": "…", "windowMinutes": 5 } },
  "dispatch": { "enabled": false, "allowInProduction": false },
  "auth":     { "mode": "none" }                      // slice 2 fills this in; reserve the key now
}
```

Valid `source`/`type` names — these are the **only** ones this slice adds:

- `artifactStore.type`: `file` (default), `s3`, `azureBlob`, `gcs`
- `usage[].source`: `cloudwatch`, `applicationInsights`
- `fleet.source`: `none` (default), `xray`, `tempo`, `jaeger`
- `topology.source`: `none` (default), `tempo`

**Backwards compatibility is required.** `examples/K8sMesh/compose/mesh.json` must keep working
untouched — it sets only `artifactRootDirectory`, `pollIntervalSeconds` and `services`. Every new
section defaults to today's behaviour.

## Tasks

### 1.1 — The config classes

**Files:** `deploy/Mesh/Benzene.Mesh.Host/MeshHostConfig.cs` (modify), plus new files in the same
folder for the section classes if it reads better.

All mutable `{ get; set; }`, all with defaults. `MeshHostConfig` already documents why it deviates
from the immutable style used in `Benzene.Mesh.Contracts` — the binder requires it. Follow that.

Model options as `Dictionary<string, string>` per section, matching the existing
`MeshHostServiceConfig.SourceOptions` precedent, rather than a typed class per source. One shape to
learn, and the host does not have to grow a class every time a source is added.

**Verify:** binding tests in `deploy/Mesh/Benzene.Mesh.Host.Test/` — the compose sample binds with
every new section at its default; a full config binds every section.

### 1.2 — The source registrar, with fail-fast

**Files:** new `deploy/Mesh/Benzene.Mesh.Host/MeshSourceRegistrar.cs`; `Startup.cs` (modify).

One place that maps a name to an `Add*` call. Keep it a plain `switch` over lowercased names — a
reflection-driven registry would be cleverer and much harder to debug at 3am.

**Unknown names must fail at startup, listing the valid values.** This is the single most important
behaviour in the slice: silently falling back to a default when someone typos `"cloudwtach"` gives
an operator an empty dashboard and no reason. Message shape:

```
Unknown usage source 'cloudwtach'. Valid values: cloudwatch, applicationInsights.
```

Do the same for a **missing required option** — `fleet.source: "tempo"` with no `url` must name the
missing key, not throw a `NullReferenceException` on first poll.

Register the artifact store by rewriting the existing `AddMeshAggregator(registry, artifactRootDirectory)`
call into the `Func<IServiceResolver, IMeshArtifactStore>` overload for non-`file` types. The
adapters are `AddMeshAggregatorWithS3(registry, bucket, prefix)`,
`AddMeshAggregatorWithBlob(registry, blobServiceUri, containerName, prefix)`,
`AddMeshAggregatorWithGcs(registry, bucket, prefix)`.

**Exactly one fleet source may be registered.** `CompositeMeshFleetReadModel` takes a single
`IMeshTraceSource`, not an enumerable — see the backlog note in [`README.md`](README.md). `fleet` is
therefore an object, not an array. `usage` **is** an array, because `IMeshUsageSource` is resolved
as `IEnumerable<>` and genuinely supports several.

When `fleet.source` is not `none`, also register the read handlers and point the UI at the envelope,
mirroring what AwsMesh does:

```csharp
asp.UseMeshUi(path: "/mesh-ui", manifestUrl: "/artifacts/manifest.json", envelopeUrl: "/benzene/invoke");
// and an inner benzene-message pipeline serving MeshCollectorHandlers.Queries
```

Use `MeshCollectorHandlers.Queries` (the five read-only `benzene:mesh:query:*` handlers), **not**
`.All` — there is no in-memory ring to ingest into on this plane.

**Known limitation to document, not fix:** on a composite (X-Ray/Tempo/Jaeger) plane,
`CompositeMeshFleetReadModel.ServiceAsync` and `TopicAsync` return hardcoded `null`, so
`benzene:mesh:query:service` and `:topic` answer "not found" and the service/topic drill-in pages do
not work. That is a pre-existing bug on the deferred list. State it in the README; do not fix it here.

**Verify:** a test per source name that the expected registration appears in the container; a test
per unknown name that startup throws with the valid values in the message.

### 1.3 — Project references

**File:** `deploy/Mesh/Benzene.Mesh.Host/Benzene.Mesh.Host.csproj` (modify).

Add: `Benzene.Mesh.Aws.S3`, `Benzene.Mesh.Azure.Blob`, `Benzene.Mesh.GoogleCloud.Storage`,
`Benzene.Mesh.Fleet.Aws.XRay`, `Benzene.Mesh.Fleet.Tempo`, `Benzene.Mesh.Fleet.Jaeger`,
`Benzene.Mesh.Usage.CloudWatch`, `Benzene.Mesh.Usage.ApplicationInsights`,
`Benzene.Mesh.Tracing.Tempo`, `Benzene.Mesh.Collector`, and `Benzene.Mesh.Artifacts` (from slice 0).

**Do NOT add any `Benzene.Mesh.Discovery.*` reference.** The vanilla host must be physically
incapable of enumerating a cloud account — see slice 3, which makes that a tested invariant. If a
task seems to need discovery, you have misread it.

This pulls in the AWS, Azure and Google SDKs, which grows the image. That is the accepted cost of one
image that can be configured into any of them; do not try to trim it with conditional compilation.

Also update `.github/workflows/build-mesh-host.yml`'s path filters to include the newly referenced
packages — slice 0 already established the pattern and fixed the existing hole.

### 1.4 — `--validate-config`

**Files:** `Program.cs` (modify).

`benzene-mesh --validate-config` (or `dotnet run -- --validate-config`) binds and validates the
config, prints what it resolved, and exits — 0 for valid, non-zero with the error for invalid.
Without it, the only way to test a config change is to deploy it.

Reuse task 1.2's validation; do not write a second copy of the rules. If validation and startup can
disagree, the feature is worse than useless.

**Verify:** valid config → exit 0; unknown source name → non-zero, message names the bad value.

### 1.5 — The acceptance test

**Files:** `deploy/Mesh/Benzene.Mesh.Host.Test/AwsMeshParityTest.cs` (new).

Write a `mesh.json` that reproduces `examples/AwsMesh/Mesh/Startup.cs`: S3 artifact store, a service
sourced by `AwsLambdaInvoke`, CloudWatch usage, X-Ray fleet, UI and spec UI. Assert every
corresponding registration resolves from the container.

Do **not** call AWS. Assert on the registrations, not on behaviour that needs credentials.

**Done when:** for each `Add*` call in AwsMesh's `Startup.cs`, an assertion proves configuration
produced the equivalent. If any capability cannot be reached from config, that is the finding —
report it rather than quietly narrowing the test.

### 1.6 — Documentation

**Files:** `deploy/Mesh/README.md`, `deploy/Mesh/Benzene.Mesh.Host/CLAUDE.md`, and a new sample
`deploy/Mesh/mesh.sample.json`.

The README currently documents only `services` and two scalars. Document every section, with a
worked example for the filesystem/HTTP case and the S3/Lambda/X-Ray case.

Include a **per-source least-privilege permission matrix** — the IAM or RBAC permissions each source
needs (for example `AwsLambdaInvoke` requires `lambda:InvokeFunction` on the listed functions;
CloudWatch usage requires `cloudwatch:GetMetricData`). An enterprise reviewer asks for this first.
Slice 5 later moves it into a dedicated `CONFIG.md`; a first version belongs here now.

Add `deploy/Mesh/mesh.sample.json`. The README's local-dev instructions already tell people to run
`MESH_CONFIG_PATH=./mesh.json` from `deploy/Mesh` — **and that file does not exist in the repo**, so
the documented command has always started an empty mesh. Slice 0 makes that failure loud; this makes
it unnecessary.

In `CLAUDE.md`, remove the **"No Tempo wiring"** deviation — this slice closes it. A resolved caveat
left on the page makes the next reader plan around a limitation that is gone.

**Credentials never appear in any sample.** Config names endpoints; secrets come from the ambient
credential chain or environment variables. Say so explicitly in the README.

## Definition of done

- [ ] All build and test commands green, including `dotnet build Benzene.sln`.
- [ ] `examples/K8sMesh/compose/mesh.json` still works unmodified, and the compose smoke test passes.
- [ ] Every name in the valid-values lists registers what it should, proven by a test each.
- [ ] Every unknown name fails at startup with a message listing the valid values, proven by a test each.
- [ ] A missing required option names the missing key.
- [ ] `--validate-config` returns 0 for the sample and non-zero for a broken config.
- [ ] The AwsMesh parity test passes, or its gaps are reported.
- [ ] `mesh.sample.json` exists; README documents every section plus the permission matrix; the
      "No Tempo wiring" deviation is gone from `CLAUDE.md`.
- [ ] No `src/` options class was modified. No public API signature changed.
- [ ] No `Benzene.Mesh.Discovery.*` reference was added.

## Do NOT

- Do not modify options classes under `src/`. Mirror them in the host instead.
- Do not add a plugin/assembly-loading mechanism. This is the slice where that temptation appears;
  the answer is in the house rules and it is no.
- Do not put credentials, tokens or connection strings in `mesh.json` or any sample.
- Do not make `fleet` an array.
- Do not fix `ServiceAsync`/`TopicAsync` returning `null` — document it.
- Do not silently default an unknown name to anything.

## Report back with

The full valid-values list as built; the AwsMesh parity test result including any capability that
could not be reached from config; and the permission matrix you wrote, so it can be reviewed by
`aws-product-owner` before it reaches customers.
