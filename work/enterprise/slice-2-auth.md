# Slice 2 — Auth in the host

**Status:** **SHIPPED** (verified against source 2026-08-20) — `deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs` and `src/Benzene.Mesh.Auth.Oidc/`,
covered by `MeshAuthGateTest` and `MeshAuthAcceptanceTest` (which is the `/artifacts/*`-is-protected test
this slice asked for).

> **CORRECTION (2026-08-22):** the 2026-08-20 "SHIPPED"/"verified against source" line above and the
> task 2.5 checkbox below were wrong about the dispatch gate. Re-verifying against source found
> `Startup.cs` calling `UseMeshDispatch()` alone — no `[HttpEndpoint]` route, no `UseBenzeneMessage`
> envelope, and no route reachable through `.UseMessageHandlers()` either (the handler carries no
> `[HttpEndpoint]` attribute) — so `mesh:dispatch` had **no HTTP path that reached it in this host at
> all**, and `AuthorizationExtensions.RequireRole` was never called anywhere in `Startup.cs`.
> `MeshAuthConfig.DispatchRole` was bound and validated by the config loader (hence exercised by
> `MeshHostConfigTest`) but enforced nowhere - a principal with no role, and in fact any principal at
> all, could not have been rejected by a check that was never wired, because the request could not
> reach the check either way. `UseMeshDispatchGuard()` was similarly never called in this host - only
> `UseMeshDispatch()` was - so the guard's CSRF/identity/rate-limit checks in the middleware from
> `Benzene.Mesh.Artifacts` this doc points to below were dormant here too.
>
> Fixed in the same session that found it. `Startup.Configure` now gives `mesh:dispatch` its own
> `UseBenzeneMessage` envelope (mirroring `examples/AwsMesh/Mesh/Startup.cs`'s own `DispatchPath`
> pattern) with `UseMeshDispatchGuard()` mounted directly ahead of it. `DispatchRole` is enforced in
> `MeshAuthGate` itself, not via `RequireRole` on that envelope - see `MeshAuthGate`'s remarks for why
> (that envelope's inner pipeline runs in a separately-built DI scope this host's `app.UseBenzene(IApplicationBuilder, ...)`
> wiring creates, which never sees anything this gate sets via `HttpContext.RequestServices`; the gate
> also now sets `context.User` for every mode, and `MeshDispatchIdentity` is registered to read it back
> through `IHttpContextAccessor`, so the guard's identity check - previously unreachable, now reachable
> - is not itself always-refuse). Regression coverage:
> `deploy/Mesh/Benzene.Mesh.Host.Test/MeshDispatchRoleAcceptanceTest.cs` boots the real `Startup` on a
> real Kestrel pipeline and proves, against actual HTTP responses: (a) a principal missing the
> configured `dispatchRole` gets 403 before the handler runs; (b) a principal holding it passes the
> check and reaches `MeshDispatchMessageHandler`; (c) the dispatch guard is actually wired into this
> host's pipeline (a request missing its CSRF header is refused by the guard specifically, not by the
> role check). All three were previously unverified by any test - `MeshAuthGateTest.cs` has no
> dispatch-role coverage at all (confirmed by re-checking it while writing this correction), and even
> a unit test against `MeshAuthGate` alone could not have caught this: the gap was in `Startup.cs`'s
> pipeline wiring, one level up from the gate.

**Depends on:** slice 1 (this adds an `auth` section to the config schema slice 1 establishes).
**Branch:** `claude/mesh-enterprise-slice-2`
**Spans two repos.** Tasks 2.1–2.7 are `benzene-dotnet`. Task 2.8 is `benzene-ui` and is optional
polish — the slice ships without it.

## Why

The mesh dashboard is a map of the entire estate: every topic, every schema, every health status,
and — when dispatch is on — a button that invokes real handlers. Today **there is no authentication
anywhere in the mesh**, by an explicit design position: identity was deemed to belong to "the
gateway in front". That is a defensible answer for a library. It is not a sufficient answer for a
product a customer deploys, and it is the single hardest blocker on enterprise adoption.

## Read this before you touch anything

### The trap that will bite you

**This section describes the code as slice 1 left it — read the real `Startup.cs` before you start,
it may have moved again.** Slice 1 (config schema v1) split artifact serving into two paths, keyed
on `IsFileArtifactStore`:

```csharp
public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    app.UseRouting();

    string manifestUrl;
    if (IsFileArtifactStore)
    {
        // ASP.NET Core static files — NOT the Benzene pipeline. The slice 1 author left a comment
        // here flagging exactly this: "ONLY safe while there is no auth in front of it (slice 2
        // must protect this surface too, not just the Benzene pipeline below)." That's you.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.GetFullPath(_config.ArtifactRootDirectory)),
            RequestPath = "/artifacts",
        });
        manifestUrl = "/artifacts/manifest.json";
    }
    else
    {
        // Root-relative — Benzene.Mesh.Artifacts.UseMeshArtifacts() (below) serves these INSIDE the
        // Benzene pipeline for s3/azureBlob/gcs, unlike the file case above.
        manifestUrl = "manifest.json";
    }

    app.UseBenzene(benzene => benzene
        .UseHttp(asp =>
        {
            if (!IsFileArtifactStore) { asp.UseMeshArtifacts(); }
            asp.UseMeshUi(path: "/mesh-ui", manifestUrl: manifestUrl, envelopeUrl: _fleetEnabled ? "/benzene/invoke" : null);
            asp.UseMeshSpecUi(path: "/mesh-spec-ui.html", manifestUrl: manifestUrl);
            if (_fleetEnabled) { asp.UseBenzeneMessage(..., fleet => fleet.UseMessageHandlers(MeshCollectorHandlers.Queries)); }
            if (_config.Dispatch.Enabled) { asp.UseMeshDispatch(new MeshDispatchOptions { AllowInProduction = _config.Dispatch.AllowInProduction }); }
            asp.UseMessageHandlers();
        })
    );
}
```

**So there are two cases, not one:**

- **`artifactStore.type: "file"` (the default):** `/artifacts/*` is served by ASP.NET's
  `UseStaticFiles`, entirely outside the Benzene pipeline. `/artifacts/manifest.json` — the whole
  estate in one file — is world-readable the moment `app.UseStaticFiles` runs, regardless of what
  you put in the Benzene pipeline below it.
- **`artifactStore.type` of `s3`/`azureBlob`/`gcs`:** artifacts are served by
  `Benzene.Mesh.Artifacts.UseMeshArtifacts()` **inside** `UseHttp`, so they already sit behind
  whatever you put earliest in that pipeline.

**If you protect only the inner Benzene pipeline and the deployment uses the file store — the
default — you will ship a login page in front of a UI whose data is still world-readable at
`/artifacts/manifest.json`.** It will look like it works. It will demo fine on the non-default config
you tested with. It is not secure on the default one.

Every auth task below must protect **both** cases with one gate, placed early enough to cover the
`file`-store branch too (task 2.2 says exactly where). The acceptance test in 2.7 exists specifically
to prove both cases, not just the one that's already inside the pipeline.

### What already exists — do not rebuild it

`Benzene.Auth.Core`, `Benzene.Auth.Basic` and `Benzene.Auth.OAuth2` are fully implemented, tested,
and already in `Benzene.sln`. Read `src/Benzene.Auth.Core/` and `test/Benzene.Core.Test/Auth/`
before writing anything.

| You need | It already exists as |
|---|---|
| Where identity lives | `AuthenticationHolder` — a **scoped DI holder**, not a property on `TContext`. This is the repo's "context purity" rule; do not add identity to a context type. |
| Short-circuiting | `AuthResults.UnauthorizedAsync(...)` / `AuthResults.ForbiddenAsync(...)` |
| Role gating | `AuthorizationExtensions.RequireRole<TContext>(params string[] anyOfRoles)` — works on any transport |
| Basic auth | `UseBasicAuth<TContext>(IBasicAuthCredentialValidator validator, string realm = "Benzene")` |
| Bearer JWT validation | `UseOAuth2Bearer<TContext>(OAuth2BearerOptions options)`, plus `RequireScope` |
| Bearer options, already config-bindable | `OAuth2BearerOptions` — `Authority`, `JwksUri`, `ValidIssuers[]`, `ValidAudiences[]`, `ValidAlgorithms[]`, `ClockSkew`, `RequireHttpsMetadata`; all get/set, with an internal `Validate()` that throws at wire-up rather than on first request |

The canonical `Use*` wiring pattern, copied verbatim from `Benzene.Auth.Basic/Extensions.cs` — match
this shape for any new middleware you add:

```csharp
app.Register(x =>
{
    x.TryAddScoped<AuthenticationHolder>();
    x.AddScoped(resolver => new BasicAuthMiddleware<TContext>(
        validator, realm,
        resolver.GetService<IHttpRequestAdapter<TContext>>(),
        resolver.GetService<IBenzeneResponseAdapter<TContext>>(),
        resolver.GetService<AuthenticationHolder>(),
        resolver
    ));
});

return app.Use<TContext, BasicAuthMiddleware<TContext>>();
```

`RequireScope` in `Benzene.Auth.OAuth2/Extensions.cs` is the reference for a middleware that needs
no dedicated class — it uses `FuncWrapperMiddleware<TContext>`.

### What does not exist and must be built

**Interactive browser login.** `UseOAuth2Bearer` validates a bearer JWT on a message pipeline — that
is API auth. A person opening `/mesh-ui` in a browser has no bearer token; they need an
authorization-code redirect and a cookie session. That is ASP.NET Core plumbing
(`AddAuthentication().AddCookie().AddOpenIdConnect()`), and it belongs **in this host project**, not
in a new `Benzene.Auth.*` package. Do not create one.

## Before you start

```bash
dotnet build Benzene.sln
dotnet build deploy/Mesh/Benzene.Mesh.Host.sln
dotnet test  deploy/Mesh/Benzene.Mesh.Host.sln     # slice 0 created this test project
```

All three must be green before you begin.

## Tasks

### 2.1 — The `auth` config section

**Files:** `deploy/Mesh/Benzene.Mesh.Host/MeshHostConfig.cs` (modify).

**Do:** add an `Auth` property of a new `MeshAuthConfig` class. Mutable get/set properties
throughout — the configuration binder requires it, and `MeshHostConfig` already documents this as
the reason it deviates from the immutable style used in `Benzene.Mesh.Contracts`.

```jsonc
"auth": {
  "mode": "none",                  // none | proxy | basic | oidc — see tasks below
  "allowedEmailDomains": [],       // empty = any authenticated user
  "requiredGroups": [],            // empty = any authenticated user
  "dispatchRole": null,            // when set, mesh:dispatch additionally requires this role/group
  "proxy": { "userHeader": "X-Forwarded-User", "trustedProxies": [] },
  "oidc": {
    "authority": null, "clientId": null,
    "clientSecretEnvVar": "MESH_OIDC_CLIENT_SECRET",   // the NAME of an env var, never the secret
    "callbackPath": "/signin-oidc", "scopes": [ "openid", "profile", "email" ]
  }
}
```

**`clientSecretEnvVar` holds the *name* of an environment variable, never a secret.** If you find
yourself adding a `clientSecret` property, stop — that violates the house rule in
[`README.md`](README.md), and this config file gets committed to customers' repositories.

**Done when:** `mode: "none"` binds and the host behaves exactly as it does today.

### 2.2 — Mode `proxy`, and the shared gate

**Files:** `deploy/Mesh/Benzene.Mesh.Host/Startup.cs` (modify), plus a new
`MeshAuthGate.cs` in the same folder.

**Do this one first of the three modes.** It is the one enterprises are most likely to actually use
— many will insist their own oauth2-proxy / ALB+Cognito / Azure App Proxy front door performs login
regardless of what we build — and it is the smallest.

Behaviour: read the configured `userHeader` from the request. If absent, 401. If present, build a
`ClaimsPrincipal` from it and continue. **Only trust the header when the immediate peer is in
`trustedProxies`** — an un-gated forwarded-identity header is a total authentication bypass, because
anyone who can reach the host can simply set it. If `trustedProxies` is empty, refuse to start with
a message saying so; do not silently trust everything.

Implement the gate as ASP.NET Core middleware registered **immediately after `app.UseRouting()` and
before `app.UseStaticFiles(...)`**, so it covers `/artifacts` and the Benzene pipeline in one place.
This is the fix for the trap described above.

**Verify:** tests in `deploy/Mesh/Benzene.Mesh.Host.Test/` (created in slice 0) — header absent → 401;
header present from an untrusted peer → 401; header present from a trusted peer → 200; empty
`trustedProxies` → startup throws.

### 2.3 — Mode `basic`

**Files:** `Startup.cs` (modify).

**Do:** wire `UseBasicAuth` with an `IBasicAuthCredentialValidator` whose credentials come from
environment variables (`MESH_BASIC_USER` / `MESH_BASIC_PASSWORD`), not from config. Reuse the
existing package; write no new middleware.

Note this only covers the Benzene pipeline, so the shared gate from 2.2 must handle `/artifacts` for
this mode too. Prefer routing all three modes through one gate rather than three different
mechanisms — one place to reason about, one place to get wrong.

**Verify:** wrong password → 401 with a `WWW-Authenticate` header; correct → 200; `/artifacts` is
equally protected.

### 2.4 — Mode `oidc`

**Files:** `Startup.cs` (modify), `Benzene.Mesh.Host.csproj` (add
`Microsoft.AspNetCore.Authentication.OpenIdConnect`).

**This adds a NuGet dependency, which `AGENTS.md` says requires asking first.** It is the one this
brief authorizes; do not add any other. If you find yourself wanting a second, stop and report.

**Do:** standard ASP.NET Core cookie + OIDC authorization-code wiring, driven by the `oidc` config
block, with the client secret read from the environment variable named by `clientSecretEnvVar`.

One configurable implementation covers Google, Okta, Entra ID, Auth0 and Keycloak — **social login
and the customer's own SSO are the same feature** when the authority is configuration. Do not write
per-provider code paths.

GitHub is OAuth2 rather than full OIDC and needs a small accommodation (no discovery document, a
userinfo call to get the email). Add it only if it falls out cleanly; if it wants its own code path,
leave it out and report that — it is worth having but not worth distorting the shape for.

**Explicitly out of scope: SAML.** Every enterprise IdP that matters bridges SAML to OIDC, and mode
`proxy` covers the rest. Do not add it, and do not add a Facebook provider.

**Verify:** an unauthenticated browser request to `/mesh-ui` returns a redirect to the authority; a
request carrying a valid cookie returns 200. For the token-validation path, follow the fake-JWKS
pattern already used in the auth tests — a loopback `HttpListener`, as in `Benzene.CloudService.Probe`'s
tests.

### 2.5 — Authorization: domains, groups, and the dispatch gate

**Files:** `Startup.cs` (modify), `MeshAuthGate.cs` (modify).

**Do:** after authentication, apply `allowedEmailDomains` / `requiredGroups` if either is non-empty.
Authenticated-but-not-permitted is **403, not 401** — the distinction is already modelled by
`AuthResults.ForbiddenAsync` vs `UnauthorizedAsync`, and getting it wrong sends a legitimate user
into a redirect loop.

Then the one read/write distinction worth having in v1: when `auth.mode != "none"` **and**
`Dispatch.Enabled` is true **and** `dispatchRole` is set, `mesh:dispatch` additionally requires that
role. "Who may fire the button that invokes real handlers" is the first question a security reviewer
asks.

**Corrected 2026-08-22 (see the CORRECTION note at the top of this file): do NOT use
`AuthorizationExtensions.RequireRole<TContext>` on the `mesh:dispatch` `UseBenzeneMessage` envelope's
own inner pipeline, despite that being the obvious reading of the original advice above.** That
envelope's inner pipeline runs in a separately-built DI scope (this host wires Benzene via
`app.UseBenzene(IApplicationBuilder, ...)`, the "embedding" overload - see its own remarks on the
clone provider this creates), which never sees anything `MeshAuthGate` sets on
`HttpContext.RequestServices` - `RequireRole` there sees no principal at all and refuses every
dispatch, role or no role, not just a wrongly-roled one. Enforce `dispatchRole` in `MeshAuthGate`
itself instead, directly against `HttpContext`, keyed on the dispatch path (`MeshAuthGate.DispatchPath`)
- see its remarks for the full explanation and `HasAnyRole` for the role-matching logic (shared with
the `requiredGroups` check above it). Making the dispatch guard's identity check work end-to-end (so a
correctly-role-gated dispatch doesn't then get refused by the guard for "no identity") needed the same
kind of cross-scope fix: `MeshAuthGate` now sets `context.User` for every mode, and
`Startup.ConfigureServices` registers `MeshDispatchIdentity` to read it back through
`IHttpContextAccessor` - a process-wide `AsyncLocal`, the one thing that DOES cross that DI boundary.

**Note on the config shape:** slice 1 nested the dispatch flags — `_config.EnableDispatch` /
`_config.DispatchAllowInProduction` from the original sketch shipped as `_config.Dispatch.Enabled` /
`_config.Dispatch.AllowInProduction` (a `MeshDispatchConfig` section, see
`deploy/Mesh/Benzene.Mesh.Host/MeshHostConfigSections.cs`). Every reference to the flat names
elsewhere in this brief means the nested ones.

**Scope limit: no per-service RBAC.** Authenticated → full read access. If you find yourself
building a permission model, you have exceeded this slice.

**Verify:** a principal outside `allowedEmailDomains` → 403; a principal without `dispatchRole` →
403 on dispatch but 200 on read.

### 2.6 — Decide what happens to the ingestion endpoint

**Files:** `Startup.cs`, `deploy/Mesh/README.md`.

`MeshReportMessageHandler` is reachable at `/mesh/report` because Benzene's reflection-based
`.UseMessageHandlers()` discovers every attributed handler in every referenced assembly — the host's
`CLAUDE.md` documents this as a deliberate v1 decision. It is a **write** endpoint: services
self-report into the mesh through it.

Cookie-based browser login cannot cover it, because the caller is a service, not a browser. The
`Benzene.Auth.*` design doc explicitly deferred service-to-service mesh auth (its Q5), so there is no
shipped answer to reuse.

**The decision, made here so you do not have to make it:** add `auth.ingestion` with two modes.
`open` is the default and preserves today's behaviour. `sharedSecret` requires a header whose value
matches an environment variable (`MESH_INGEST_SECRET`); a mismatch is 401. Compare in
**constant time** — a naive `==` on a secret is a timing oracle.

If this grows beyond roughly thirty lines, stop and report rather than expanding it — the proper
answer is the deferred API-key package, not an ad-hoc scheme grown inside this slice.

**Document the residual gap either way:** with `ingestion: "open"` and auth enabled, the read surface
is protected and the write surface is not. That must be stated plainly in the README, not left for an
operator to discover.

### 2.7 — The acceptance test

**Files:** `deploy/Mesh/Benzene.Mesh.Host.Test/` (add), `.github/workflows/smoke-mesh-compose.yml`
(modify).

**Do:** for each of `proxy`, `basic` and `oidc`, assert that an unauthenticated request to **each of
these paths** is refused:

- `/mesh-ui`
- `/mesh-spec-ui.html`
- `/artifacts/manifest.json` ← **the one that catches the trap**
- `/artifacts/services/<any>.json`
- the benzene-message envelope path used for `benzene:mesh:query:*`

And that `auth.mode: "none"` leaves every one of them open, so the default local-dev experience is
provably unchanged.

Run that same set of assertions against **both** artifact-store branches — `artifactStore.type:
"file"` (paths under `/artifacts/`) and a non-file type (paths root-relative, e.g. `manifest.json`
served through `Benzene.Mesh.Artifacts.UseMeshArtifacts()`). The file case is the one with the real
trap (outside the pipeline); the non-file case is already inside it, but "already inside the
pipeline" is exactly the kind of assumption this test exists to stop taking on faith.

Extend the compose smoke test to run one pass with `mode: "proxy"` and assert
`/artifacts/manifest.json` returns 401 without the header and 200 with it. The existing smoke test
already curls that exact path, so this is a small addition to a proven harness. The compose sample
uses the file store, so this covers that branch only — the non-file branch is unit-test-only, per
the point above.

**Done when:** removing the static-files gate makes a test fail. If it does not, the test is not
testing the trap.

### 2.8 — UI polish (repo `benzene-ui`) — optional

**Do not start this until 2.1–2.7 are merged.** The slice ships without it.

Login means almost nothing to the single-page UI, by design: auth sits in front of the whole host,
the browser logs in before the page loads, and every fetch is already same-origin, so cookies simply
flow. Two progressive enhancements only:

1. Treat a 401 on a background fetch as "session expired" → full-page reload, so an expired cookie
   re-enters the login flow instead of silently showing stale data.
2. If a `whoami` endpoint is present, show an identity/logout chip. Feature-detect it exactly the
   way `selectFleetAvailable` feature-detects the collector — absent must render as nothing, never
   as an error.

`Benzene.Mesh.Ui` stays auth-free and statically hostable. Do not add auth to the component library.

Remember the vendoring chain: change `benzene-ui/src/`, `npm run build`, then re-vendor to all five
copies. Never hand-edit `mesh-ui.html`.

## Definition of done

- [x] `dotnet build Benzene.sln` and both host build/test commands green.
- [x] `auth.mode` of `none`, `proxy`, `basic`, `oidc` all work; `none` is the default and unchanged.
- [x] **`/artifacts/*` is protected in every non-`none` mode**, proven by a test that fails if the
      gate is removed.
- [x] Authenticated-but-not-permitted returns 403; unauthenticated returns 401.
- [x] Dispatch requires `dispatchRole` when configured. **Corrected 2026-08-22: this box was checked
      2026-08-20 while the checked-in code did not do this at all - see the CORRECTION note at the top
      of this file. Now actually true, and now actually tested
      (`MeshDispatchRoleAcceptanceTest.cs`) against a real request through the real host, not just
      against config binding.**
- [x] Exactly one new NuGet dependency (`Microsoft.AspNetCore.Authentication.OpenIdConnect`).
- [x] No secret appears in any config file or sample; secrets are read from environment variables.
- [x] `deploy/Mesh/README.md` documents each mode and the ingestion gap.

## Do NOT

- Do not create a new `Benzene.Auth.*` package. Interactive login is host plumbing.
- Do not put identity on a `TContext`. Use `AuthenticationHolder` — this is a standing repo rule.
- Do not implement SAML or Facebook login. Both are deliberate declines, argued in the research
  document; reversing them is a product decision, not an implementation one.
- Do not build per-service RBAC.
- Do not add auth to `src/Benzene.Mesh.Ui`.
- Do not edit `test/conformance-fixtures/**`, and do not add anything about auth to the
  language-neutral spec.

## Report back with

Which auth modes you verified and how; the exact test that proves `/artifacts` is protected; whether
GitHub login made it in or was left out and why; and the wording you used in the README for the
ingestion gap.
