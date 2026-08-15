# Benzene.Mesh.Auth.Oidc

## What this package does
OIDC login (OAuth2 Authorization Code flow - never implicit) gated by an explicit email allowlist, as
opt-in Benzene middleware (`UseMeshOidcAuth`). Built for `examples/AwsMesh`'s mesh Lambda, whose entire
HTTP surface (Mesh UI, catalog artifacts, `/mesh/refresh`, eventually dispatch) was reachable by anyone
with the URL - this puts a real login in front of all of it. Provider-agnostic by construction: nothing
Google-specific in this package's public API (types, method names, config keys) - `MeshOidcOptions.Issuer`
is any OIDC provider's issuer URL (Google, Microsoft Entra ID, Okta, Auth0, ...), and every provider
endpoint is resolved via OIDC discovery, never hardcoded. "Google" only shows up in `examples/AwsMesh`'s
own wiring, where `Issuer` is set to `https://accounts.google.com`.

## Why OIDC discovery, not hardcoded endpoints
`MeshOidcOptions.Issuer` is the only endpoint a caller configures. `OidcConfigurationManagerFactory`
fetches `{Issuer}/.well-known/openid-configuration` **once**, via
`Microsoft.IdentityModel.Protocols.OpenIdConnect`'s `ConfigurationManager<OpenIdConnectConfiguration>` -
the exact same building block `Benzene.Auth.OAuth2`'s `Authority` path already uses (see its `CLAUDE.md`).
That single fetch supplies the authorization endpoint, token endpoint, and JWKS (with automatic
caching/refresh, including refresh-on-unrecognized-`kid` for key rotation) - so this package never
hardcodes a provider's URLs and needs no per-provider branch to add a second identity provider later.
The one documented provider quirk (`Extensions.ValidIssuersFor`): Google's discovery document advertises
`iss` as `https://accounts.google.com`, but some Google-issued ID tokens historically carry the bare
`accounts.google.com` (no scheme) - both are accepted, but ONLY when the configured issuer is exactly
Google's; every other provider gets a single exact-match `ValidIssuers` entry.

## Why the flow is Authorization Code, not implicit
The ID token never appears in a browser-visible URL fragment or is handled by client-side JS at all -
`/login` redirects to the provider, the provider redirects back to `/callback` with only a short-lived
`code`, and `/callback` exchanges that `code` for tokens via a server-to-server POST (`OidcTokenExchangeClient`)
using the client secret. The secret and the ID token both stay server-side for their entire lifetime.

## Key types
- `MeshOidcOptions` - `Issuer`/`ClientId`/`ClientSecret`/`SigningKey` (all required, `Validate()` throws
  `ArgumentException` at wire-up time if any is missing or `SigningKey` is under 32 bytes - never a
  silently-weak default), `BasePath` (default `/mesh/auth`), `AllowedEmails` (empty = deny everyone, not
  an error), `Scope` (default `openid email`), `SessionDuration` (default 24h), `ValidAlgorithms`
  (default `["RS256"]`, algorithm-confusion protection per RFC 8725 §3.1 - same reasoning as
  `Benzene.Auth.OAuth2.OAuth2BearerOptions.ValidAlgorithms`), `RequireHttpsMetadata` (default `true`,
  test-only escape hatch), `PublicBaseUrl` (optional override for deriving the absolute `redirect_uri`).
- `Extensions.UseMeshOidcAuth<TContext>(options)` - validates options, builds the shared long-lived
  `ConfigurationManager<OpenIdConnectConfiguration>` / `JsonWebTokenHandler` / `TokenValidationParameters`
  / `HttpClient` once at wire-up time, and registers four middlewares in order:
  1. `OidcLoginMiddleware<TContext>` - `GET {BasePath}/login`. Redirects (302) to the discovered
     authorization endpoint with `client_id`/`redirect_uri`/`response_type=code`/`scope`/`state`, and
     sets the `state` value as a short-lived (10 min), `HttpOnly`/`Secure`/`SameSite=Lax` cookie scoped
     to `BasePath`. `?returnTo=` is validated (`ReturnToValidator.IsSafe`) and embedded in the signed
     state token so a successful login lands the user back where they started.
  2. `OidcCallbackMiddleware<TContext>` - `GET {BasePath}/callback`. Validates `state` (below), exchanges
     `code` for an ID token (`OidcTokenExchangeClient`), verifies it (`OidcIdTokenValidator`), checks the
     verified email against `AllowedEmails`, and only then issues a session cookie and redirects to the
     validated `returnTo`. Any failure at any step: no session issued, a generic "access denied" HTML
     response (401), the real reason logged server-side only via `ILogger` - never echoed to the browser.
  3. `OidcLogoutMiddleware<TContext>` - `GET {BasePath}/logout`. Clears the session cookie, redirects to
     `/`.
  4. `OidcSessionGateMiddleware<TContext>` - registered last, so it never sees a request to the three
     routes above (they've already short-circuited). Requires a valid session cookie whose email is
     **currently** allowlisted (re-checked against live `MeshOidcOptions.AllowedEmails` every request,
     never just trusted from the cookie - removing an email takes effect on the very next request even
     for an existing session). Missing/invalid/expired session: `Accept: text/html` → redirect to
     `{BasePath}/login?returnTo=<original path+query>`; anything else (fetch/XHR/POST) → `401` with a
     minimal JSON body, never a redirect. Not `ITerminalMiddleware` - it calls `next()` on success like
     any decorator, only short-circuiting on failure.
- `IOidcQueryStringReader<TContext>` - the one abstraction this package had to invent: Benzene's
  transport-agnostic `HttpRequest.Path` deliberately excludes the query string (see its own remarks), and
  no existing Benzene abstraction exposes it generically. A transport binding supplies its own
  implementation and registers it in DI before calling `UseMeshOidcAuth` - see `examples/AwsMesh`'s
  `ApiGatewayOidcQueryStringReader` for the AWS API Gateway (v1 payload) binding. This is also what keeps
  this package free of any transport SDK dependency.
- `SignedToken` - the shared "HMAC-SHA256-signed, base64url JSON payload" codec both the state token and
  session cookie are built on: `base64url(json) + "." + base64url(HMAC-SHA256(key, base64url(json)))`.
  Deliberately not a JWT - no algorithm negotiation, so no algorithm-confusion surface to defend at all.
  Signature comparison is constant-time (`CryptographicOperations.FixedTimeEquals`). Both payload types
  share one signing key and an `Exp` field, so a validly-signed token of one type deserializes without
  error as the other shape too (with its type-specific fields null) - `OidcStateToken`/`OidcSessionToken`
  both explicitly reject a null/empty required field rather than relying on a downstream caller to catch
  it, closing that cross-token-confusion gap.
- `OidcStateToken` / `OidcSessionToken` - typed wrappers over `SignedToken` for the two payloads
  (`{Nonce, ReturnTo, Exp}` and `{Email, Exp}`). `OidcStateToken.TryValidate` does the CSRF check: the
  callback's `state` query parameter must be **byte-identical** (constant-time compared) to the state
  cookie - the double-submit pattern. An attacker who can only steer a victim to a crafted callback URL
  can never have set the victim's own `HttpOnly` cookie, so a mismatch means a forged or replayed
  callback. The token's own signature+expiry are then verified too, and the state cookie is cleared on
  every DENIED callback response so a captured/replayed callback URL can't be tried again. It is
  deliberately NOT also cleared on the success path (see "One `Set-Cookie` per response" below) -
  harmless, since the authorization `code` itself is single-use (enforced by the provider), which is
  what actually blocks a replayed callback URL from succeeding twice, regardless of the state cookie's
  remaining lifetime.
- `EmailAllowlist.IsAllowed` - case-insensitive, exact match only. No substring/domain matching in this
  first pass (see "Follow-ups").
- `ReturnToValidator.IsSafe` - the open-redirect guard: must start with a single `/` (path-absolute), not
  `//` or `/\` (protocol-relative tricks), no embedded `://` anywhere, no control characters (tab/CR/LF -
  a classic whitespace-before-scheme browser bypass).
- `OidcIdTokenValidator` - signature/`iss`/`aud`/`exp` via `JsonWebTokenHandler.ValidateTokenAsync` (same
  call shape as `Benzene.Auth.OAuth2.OAuth2BearerMiddleware`), THEN the OIDC-specific check plain JWT
  validation has no reason to make: `email_verified` must be the string `"true"` (how
  `JsonWebTokenHandler` surfaces Google's JSON boolean claim) before `email` is trusted at all - absent
  entirely fails closed.
- `OidcTokenExchangeClient` - the server-to-server `code`-for-tokens POST. Bare `HttpClient` (`TryAddSingleton(_
  => new HttpClient())`), matching `Benzene.Mesh.Dispatch.HttpMeshServiceDispatcher`'s exact convention -
  no new HTTP client library.

## Session cookie: signed, not encrypted (deliberate)
The session cookie's payload is `{email, exp}` - not secret (an authorized user's own email, which the
mesh UI would show them anyway), so the property that actually matters is tamper-evidence: a browser (or
anyone who can read the cookie jar) must not be able to forge or extend a session by editing it.
HMAC-SHA256 signing (`SignedToken`) gives exactly that, verified with a constant-time comparison. This is
a deliberate choice, not an oversight - encrypting a non-secret payload would add complexity (key
management for a second purpose, IV handling) without adding a real security property. If a future
payload needs to carry something actually secret, that decision should be revisited then, not
speculatively built in now.

## One `Set-Cookie` per response (a real transport constraint, not a style choice)
`IBenzeneResponseAdapter<TContext>.SetResponseHeader` is a single-value-per-key contract, and at least
one adapter in this repo implements it as a literal dictionary overwrite for `ApiGatewayContext` (the
AWS API Gateway v1 payload format `examples/AwsMesh` uses - see `ApiGatewayResponseAdapter.SetResponseHeader`
→ `DictionaryUtils.Set`): a second `SetResponseHeader(context, "Set-Cookie", ...)` call in the same
response silently replaces the first instead of adding a second header line. (The v2 payload format's
adapter special-cases `set-cookie` into its own array and would be fine with two - but this package
targets the lowest common denominator across `IBenzeneResponseAdapter` implementations, not the specific
adapter `examples/AwsMesh` happens to use today.) Every middleware in this package is written to never
need two `Set-Cookie` writes in one response as a result - see `OidcCallbackMiddleware`'s deny path above
for the one place this constraint actually shaped the design.

## No detail leakage on failure
Every callback failure path (bad/missing state, failed token exchange, failed ID token validation, email
not allowlisted) produces the same generic "access denied" response - the real reason goes to `ILogger`
only. This mirrors `Benzene.Auth.OAuth2`'s existing "no detail leakage" convention (see its `CLAUDE.md`):
a distinguishable failure reason in the response is an oracle an attacker can use to probe which stage
failed (e.g. "was this a valid-but-unallowlisted account, or a forged token?").

## Wiring order matters
`UseMeshOidcAuth` must be added to the pipeline BEFORE whatever it's meant to protect. Everything
registered after it on the same pipeline is gated; everything before it (and the four routes this
package itself owns) is not. See `examples/AwsMesh/Mesh/Startup.cs` for the wiring: this call sits right
after tracing/enrichment/metrics and before `UseMeshUi`/`UseMeshArtifacts`/`UseBenzeneMessage`/message
handlers on the mesh Lambda's HTTP sub-pipeline - not on the EventBridge sub-pipeline (scheduled
aggregation needs no browser session) and not on any of the six domain-service Lambdas (out of scope;
they're separate functions entirely).

## Dependencies
`Microsoft.IdentityModel.JsonWebTokens` / `Microsoft.IdentityModel.Protocols.OpenIdConnect` (same
versions `Benzene.Auth.OAuth2` already depends on - reused, not duplicated, so both packages get the
same discovery/JWKS-caching/key-rotation behavior from one well-maintained source) via `Benzene.Http`'s
transitive graph: `Benzene.Http` → `Benzene.Core.MessageHandlers`/`Benzene.Abstractions` gives
`IHttpContext`/`IHttpRequestAdapter<TContext>`/`IBenzeneResponseAdapter<TContext>`,
`IMiddleware<TContext>`/`ITerminalMiddleware`/`IMiddlewarePipelineBuilder<TContext>`, and
`Microsoft.Extensions.Logging.Abstractions`.

## Tests
`test/Benzene.Mesh.Auth.Oidc.Test` - state/CSRF validation (mismatch, missing, expired, tampered),
ID token verification against a real loopback fake OIDC provider (`FakeOidcProvider`: serves both
`.well-known/openid-configuration` and a JWKS, signs real RS256 tokens with crafted claims) covering
signature/issuer/audience/expiry/`email_verified`/algorithm-confusion, the allowlist check
(case-insensitivity, exact-match, empty-allowlist-denies-everyone), the session cookie sign/verify
round-trip and tamper detection (a flipped byte fails), the gate's redirect-vs-401 split, and the
`returnTo` open-redirect guard. `FakeOidcProvider` additionally proves discovery is genuinely read from
the document (not hardcoded) by using non-Google-shaped endpoint paths.

## No OIDC `nonce` claim echo (deliberate, given Authorization Code flow only)
This package does not send a `nonce` authorization-request parameter or verify a matching `nonce`
claim in the ID token. That check exists in the OIDC spec mainly to bind an ID token to one specific
browser session when the token can reach the browser directly (implicit/hybrid flows, or any flow
where the token rides the front channel) - here it never does: the ID token is obtained by
`OidcTokenExchangeClient`'s server-to-server POST, authenticated with `ClientSecret`, so an attacker
would need the client secret itself to inject a foreign ID token into the exchange. `state`'s
double-submit check is what defends the front channel (the callback URL). Add `nonce` if this package
ever grows an implicit/hybrid mode - it should not for Authorization Code alone.

## Follow-ups (not in this package yet)
- Email allowlist is exact-match only - no domain-wildcard support (`*@company.com`). Deliberately out of
  scope for this first pass; a wrong domain rule is an easy way to accidentally widen access.
- `AuthenticationHolder` (the `ClaimsPrincipal` carrier `Benzene.Auth.Basic`/`Benzene.Auth.OAuth2` set) is
  not wired here - the session gate makes its own allow/deny decision without publishing a principal for
  downstream handlers to read. Worth adding if a handler behind this gate ever needs to know *which*
  allowlisted email is logged in, beyond the gate's own check.
- Cookie names (`benzene_mesh_oidc_state`, `benzene_mesh_session`) and the state token TTL (10 minutes)
  are constants, not configuration - fine for a single mesh deployment per origin; revisit if that stops
  being true.
- No CSRF protection on `/logout` (a GET, deliberately - "or similar" per the brief) beyond it being
  idempotent and harmless (it can only ever clear the caller's own session).
- Dispatch (`Benzene.Mesh.Dispatch`) isn't wired into `examples/AwsMesh` yet; when it is, it inherits this
  gate automatically as long as it's registered after `UseMeshOidcAuth` on the same pipeline.
