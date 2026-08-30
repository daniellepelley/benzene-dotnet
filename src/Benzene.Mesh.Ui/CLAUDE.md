# Benzene.Mesh.Ui

## Vendored bundle — read this before touching anything in this package

**`mesh-ui.html` and `mesh-spec-ui.html` are a minified React + Redux Toolkit build, vendored
verbatim from the external [`benzene-ui`](https://github.com/daniellepelley/benzene-ui) repository.**
They are not hand-written vanilla JS, and nothing below should be read as a description of their
internal implementation — verify the running page (or the `benzene-ui` source) before trusting any
implementation-level claim about them.

- **Never hand-edit `mesh-ui.html` or `mesh-spec-ui.html`.** Any UI change — a new feature, a copy
  fix, a bug fix in client-side behavior — is made upstream in `benzene-ui` and re-vendored here by
  copying its `build/mesh-ui.html` and `build/mesh-spec-ui.html` output over these two files.
- **`.github/workflows/mesh-ui-drift-check.yml`** enforces this: it clones `benzene-ui`, builds the
  canonical pages, and fails CI if any vendored copy in this repo (discovered by scanning for large
  HTML files carrying the page's `<html lang="en">` opening tag — not a hardcoded path list) doesn't
  byte-match one of them. A local edit here will pass review only to be silently overwritten the next
  time someone re-vendors, or it will fail the drift check outright.
- `docs/capability-matrix.md` records this package's copy as byte-identical to the vendored copy the
  cross-language spec/website repo also carries — both trace back to the same `benzene-ui` build.
- Everything in this file about `MeshUiPage`, `MeshUiMiddleware`, `MeshUiExtensions`,
  `MeshSpecUiPage`, `MeshSpecUiMiddleware` and how to deploy them is real, current, server-side C#
  that lives in this repo and is safe to edit normally — the vendoring restriction is scoped to the
  two `.html` files only.

## What this package does
Serves a self-contained, catalog-style web viewer for a **Benzene service mesh** - the
`manifest.json`/`services/{name}.json` artifacts produced by `Benzene.Mesh.Aggregator`. It shows
every registered service's health status and contract-drift flag at a glance, with a per-service
drill-down into health check detail. Optionally, when a host wires the corresponding endpoints, the
page also enriches the static catalog with a live Fleet plane (health/traffic polled from
`Benzene.Mesh.Collector`), a Test Console that can dispatch a real message (`Benzene.Mesh.Dispatch`),
sign-out (`Benzene.Mesh.Auth.Oidc`), and an on-demand refresh/aggregation trigger
(`Benzene.Mesh.Artifacts`'s refresh guard). The exact feature set and behavior of all of this lives in
`benzene-ui`, not here — the paragraphs below describe only the server-side contract this package
exposes to configure it.

This package renders the catalog; it does **not** generate it. Generation lives in
`Benzene.Mesh.Aggregator`. It mirrors `Benzene.Spec.Ui`'s exact shape and philosophy, one level up
(catalog-of-services rather than catalog-of-topics).

## Key types
- `MeshUiPage` — transport-agnostic accessor for the viewer HTML.
  - `GetHtml()` — the page as-is (falls back to an embedded sample manifest, or a `?url=` query param).
  - `GetHtml(string manifestUrl)` — injects a `data-manifest-url` onto the document root so the
    page fetches and renders that manifest on load.
  - `GetHtml(string? manifestUrl, string? envelopeUrl)` — additionally injects a `data-fleet-url`
    when `envelopeUrl` is set, so the page's live **Fleet plane** feature-detects it and enriches the
    catalog with `mesh:query:*` data polled from that wire-envelope endpoint. `envelopeUrl` null →
    the static catalog viewer, Fleet plane dormant.
  - `GetHtml(string? manifestUrl, string? envelopeUrl, string? dispatchUrl)` — additionally injects
    `data-dispatch-url`, which the **Test Console** feature-detects to offer a real send.
  - `GetHtml(string? manifestUrl, string? envelopeUrl, string? dispatchUrl, string? logoutUrl, string? refreshUrl, string? environment = null)`
    — additionally injects `data-logout-url` (the page renders a **Sign out** control), `data-refresh-url`
    (the page renders a **Refresh** control, plus a self-service empty state offering to run the first
    pass), and `data-environment` (the page renders which estate — production/staging/etc. — it's
    looking at; free text, deliberately never inferred from a hostname). Every value is HTML-encoded
    into the `<html>` tag's attribute list, and each is independently optional: an attribute that
    isn't injected leaves that control unrendered, because the page feature-detects each one
    separately.
- `MeshUiMiddleware<TContext> : IMiddleware<TContext> where TContext : IHttpContext` — transport-
  agnostic HTTP middleware, same short-circuit shape as `Benzene.Spec.Ui`'s `SpecUiMiddleware`. One
  ctor per `GetHtml` overload shape, each delegating to the widest one.
- `MeshUiExtensions.UseMeshUi<TContext>(this IMiddlewarePipelineBuilder<TContext>, path = "/mesh-ui", manifestUrl = "manifest.json", envelopeUrl = null, dispatchUrl = null, logoutUrl = null, refreshUrl = null, environment = null)`
  — registers the middleware on any Benzene HTTP pipeline. This is a **secondary convenience**,
  not the primary deployment path (see below). Pass `envelopeUrl` (e.g. `"/benzene/invoke"`) on a
  mesh host that also serves a `Benzene.Mesh.Collector` to fold the live Fleet plane into the catalog.

### Every capability parameter is an explicit opt-in
`dispatchUrl`, `logoutUrl`, `refreshUrl` and `environment` all follow the same rule: **none may be
inferred** from another being set, from auth happening to be wired, or from an aggregator being
registered. A parameter that turns a read-only viewer into a page that *acts* on the estate, or that
labels the estate, has to be a sentence the host wrote deliberately.
- `refreshUrl` (`MeshUiExtensions.DefaultRefreshUrl` = `/mesh/refresh`) is the sharpest case: per the
  page's own copy, it adds a button that fans out to every service in the mesh and rewrites the whole
  catalog on each press — real money per click. Passing it is also an implicit statement that the host
  guards that endpoint; `Benzene.Mesh.Artifacts`' `UseMeshRefreshGuard()` is the matching server side,
  and the page's POST carries the `X-Benzene-Refresh: 1` header that guard requires. **The header name
  and the default path are a contract with the vendored bundle** — change one end without the other
  and the button gets a `403` it cannot explain.
- `logoutUrl` deliberately has no constant default: the route is `Benzene.Mesh.Auth.Oidc`'s configurable
  `BasePath` plus `/logout`, and only the host knows its `BasePath`. Left null (the right default for an
  ungated host) no Sign-out control renders — a page nobody had to log into has nothing to sign out of.
- `environment` has no default either: nothing publishes an environment name until
  `placement.environment` reaches the spec, so the host must supply it explicitly or the page says the
  environment is not published — it never guesses from a hostname.
- `MeshSpecUiPage` / `MeshSpecUiMiddleware<TContext>` / `UseMeshSpecUi<TContext>(path =
  "/mesh-spec-ui.html", manifestUrl = "manifest.json")` — the **mesh-hosted per-service Spec UI**
  (page: `mesh-spec-ui.html`), the target of `mesh-ui.html`'s per-service *spec* link. It renders a
  single service's Benzene spec — the same Swagger-style view as `Benzene.Spec.Ui`'s `spec-ui.html` —
  but reads the spec the aggregator already captured into the **same-origin** `services/{name}.json`
  snapshot (`MeshServiceSnapshot.specJson`), unwrapping it client-side. So a mesh service only ever
  serves its spec as **JSON** (the Cloud Service contract) — it never has to host any HTML, and there
  is no cross-origin fetch. Opened as `mesh-spec-ui.html?service=<name>&manifest=<url>`, with a
  `?url=<specUrl>` direct-spec fallback also honoured.

## Primary deployment target: a static file host, not a Benzene pipeline
Unlike `Benzene.Spec.Ui` (which is served by the exact service whose spec it shows),
`Benzene.Mesh.Aggregator`'s output is typically generated by one process and consumed from
wherever it's published (local disk, blob storage, a CDN) - there's usually no single "the mesh
service" to serve this page from. The realistic deployment is: copy `mesh-ui.html` into the same
directory/bucket the aggregator writes `manifest.json`/`services/*.json` to, and serve all of it
as static files. `MeshUiMiddleware`/`UseMeshUi` exist for the secondary case where you do want to
serve it from a live Benzene app (local demo, or an aggregator host self-serving its dashboard).

## The viewer (`mesh-ui.html` / `mesh-spec-ui.html`)
Both files are self-contained builds (all CSS/JS inlined, no CDN/webfont/external script references,
so they work offline and behind strict CSPs) embedded as resources (`LogicalName`
`Benzene.Mesh.Ui.mesh-ui.html` / `Benzene.Mesh.Ui.mesh-spec-ui.html`). That "self-contained, no
external requests" property is the one client-side characteristic this doc asserts with confidence,
because it's externally observable and doesn't depend on knowing the bundle's internals — everything
else about what the pages render and how is `benzene-ui`'s documentation to own, not this file's.

At a high level, from what the C# API surface above exists to configure, the estate viewer:
- Renders the static catalog (services, health, contract drift, topics, topology) from the
  aggregator's artifacts, with no live endpoint required.
- Optionally enriches that catalog with a live Fleet plane, a Test Console capable of dispatching a
  message, a Sign-out control, and a Refresh control — each strictly gated behind its own opt-in
  parameter as described above.
- Links out to `mesh-spec-ui.html` for a per-service spec view, sourced from the same-origin
  `services/{name}.json` snapshot rather than a cross-origin fetch to the service itself.

Do not extend this list from memory or by inspecting the minified bundle — treat the running page (or
`benzene-ui`'s own source/docs) as the source of truth for anything more specific than this, and treat
any older, more detailed version of this section (implementation walkthroughs, internal function
names, dated "shipped" changelog entries) you might find in this file's history as unreliable: it
described a hand-written vanilla-JS predecessor and drifted out of sync with the current vendored
React + Redux Toolkit bundle without being corrected at the time.

## Known upstream items (fix in `benzene-ui`, then re-vendor — do not patch here)
These are real, confirmed gaps in the vendored bundle's client-side behavior. Because the bundle is
vendored verbatim (see above), they cannot be fixed by editing the `.html` files in this repo — any
such edit is exactly what the drift check exists to catch, and would be overwritten by the next
re-vendor regardless. Each needs a change in `benzene-ui`, a rebuild, and a re-vendor of both
`mesh-ui.html` copies (this repo's and the cross-language spec/website repo's) plus `mesh-spec-ui.html`
if touched.

- **#205 — Refresh has no confirmation step.** The Refresh control (see `refreshUrl` above) is
  documented by this very package as "real money per click" — it fans out to every service in the
  mesh and rewrites the whole catalog on every press — yet clicking it sends the request immediately
  with no confirmation. The Test Console's Send action, by contrast, requires an explicit checkbox
  before it will submit. Refresh should get an equivalent confirmation gate (a checkbox, an "are you
  sure" step, or similar) before its POST fires.
- **#206 — Sign-out has no pending/disabled state.** Refresh and Send both disable themselves (or show
  a pending state) while their request is in flight; Sign-out does not, so a rapid double-click can
  fire two concurrent logout requests. Give Sign-out the same disabled-while-pending treatment its
  siblings already have.
- **#207 — Sign-out's `fetch()` doesn't pass `credentials: "same-origin"` explicitly.** The other two
  write-action helpers (Refresh, Send) set this explicitly; Sign-out relies on the browser default
  (which is same-origin, so this is not an active bug) instead of stating it. Normalize Sign-out's
  fetch call to match its siblings for consistency and to avoid the omission reading as an oversight.

## Conventions
- Keep the viewer dependency-free from the *host's* perspective: no CDN/webfont/external script
  references in the served page, so it works offline and behind strict CSPs, matching
  `Benzene.Spec.Ui`'s convention. This is a constraint on the vendored build's output, not on how
  `benzene-ui` implements it internally.
- Do not add any implementation-level convention here about how the bundle is built (framework
  choice, whether it uses a chart library, module structure, etc.) — that is `benzene-ui`'s decision
  and its own docs' responsibility. This file's conventions section is scoped to the server-side
  packaging (`MeshUiPage`/`MeshUiMiddleware`/`MeshUiExtensions`) and to the vendoring discipline
  above.
