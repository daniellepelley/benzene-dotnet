# Benzene.Spec.Ui

## `spec-ui.html` is a vendored build output — read this before touching anything below

**`spec-ui.html` is NOT hand-written code that lives in this repo.** It is a minified React +
Redux Toolkit production bundle, built by the external
[`benzene-ui`](https://github.com/daniellepelley/benzene-ui) repo and committed here **verbatim**
as vendored output — the same trade `test/conformance-fixtures/` makes for the spec fixtures
(source of truth elsewhere, a byte-for-byte copy here because this repo's consumers aren't a
Node.js toolchain). It is **byte-identical** to `src/Benzene.Mesh.Ui/mesh-spec-ui.html` — both are
the same `benzene-ui` build output vendored into two packages — and CI's
`.github/workflows/mesh-ui-drift-check.yml` enforces that: on every push/PR/weekly schedule it
fetches `benzene-ui`'s canonical `build/mesh-spec-ui.html`, discovers every committed HTML page over
100KB by scanning for the page's opening-tag fingerprint, and fails the build if any of them isn't a
byte-for-byte match. There is no path allowlist to fall out of date — any new vendored copy
(another package, another example) is covered automatically.

**Never hand-edit `spec-ui.html` directly.** A local edit survives exactly until the next drift-check
run (or the next `benzene-ui` re-vendor overwrites it), and in the meantime CI is red. A change to
what the viewer does, looks like, or fetches belongs in `benzene-ui` — implement it there, run
`npm run build`, and re-vendor the output:
```
cp <benzene-ui>/build/mesh-spec-ui.html <this repo>/src/Benzene.Spec.Ui/spec-ui.html
cp <benzene-ui>/build/mesh-spec-ui.html <this repo>/src/Benzene.Mesh.Ui/mesh-spec-ui.html
```
(`benzene-ui`'s single `build/mesh-spec-ui.html` output is vendored into both consuming locations —
see `Benzene.Mesh.Ui/CLAUDE.md` for the sibling copy and why it exists there too.) This package's own
C# (`SpecUiPage`, `SpecUiMiddleware`, `SpecUiExtensions`) is real, hand-written, freely editable code —
only the embedded `.html` resource itself is off-limits.

## What this package does
Serves a Swagger-UI-style web viewer for the **Benzene message spec** — the `benzene`-format spec
produced by `Benzene.Schema.OpenApi`'s `UseSpec` (topics, request/response payloads, broadcast
events, and validation rules). It is the Benzene equivalent of `UseSwaggerUI`, but topic-centric
rather than path-centric.

This package renders the spec; it does **not** generate it. Generation lives in
`Benzene.Schema.OpenApi` (the `spec` topic / `GET /spec?type=benzene`).

## Key types
- `SpecUiPage` — transport-agnostic accessor for the viewer HTML.
  - `GetHtml()` — the page as-is (falls back to an embedded sample spec, or a `?url=` query param).
  - `GetHtml(string specUrl)` — injects a `data-spec-url` onto the document root so the page fetches
    and renders that spec on load.
- `SpecUiMiddleware<TContext> : IMiddleware<TContext> where TContext : IHttpContext` — transport-
  agnostic HTTP middleware. On a GET/HEAD to its path it writes the page as `text/html` and
  short-circuits; otherwise it calls `next`.
- `SpecUiExtensions.UseSpecUi<TContext>(this IMiddlewarePipelineBuilder<TContext>, path = "/spec-ui", specUrl = "/spec?type=benzene")`
  — registers the middleware on any Benzene HTTP pipeline.

## Why it isn't ASP.NET-specific
Most Benzene services run serverless (Lambda / Azure Functions), so serving must not bind to
ASP.NET. `SpecUiMiddleware` emits the response by driving `IBenzeneResponseAdapter<TContext>`
directly — `SetStatusCode("200")` → `SetContentType("text/html")` → `SetBody(html)` →
`FinalizeAsync(context)` — and deliberately does **not** route through the message-result path
(`IMessageHandlerResultSetter.SetResultAsync`), because that path's body handler forces
`application/json` on ASP.NET. This is the same short-circuit shape as `CorsMiddleware<TContext>`
and works identically on API Gateway, Azure Functions, ASP.NET Core, and self-host.

## What the shipped bundle does (`spec-ui.html`)
Below is what the vendored `benzene-ui` React bundle actually renders, embedded as a resource
(`LogicalName` `Benzene.Spec.Ui.spec-ui.html`) — useful for understanding behavior from this side of
the repo split, but a description of the shipped output, not a spec for code that lives here. To
change any of it, change `benzene-ui` and re-vendor (see above).
- Resolves `$ref`s into `components.schemas`; renders each topic as an expandable "operation" with
  its request/response payload tables, required-field emphasis, and validation constraint chips
  (`format`, `enum`, `minLength`/`maxLength`, `minimum`/`maximum`, `pattern`, `nullable`).
- Renders the spec's document-level `transports` field (see `docs/spec.md`'s "Transport
  advertisement" section) as a chip row under the title/description (`#lede-transports`) when
  present - every transport the service is wired to receive messages over, HTTP included as just
  one chip among several rather than the implicit default.
- Renders the per-topic/per-event `example` payload the `benzene` spec carries (generated
  server-side by `Benzene.Schema.OpenApi.Examples.ExamplePayloadBuilder` during spec build),
  pretty-printed with a copy button (`navigator.clipboard` with an `execCommand` fallback).
- "Try it" panel per topic/event card — shown only when the loaded spec advertises a top-level
  `messageEndpoint` (written by `UseSpec` when `Benzene.Http.BenzeneMessage`'s `UseBenzeneMessage`
  is registered; see `docs/payload-testing.md`). Payload textarea pre-filled from the spec
  `example`, `Key: Value`-per-line headers textarea, Send (Dispatch on event cards) POSTs the
  `{topic, headers, body}` envelope (payload JSON is validated client-side first; `body` is the
  payload as a string), and the response envelope renders inline (HTTP + Benzene status chips,
  headers, pretty-printed body, duration). The endpoint path resolves against the spec's URL
  origin when the spec was fetched, else against the page origin. Capability gating is
  server-side: no `messageEndpoint`, no panel — the page degrades to the read-only viewer.
- Loads a spec from, in precedence order: `?url=` query param → `data-spec-url` on the document root
  → embedded sample. Theme-aware (light/dark), with a search filter and a "Load spec" dialog.
- **Reserved utility topics** (`reserved: true` in the `benzene` spec — see `Benzene.Schema.OpenApi`'s
  `ReservedTopics`) are split out of the main "Message topics" list into a collapsed, labelled
  "Benzene utilities" panel (with a Utilities stat), so the service's domain contract stays the
  focus while its Cloud Service Profile endpoints stay one click away.

## When to use this package
- To give a running Benzene service a browsable spec page, alongside its `spec` endpoint.
- `UseSpecUi` is the turnkey path on any HTTP transport (Lambda, Functions, ASP.NET, self-host).
  Any transport can also serve `SpecUiPage.GetHtml(...)` directly.

## Dependencies
- `Benzene.Http` (project reference) — for the transport-agnostic HTTP abstractions
  (`IHttpContext`, `IHttpRequestAdapter`, `IBenzeneResponseAdapter`, the middleware pipeline). No
  ASP.NET / web-framework dependency. `SpecUiPage` alone has no Benzene dependencies at all.

## Conventions
- Point the UI at the `benzene` spec type (`/spec?type=benzene`) — it is designed around the
  topic/payload/validation shape of that format, not `openapi`/`asyncapi`.
- `spec-ui.html` itself is not a convention to keep in mind while editing — it isn't edited here at
  all. See the vendoring section at the top of this file.

## Tests
- `test/Benzene.Core.Test/SpecUi/SpecUiPageTest.cs` — `GetHtml()`/`GetHtml(specUrl)`: embedded
  resource loads, `data-spec-url` injection and HTML-encoding, null/whitespace fallback.
- `test/Benzene.Core.Test/SpecUi/SpecUiMiddlewareTest.cs` — GET/HEAD-to-matching-path
  short-circuits (writes the page via `IBenzeneResponseAdapter`, never calls `next`); any other
  method or path falls through to `next`; path normalization (case, leading/trailing slash).
  Uses a trivial `FakeHttpContext : IHttpContext` (the interface is a pure marker) with Moq'd
  `IHttpRequestAdapter`/`IBenzeneResponseAdapter` — no real transport needed.
- `mesh-ui-drift-check.yml` is this package's real test for `spec-ui.html` itself: it doesn't check
  behavior, it checks that the embedded bundle hasn't drifted from what `benzene-ui` actually built.
