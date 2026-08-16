# Benzene.Mesh.Artifacts

## What this package does
The HTTP surface over a mesh's `IMeshArtifactStore`, in both directions:

1. **Serving** a `Benzene.Mesh.Aggregator`'s generated catalog artifacts (`manifest.json`,
   `topology.json`, `topics.json`, `registry.json`, `services/*.json`, `usage.json`, `annotations.json`,
   `asyncapi.json`) by reading them from whichever `IMeshArtifactStore` the host has registered —
   filesystem, S3, Azure Blob Storage, GCS, or any future adapter. It's the read-side companion to
   `Benzene.Mesh.Ui`'s `mesh-ui.html`, which fetches `manifest.json` relatively and needs *something* to
   serve it when the mesh isn't deployed as plain static files next to the aggregator's output.
2. **Guarding** the endpoint that *regenerates* them (`UseMeshRefreshGuard`) — a CSRF check plus a
   throttle whose only state is the `generatedAtUtc` the aggregator already writes into `manifest.json`.
   It lives here because this is the one package at the (`Benzene.Http` × `IMeshArtifactStore`)
   intersection both halves need: `Benzene.Mesh.Aggregator` has no HTTP dependency, and
   `Benzene.Mesh.Ui` deliberately has no aggregator dependency (see below).

## Why this is its own package, not folded into `Benzene.Mesh.Ui`
`IMeshArtifactStore` lives in `Benzene.Mesh.Aggregator`. Folding artifact-serving into `Benzene.Mesh.Ui`
would make the UI package depend on the aggregator, breaking the "Contracts and Ui stay portable"
discipline recorded in `work/service-mesh-roadmap-1.0.md` §8. This package sits between them instead:
it depends on `Benzene.Mesh.Aggregator` so `Benzene.Mesh.Ui` doesn't have to.

## Origin
Extracted 2026-08 from five near-identical copies of `MeshArtifactMiddleware.cs` that had accumulated
under `examples/AwsMesh/Mesh/`, `AzureMesh`, `AzureFunctionsMesh`, `GoogleCloudMesh`, and `K8sMesh` —
each example wired its own artifact store (S3, Blob, GCS, filesystem) but needed the identical serving
logic on top. The `K8sMesh` copy had drifted: its `IsArtifact` allow-list was missing `usage.json` and
`annotations.json` that the other four already served, a copy-paste gap from when the artifact set was
extended, not a deliberate K8s restriction (the write side — `AddMeshAggregator`'s
`MeshAnnotationPublisher` registration — was identical across all five). Collapsing the five copies into
this package closed that gap for good rather than perpetuating it as a sixth thing to keep in sync.
`deploy/Mesh/Benzene.Mesh.Host` (the config-driven host, slice 1) is the reason this needed to exist as a
real package rather than staying inlined: it can select any store from config, and `UseStaticFiles` over
a `PhysicalFileProvider` only ever covers the filesystem case.

## Key types
- `MeshArtifactMiddleware<TContext> : IMiddleware<TContext> where TContext : IHttpContext` — short-
  circuiting middleware, same shape as `Benzene.Spec.Ui`'s `SpecUiMiddleware`. On a GET/HEAD to an
  artifact path it reads the key from the registered `IMeshArtifactStore` and writes it back as
  `application/json` (404 with a small JSON error body when absent); on OPTIONS to an artifact path it
  answers the CORS preflight; anything else falls through to `next()`.
- `MeshArtifactExtensions.UseMeshArtifacts<TContext>(this IMiddlewarePipelineBuilder<TContext>,
  CorsSettings? corsSettings = null)` — registers the middleware on any Benzene HTTP pipeline. Matches
  `Benzene.Mesh.Ui`'s `MeshUiExtensions.UseMeshUi` wiring shape: `app.Register(x =>
  x.AddSingleton(resolver => new MeshArtifactMiddleware<TContext>(...)))` then
  `app.Use<TContext, MeshArtifactMiddleware<TContext>>()`. Pass `corsSettings` (e.g. so the AsyncAPI
  Studio deep-link can fetch `asyncapi.json` cross-origin) — omit it to serve with no CORS headers.
- `MeshRefreshGuardMiddleware<TContext>` + `MeshRefreshGuardOptions` +
  `MeshArtifactExtensions.UseMeshRefreshGuard<TContext>(options?)` — the gate in front of the endpoint
  that triggers an aggregation pass (see below). Registered **scoped**, not singleton (unlike everything
  else here), because it resolves the request-scoped `IRouteFinder`.

## The refresh guard (`UseMeshRefreshGuard`)
A pass is expensive — it enumerates the platform's services, interrogates every one of them, and
rewrites the whole catalog — so authentication alone doesn't bound it. Two checks, in this order:

1. **CSRF: a required custom header** (`X-Benzene-Refresh`, a fixed contract with `mesh-ui.html`, which
   sends `X-Benzene-Refresh: 1`). A cross-site `<form method="post">` cannot set a custom header at all,
   and a cross-origin `fetch()` that sets one triggers a CORS preflight no mesh pipeline approves — so
   only genuine same-origin callers get through. Missing: `403`, generic body, **zero I/O**. That
   ordering is deliberate: a caller who couldn't set the header learns nothing about catalog state and
   costs nothing to refuse. The header's *value* is not inspected — what defends against CSRF is that a
   cross-site caller cannot set the header at all, not what it would have set.
2. **Throttle**: the last pass's `generatedAtUtc` is read back from `manifest.json`; inside
   `MinimumInterval` (default 30s) the request gets `429` + `Retry-After` and no pass runs.

**Matching** is `path == Options.Path` **OR** `IRouteFinder` resolves the request to `Options.Topic`
(default `MeshAggregatorTopics.Aggregate`). The path comparison replicates `RouteFinder`/`UrlMatcher`'s
own normalization exactly (query string dropped, empty segments removed, case-insensitive), so
`/mesh/refresh/`, `//mesh//refresh`, `/MESH/REFRESH` and `/mesh/refresh?x=1` are all guarded — this
equivalence is asserted directly in `MeshRefreshRoutingTest.GuardNormalization_AgreesWithTheRouter`. The
topic arm catches what the path arm can't see: a second `[HttpEndpoint]` alias, or the version-prefixed
route `AddHttpVersioning()` synthesises. Matching is **not** restricted to POST, so adding a GET alias
later can't silently open a CSRF hole (`SameSite=Lax` still sends cookies on a top-level GET navigation).

**Two things it is not, and must never be described as:**
- **Not a distributed lock.** It reads a timestamp, decides, then runs — so two requests arriving close
  enough together both read the same stale value and both proceed. It bounds *sustained* abuse (roughly
  one pass per window over any period longer than the window), which is the cost- and load-shaped threat;
  it does not guarantee single-flight, and concurrent passes can still race on the same artifact keys.
  The trade buys **zero new infrastructure** — no lock table, no Redis, no lease. Real single-flight is a
  different mechanism (an S3 conditional write / `If-None-Match` lease, a DynamoDB conditional put).
- **Not fail-closed.** A missing/unreadable/unparseable manifest allows the pass, because the first
  refresh after a deploy has no manifest to read and failing closed would brick a fresh deployment
  (`mesh-example-aws-deploy.yml` triggers exactly that first pass). An attacker able to delete or corrupt
  `manifest.json` therefore disables the throttle — but they already have write access to the artifact
  store at that point, which is strictly worse than an unthrottled refresh.

**Wiring order matters in both directions:** after whatever authenticates the pipeline (in front of it,
it would answer `403` to anonymous callers that should get `401`) and before the message-handler
middleware (behind it, it would never run).

**A related sharp edge this guard cannot cover.** Handler discovery is a single process-wide union of
every `AddMessageHandlers` call, so a `UseBenzeneMessage` endpoint routes *any* registered topic
regardless of the handler-type list its inner pipeline was configured with — including the aggregate
topic, down a path with no HTTP route and so no guard. A host that exposes both must set
`BenzeneMessageHttpOptions.TopicFilter`; `examples/AwsMesh/Mesh/Startup.cs` restricts its
`/benzene/invoke` endpoint to `benzene:mesh:query:*` for exactly this reason, and
`AwsMeshRefreshEndpointTest` pins both halves.

## The artifact allow-list (`IsArtifact`)
`manifest.json`, `topology.json`, `topics.json`, `registry.json`, `asyncapi.json`, `usage.json`,
`annotations.json`, and anything under `services/*.json`. This is the **full** set the aggregator can
produce; it is deliberately not narrowed per deployment (e.g. `K8sMesh` wires no usage source today, so
`usage.json` is moot there right now — but the allow-list stays generic because a future config-driven
host can wire a usage source under any hosting model, and the serving layer shouldn't need to know which
sources a given deployment happens to have wired).

## Dependencies
- **Benzene.Mesh.Aggregator** — `IMeshArtifactStore` (the port this middleware reads from).
- **Benzene.Http** — `IHttpContext`, `IHttpRequestAdapter<T>`, `IBenzeneResponseAdapter<T>`,
  `Benzene.Http.Cors` (`CorsSettings`/`CorsOriginChecker`). `Benzene.Core.Middleware`'s
  `Use<TContext, TMiddleware>()` extension comes in transitively through `Benzene.Http` →
  `Benzene.Core.MessageHandlers` → `Benzene.Core.Middleware` (not referenced directly, matching
  `Benzene.Mesh.Ui`'s convention).

## When to use
Wire `UseMeshArtifacts()` into any Benzene HTTP pipeline that also registers an `IMeshArtifactStore` and
wants to serve the aggregator's output live (rather than, or in addition to, publishing the artifacts to
a static file host / CDN). Every mesh example under `examples/` (`AwsMesh`, `AzureMesh`,
`AzureFunctionsMesh`, `GoogleCloudMesh`, `K8sMesh`) does this alongside `Benzene.Mesh.Ui`'s `UseMeshUi()`
and `UseMeshSpecUi()`, each with its own store adapter registered ahead of it.

## Tests
- `test/Benzene.Mesh.Test/MeshArtifactMiddlewareTest.cs` — a hand-written fake `IMeshArtifactStore`
  covering: a known path returns the stored content (200, `application/json`); an unknown path (not in
  the allow-list) falls through to `next()` without touching the store; a directory-traversal attempt
  (`../`-prefixed / containing path) is refused (falls through to `next()`, same as any other
  non-matching path — the store is never asked to resolve it).
- `test/Benzene.Mesh.Test/MeshRefreshGuardMiddlewareTest.cs` — the guard in isolation: header
  present/absent/blank and every casing of its name, the value deliberately not inspected, every path
  spelling, every HTTP method, the route-alias arm, throttle inside/outside/exactly-on the window,
  future-dated manifests, every fail-open case (absent/blank/not-JSON/no-field/store-throws), and that a
  `403` never touches the store.
- `test/Benzene.Mesh.Test/MeshRefreshRoutingTest.cs` — the routing-layer guarantee (`POST` only, nothing
  else routes) and the guard-vs-router normalization equivalence.
- `test/Benzene.Mesh.Test/AwsMeshRefreshEndpointTest.cs` — the same protections end-to-end through the
  real API Gateway host, mirroring `examples/AwsMesh/Mesh/Startup.cs`'s wiring (examples aren't in the CI
  gate, so this library-side test stands in for it, exactly as `AwsMeshFleetEndpointTest` does).
- `InternalsVisibleTo(Benzene.Mesh.Test)` exists solely so the normalization-equivalence test can reach
  `MeshRefreshGuardMiddleware.Canonicalize` — same convention as `Benzene.Mesh.Auth.Oidc`.

## Conventions
- Keep this package's only real dependency on `Benzene.Mesh.Aggregator` — it exists specifically to
  avoid pulling the aggregator into `Benzene.Mesh.Ui`. Don't add a reference the other direction.
- The allow-list is the single source of truth for "what counts as a mesh artifact" across every
  hosting model. If the aggregator grows a new artifact, add it here once, not per example.
- `MeshRefreshGuardOptions.HeaderName`'s default (`X-Benzene-Refresh`) and `Path`'s default
  (`/mesh/refresh`, mirrored by `MeshUiExtensions.DefaultRefreshUrl`) are a **contract with the vendored
  `mesh-ui.html`**, not free parameters. Renaming either without re-vendoring a UI that matches silently
  breaks the Refresh button — the page would POST and get a `403` it can't explain.
