# Benzene.Mesh.Artifacts

## What this package does
Serves a `Benzene.Mesh.Aggregator`'s generated catalog artifacts (`manifest.json`, `topology.json`,
`topics.json`, `registry.json`, `services/*.json`, `usage.json`, `annotations.json`, `asyncapi.json`)
over HTTP by reading them from whichever `IMeshArtifactStore` the host has registered — filesystem, S3,
Azure Blob Storage, GCS, or any future adapter. It's the read-side companion to `Benzene.Mesh.Ui`'s
`mesh-ui.html`, which fetches `manifest.json` relatively and needs *something* to serve it when the mesh
isn't deployed as plain static files next to the aggregator's output.

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
`test/Benzene.Mesh.Test/MeshArtifactMiddlewareTest.cs` — a hand-written fake `IMeshArtifactStore`
covering: a known path returns the stored content (200, `application/json`); an unknown path (not in
the allow-list) falls through to `next()` without touching the store; a directory-traversal attempt
(`../`-prefixed / containing path) is refused (falls through to `next()`, same as any other
non-matching path — the store is never asked to resolve it).

## Conventions
- Keep this package's only real dependency on `Benzene.Mesh.Aggregator` — it exists specifically to
  avoid pulling the aggregator into `Benzene.Mesh.Ui`. Don't add a reference the other direction.
- The allow-list is the single source of truth for "what counts as a mesh artifact" across every
  hosting model. If the aggregator grows a new artifact, add it here once, not per example.
