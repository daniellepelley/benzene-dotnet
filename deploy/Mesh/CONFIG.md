# `mesh.json` configuration reference

Every key `Benzene.Mesh.Host` reads, in one place, so a platform engineer can write a working
`mesh.json` without opening a `.cs` file. The primary path is a bind-mounted `mesh.json` (env var
`MESH_CONFIG_PATH` points at it); individual top-level scalars can also be set via plain environment
variables (`Host.CreateDefaultBuilder`'s default sources, .NET's double-underscore nesting, e.g.
`Fleet__Source=xray`) — see [`README.md`](README.md) for that mechanism and everything else about
running the host. This document only covers what each key means and what it defaults to.

**Verify a config before deploying it:** `benzene-mesh --validate-config` (below, and see
[`README.md`](README.md#--validate-config)) binds and validates `mesh.json` without starting the
host, using the exact same rules described here.

## The two invariants

1. **Credentials never go in `mesh.json`.** Every section below names an *endpoint* at most — a
   bucket, a workspace id, a Tempo URL — never a secret. Cloud-backed sections authenticate off the
   container's ambient credential chain (an IAM role for AWS, a managed identity for Azure via
   `DefaultAzureCredential`, the attached service account for Google Cloud via Application Default
   Credentials). Where a credential genuinely has to exist (auth's `basic`/`oidc` modes, the
   ingestion shared secret), it comes from a **named environment variable**, never a config value.
2. **Unknown source/type names fail at startup, listing the valid values.** A typo in `source` or
   `type` never silently falls back to a default — it throws `InvalidOperationException` naming what
   was given and what would have been accepted. `--validate-config` runs the identical check, so a
   typo is caught before a deploy, not after one.

## `artifactRootDirectory` / `pollIntervalSeconds` — top-level scalars

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `artifactRootDirectory` | string | `"mesh-artifacts"` | Only used when `artifactStore.type` is `file` (the default) | Local-disk root the aggregator writes `manifest.json`/`services/*.json`/`topology.json`/etc. to, and where `/artifacts/*` is served from. Bind-mount a volume here for persistence across container restarts. |
| `pollIntervalSeconds` | int | `60` | Always applies | How often the background poll loop (`MeshPollBackgroundService`) runs a full aggregation pass. |

## `artifactStore` — where generated catalog artifacts live

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `artifactStore.type` | string: `file` \| `s3` \| `azureBlob` \| `gcs` | `"file"` | Always applies | Which backend stores `manifest.json`/`services/*.json`/`topology.json`/etc. `file` reads/writes `artifactRootDirectory` on local disk, served at `/artifacts/*` via ASP.NET static files; the other three read/write the named cloud store, served at the artifact's own root-relative path (e.g. `/manifest.json`) via `Benzene.Mesh.Artifacts`. |
| `artifactStore.options.bucket` | string | — | `type` is `s3` or `gcs` | The bucket name. |
| `artifactStore.options.prefix` | string | `""` | Optional for `s3`/`azureBlob`/`gcs` | Key/blob prefix under the bucket/container, if you want the mesh's artifacts namespaced. |
| `artifactStore.options.blobServiceUri` | string | — | `type` is `azureBlob` | The Azure Storage account's blob service endpoint, e.g. `https://myaccount.blob.core.windows.net`. |
| `artifactStore.options.container` | string | — | `type` is `azureBlob` | The blob container name. |

## `services` — the polled service registry (array)

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `services[].name` | string | `""` | Always, per entry | The service's name — how it's identified everywhere else (registry-document union, dispatch, the UI). |
| `services[].specUrl` | string? | `null` | Required for `source: "Http"`; display-only otherwise | Where the aggregator fetches the Benzene spec document from. |
| `services[].healthUrl` | string? | `null` | Required for `source: "Http"`; display-only otherwise | Where the aggregator polls health from. |
| `services[].source` | string: `Http` \| `AwsLambdaInvoke` | `"Http"` | Optional (omit for the common HTTP case) | Which `IMeshServiceSource` fetches this entry — see `Benzene.Mesh.Contracts.MeshServiceSource`. |
| `services[].sourceOptions.functionName` | string | — | `source` is `AwsLambdaInvoke` | The Lambda function name (or ARN) to invoke. |
| `services[].sourceOptions.region` | string | ambient region | Optional for `AwsLambdaInvoke` | Overrides the AWS region the Lambda client targets. |
| `services[].owningTeam` | string? | `null` | Optional | The "who do I talk to" field the Mesh UI renders on each service card. |

## `registryDocuments` — discovery-generated service lists (array of strings)

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `registryDocuments` | string[] | `[]` (none) | Optional | Zero or more locations of discovery-generated registry documents (written by the separate `../Discovery` deployable), each a path resolved through this host's own `artifactStore` and read back via `IMeshArtifactStore.TryReadAsync`. Read once at startup and unioned with `services` — **`services` always wins a name clash** ("discovery proposes, config disposes"). A document that's missing or unparseable is logged and skipped; if `registryDocuments` is non-empty and **nothing** could be read, the host fails to start. |

## `usage` — per-topic traffic feeds (array — zero or more)

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `usage[].source` | string: `cloudwatch` \| `applicationInsights` | — | Always, per entry | Which `IMeshUsageSource` to register. Several may be configured at once (`IMeshUsageSource` resolves as `IEnumerable<>`) — e.g. a CloudWatch feed and an Application Insights feed on a deployment that spans both clouds. |
| `usage[].options.namespace` | string | `"Benzene/Mesh"` | Optional, `cloudwatch` only | The CloudWatch metrics namespace to read from. |
| `usage[].options.workspaceId` | string | — | Required, `applicationInsights` only | The Log Analytics workspace id (GUID) — **not** the App Insights instrumentation key. |
| `usage[].options.metricName` | string | `"benzene.messages.processed"` | Optional, either source | The counter's metric name. |
| `usage[].options.windowHours` | number | `24` | Optional, either source | The lookback window the counts cover. |
| `usage[].options.topicDimension` | string | `"topic"` | Optional, either source | The dimension/tag name carrying the topic. |
| `usage[].options.transportDimension` | string | `"transport"` | Optional, either source | The dimension/tag name carrying the transport. |
| `usage[].options.resultDimension` | string | `"result"` | Optional, either source | The dimension/tag name carrying the result (success/failure). |
| `usage[].options.periodSeconds` | int | `60` | Optional, `cloudwatch` only | The CloudWatch metric period. |

An empty/omitted `usage` array means no usage feed — honestly empty, not fabricated.

## `fleet` — the live-traffic view's data source (an object, not an array)

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `fleet.source` | string: `none` \| `xray` \| `tempo` \| `jaeger` | `"none"` | Always applies | Which live trace source backs the Fleet plane. `none` means no live plane — the dashboard shows only the declared catalog. Deliberately an object: `CompositeMeshFleetReadModel` composes a single trace source, so only one can be configured (see "Known limitations" in `README.md`). |
| `fleet.options.url` | string | — | `source` is `tempo` or `jaeger` | The query endpoint (Tempo's or Jaeger's HTTP API). |
| `fleet.options.correlationLookbackHours` | number | `24` | Optional, `xray`/`tempo`/`jaeger` | The lookback window used to correlate a trace by a caller-supplied correlation id. |
| `fleet.options.recentFlowsLookbackHours` | number | `1` | Optional, `xray`/`tempo`/`jaeger` | The lookback window for the "what's flowing now" landing view — shorter than the correlation window on purpose. |
| `fleet.options.recentFlowsServiceEnrichmentMax` | int | `20` | Optional, `xray` only | Caps how many recent-flow entries get per-service enrichment (0 disables enrichment). |
| `fleet.options.services` | string (comma-separated) | discover all | Optional, `jaeger` only | Pins the search to specific service names instead of discovering them from Jaeger. |
| `fleet.options.searchLimitPerService` | int | `20` | Optional, `jaeger` only | Caps how many traces are searched per service. |

When `fleet.source` is anything but `none`, the host also wires the read-only `mesh:query:*`
handlers over an inner `/benzene/invoke` BenzeneMessage endpoint and points the mesh UI's live Fleet
plane at it.

## `topology` — the service-graph view's extra (observed-traffic) edges

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `topology.source` | string: `none` \| `tempo` | `"none"` | Always applies | `none`: only the structural edges the aggregator always derives from each service's declared providers/consumers. `tempo`: adds `source: "tempo"` edges with real traffic stats, from Tempo's service-graph metrics via a Prometheus-compatible query endpoint. |
| `topology.options.prometheusUrl` | string | — | `source` is `tempo` | The Prometheus-compatible instant-query endpoint Tempo's metrics-generator remote-writes to, e.g. `http://prometheus:9090/api/v1/query`. |
| `topology.options.windowMinutes` | number | `5` | Optional, `tempo` only | The lookback window used in each PromQL `rate(...)` query. |

## `dispatch` — opt-in live dispatch

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `dispatch.enabled` | bool | `false` | Always applies | Wires `mesh:dispatch` (invokes a registered service's **real** handler with a chosen payload — real side-effects execute). Off by default: this is a deliberate, non-default choice. |
| `dispatch.allowInProduction` | bool | `false` | Only checked when `dispatch.enabled` is `true` | The second gate: even with `enabled: true`, dispatch refuses to run in a Production environment (an unset environment counts as Production) unless this is also `true`. |

## `auth` — who may reach the dashboard

| Key | Type | Default | Required when | What it does |
|---|---|---|---|---|
| `auth.mode` | string: `none` \| `proxy` \| `basic` \| `oidc` | `"none"` | Always applies | `none` leaves the host exactly as it was pre-slice-2: no login, everything world-readable. **Do not expose the host on a network you don't trust with `mode: "none"`.** One gate (`MeshAuthGate`) protects both `/artifacts/*` (served outside the Benzene pipeline when `artifactStore.type` is `file`) and everything inside the pipeline, in every mode. |
| `auth.allowedEmailDomains` | string[] | `[]` (any) | Optional | If non-empty, an authenticated caller's email domain must be in this list or the request is `403 Forbidden`. |
| `auth.requiredGroups` | string[] | `[]` (any) | Optional | If non-empty, an authenticated caller must hold at least one of these groups/roles (read from a `groups` claim or common role claim types) or the request is `403 Forbidden`. |
| `auth.dispatchRole` | string? | `null` | Optional | When set (and `dispatch.enabled` is `true`), additionally requires this role/group for `mesh:dispatch` specifically — enforced by `MeshAuthGate` directly against `mesh:dispatch`'s own envelope path (`Startup.cs`'s `UseBenzeneMessage` mount, guarded by `UseMeshDispatchGuard`). See `README.md`'s note. |
| `auth.proxy.userHeader` | string | `"X-Forwarded-User"` | Optional, `mode: "proxy"` only | The request header carrying the already-authenticated identity from an upstream front door (oauth2-proxy, ALB+Cognito, Azure App Proxy). |
| `auth.proxy.trustedProxies` | string[] | `[]` | **Required (non-empty), `mode: "proxy"` only** | The peer addresses allowed to set `userHeader`. Empty means the host refuses to start — an unrestricted forwarded-identity header is a total authentication bypass. |
| `auth.oidc.authority` | string? | `null` | **Required, `mode: "oidc"` only** | The OIDC authority (issuer) URL; its `/.well-known/openid-configuration` document drives discovery. |
| `auth.oidc.clientId` | string? | `null` | **Required, `mode: "oidc"` only** | The OAuth2/OIDC client id registered with the authority. |
| `auth.oidc.clientSecretEnvVar` | string | `"MESH_OIDC_CLIENT_SECRET"` | The **named** environment variable must be set when `mode: "oidc"` | The **name** of the environment variable holding the client secret — never the secret itself (`mesh.json` gets committed to customers' repos). |
| `auth.oidc.callbackPath` | string | `"/signin-oidc"` | Optional, `mode: "oidc"` only | The path the authority redirects back to after login. |
| `auth.oidc.scopes` | string[] | `["openid", "profile", "email"]` | Optional, `mode: "oidc"` only | The OIDC scopes requested at login. |
| `auth.ingestion.mode` | string: `open` \| `sharedSecret` | `"open"` | Always applies | Independent of `auth.mode` — `/mesh/report` is a service self-reporting, not a browser session. `open`: no check (today's behaviour). `sharedSecret`: the request must carry `X-Mesh-Ingest-Secret` matching the `MESH_INGEST_SECRET` environment variable (compared in constant time). |

**`POST /mesh/auth/logout`** — always present in `mode: "oidc"`, not itself config-gated. Requires the
custom header `X-Benzene-Logout` (any non-empty value — the same CSRF convention as
`X-Benzene-Refresh`/`X-Benzene-Dispatch`: a cross-site request cannot set a custom header at all).
GET is rejected (`405`) — a GET-triggered logout is itself a CSRF hazard. Signs out the session cookie
and answers `{"redirect": <url or null>}`: the URL is the authority's discovered `end_session_endpoint`
(with `post_logout_redirect_uri`) when discovery provides one, else `null` (local sign-out only). The
mesh UI's Sign-out control (shown automatically once `mode: "oidc"` wires `logoutUrl` into
`UseMeshUi`) POSTs here, then navigates to `redirect` if non-null, else reloads.

## Which options work under which auth modes

Not every `auth.*` option is satisfiable under every `auth.mode` — some need a mode that establishes
group/role claims or an email-bearing identity, or (for `dispatch.enabled`) any identity at all. An
unsatisfiable combination is rejected at startup (`MeshAuthGate.Validate`, also run by
`--validate-config`), naming the offending key(s) and the mode — never silently ignored or
silently under-enforced. See `work/bug-fix-designs-2026-08.md`'s "WP-1" for the ruling.

| Option set | `none` | `basic` | `proxy` (no groupsHeader) | `proxy` (+groupsHeader) | `oidc` |
|---|---|---|---|---|---|
| `RequiredGroups` | ✗ reject | ✗ reject | ✗ reject | ✓ | ✓ |
| `dispatchRole` | ✗ reject (exists) | ✗ reject (#27) | ✗ reject (#6) | ✓ | ✓ |
| `AllowedEmailDomains` | ✗ reject (#3) | ✗ reject | ✓ | ✓ | ✓ |
| `dispatch.enabled` | ✗ reject (#19) | ✓ | ✓ | ✓ | ✓ |

Rationale: group/role options (`requiredGroups`, `dispatchRole`) need a mode that can carry group
claims (`oidc`, or `proxy` with `auth.proxy.groupsHeader` configured). `allowedEmailDomains` needs an
email-bearing identity (`proxy`/`oidc`) — under `basic` the operator defines the one account
themselves, so domain-filtering it is meaningless, and it is rejected rather than silently ignored.
`dispatch.enabled` needs *any* established identity, since `MeshDispatchGate`'s identity check is
fail-closed — `none` establishes none at all, so it is rejected; `basic` is allowed (its Name-claim
identity satisfies the guard).

**`basic` stays a deliberately minimal single-account mode — there is no `MESH_BASIC_ROLES` knob.**
Anyone needing roles has outgrown `basic` and should use `proxy`/`oidc`; do not add that knob without
first amending the WP-1 ruling.

**Authorization**, once authenticated (any mode): a caller who authenticates but fails
`allowedEmailDomains`/`requiredGroups` gets `403 Forbidden`, not `401 Unauthorized`. There is no
per-service RBAC in v1 — authenticated and permitted means full read access to the whole catalog.

**The residual gap, stated plainly:** with `auth.ingestion.mode: "open"` (the default) and
`auth.mode` set to anything else, the **read** surface (the dashboard, the catalog) is protected and
the **write** surface (`/mesh/report`) is not — any caller who can reach the host can inject a report
for any service name. Set `ingestion.mode: "sharedSecret"` to close that surface too.

**Credentials referenced above, never in `mesh.json`:**

| Env var | Required when |
|---|---|
| `MESH_BASIC_USER` / `MESH_BASIC_PASSWORD` | `auth.mode: "basic"` |
| The variable named by `auth.oidc.clientSecretEnvVar` (default `MESH_OIDC_CLIENT_SECRET`) | `auth.mode: "oidc"` |
| `MESH_INGEST_SECRET` | `auth.ingestion.mode: "sharedSecret"` |

## Worked examples

### Filesystem + HTTP services (the Compose case)

No cloud credentials needed — matches [`mesh.sample.json`](mesh.sample.json) and
[`examples/K8sMesh/compose/mesh.json`](../../examples/K8sMesh/compose/mesh.json):

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

Everything else (`artifactStore`, `usage`, `fleet`, `topology`, `dispatch`, `auth`) is left at its
default: local-disk artifacts, no usage feed, no live Fleet plane, no dispatch, no login.

### S3 + Lambda-invoked services + X-Ray + CloudWatch (the AWS case)

Mirrors `examples/AwsMesh/Mesh/Startup.cs`'s wiring — see
`deploy/Mesh/Benzene.Mesh.Host.Test/AwsMeshParityTest.cs` for the test that proves every one of
these resolves from config alone. All credentials come from the container's IAM role; nothing here
is a secret:

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
Lambda functions to poll) — deliberate, see [`README.md`](README.md#what-it-does)'s "what this host
will never do".

## Per-source least-privilege permission matrix

Every credential comes from the container's ambient credential chain (AWS IAM role, Azure managed
identity, Google Cloud service account) — never from `mesh.json`. Grant only what the sections you
actually enable need. See
[`../Discovery/README.md`](../Discovery/README.md#least-privilege-permission-matrix) for the
separate, narrower permission matrix `../Discovery` needs — deliberately non-overlapping with this
one on the interrogation axis (`lambda:InvokeFunction` and friends belong only to this host's role,
never to discovery's).

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
| `dispatch.enabled: true` | Whatever `services[].source` dispatches through (today: AWS Lambda `Invoke`) | Same as the matching `services[].source` row above | The specific dispatchable services — and see the "off by default, real side-effects" warning above before granting this at all |

This is a first version, meant to be reviewed by `aws-product-owner` before it reaches customers.
