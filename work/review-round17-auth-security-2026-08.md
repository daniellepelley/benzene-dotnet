# Round 17 — Auth packages adversarial security review (2026-08)

Scope: `Benzene.Mesh.Auth.Oidc`, `Benzene.Auth.OAuth2`, `Benzene.Auth.Basic`, `Benzene.Auth.Core`,
`deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs`, `Benzene.Mesh.Artifacts/MeshDispatchGuardMiddleware.cs`,
`Benzene.Mesh.Dispatch`. Reviewed at commit `4389bfb` on `main`. First dedicated security-focused
adversarial pass on this surface since round 11 (#172-182).

**Method:** for each of six hypothesized bypass classes (OIDC CSRF/open-redirect/token validation,
OAuth2 signature verification/clock skew, proxy-trust header spoofing, role-claim divergence, basic-auth
timing side-channel, fail-open policy gating), read the actual code and construct the concrete
attack/bypass scenario before concluding whether it holds. One finding (signing-key entropy floor) was
proven with a temporary probe test, run, confirmed passing (i.e. the weak key is accepted), then
reverted — `git status` clean, no source files modified, nothing committed.

**Headline result: nothing that clears "genuine bypass" bar was found in the six areas the assignment
specifically hypothesized.** Round 11 and its follow-ups appear to have been done properly, and the
surrounding code they didn't touch is, on this pass, also sound. Two real-but-narrow gaps were found
(severity LOW/MEDIUM), not the critical-severity bypasses the assignment brief was probing for.

---

## Per-item findings (all clean except where noted)

### 1. OIDC Authorization Code flow (`src/Benzene.Mesh.Auth.Oidc/OidcLoginMiddleware.cs`, `OidcCallbackMiddleware.cs`, `OidcStateToken.cs`, `ReturnToValidator.cs`, `OidcIdTokenValidator.cs`, `Extensions.cs`) — **clean**

- `state` is CSRF-protected properly: double-submit cookie (`HttpOnly`/`Secure`/`SameSite=Lax`, 10-min
  TTL) *and* the cookie value itself is an HMAC-signed token (nonce + `returnTo` + `exp`), compared
  constant-time (`CryptographicOperations.FixedTimeEquals`). An attacker who can only lure a victim to
  a crafted `/callback?code=...&state=...` cannot forge the paired cookie.
- ID token validation goes through `TokenValidationParameters` with
  `ValidateIssuer/Audience/Lifetime/IssuerSigningKey` all explicitly `true`, `ValidIssuers`/
  `ValidAudiences` pinned to configured values, `ValidAlgorithms` an explicit allowlist (no `alg:none`),
  `ClockSkew` fixed at 2 min. Plus the OIDC-specific `email_verified` check that plain JWT validation
  wouldn't do.
- `returnTo` open-redirect: `ReturnToValidator.IsSafe` rejects non-`/`-leading, `//`, `/\`, embedded
  `://`, and control characters (tab/CR/LF header-injection-style bypass). `HomePath` (the fallback)
  gets the identical check at `Validate()` time. No bypass could be constructed.

### 2. `OAuth2BearerOptions` (`src/Benzene.Auth.OAuth2/*`) — **clean**

- Signature is genuinely verified: `JsonWebTokenHandler.ValidateTokenAsync` against
  `TokenValidationParameters.ConfigurationManager` (JWKS, auto-refreshing on unrecognized `kid`), not a
  format check.
- `ClockSkew` defaults to 2 min and is *validated* to be within `[0, 15min]` at wire-up
  (`OAuth2BearerOptions.Validate`) — neither an unbounded replay window nor an accidental
  missing-tolerance rejection of legitimate tokens.
- `ValidAlgorithms`/`ValidIssuers`/`ValidAudiences` are all required non-empty, `"*"` and `alg:none`
  explicitly rejected (RFC 8725 §3.1 defense) — same pattern as the OIDC package.

### 3. `MeshAuthGate` proxy mode (`deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs`) — **clean, and not the classic bug**

- `AuthenticateProxyAsync` checks `context.Connection.RemoteIpAddress` (the raw TCP peer) against
  `auth.proxy.trustedProxies`, normalizing IPv4-mapped-IPv6 both ways (`#176`'s fix, still correct).
  Only when the *direct socket peer* is on the allowlist is the `X-Forwarded-User`/groups header
  trusted.
- Critically, confirmed there is **no `UseForwardedHeaders()` middleware anywhere in
  `Benzene.Mesh.Host`** (`grep` came back empty) — so `RemoteIpAddress` cannot itself be poisoned by an
  attacker-supplied `X-Forwarded-For`. This is exactly the check the assignment worried might be
  missing; it's present and wired correctly.
- `Validate()` refuses `auth.mode: proxy` at startup with an empty `trustedProxies` list, naming the
  exact bypass this exists to prevent.

### 4. Role-claim handling (`MeshAuthGate.HasAnyRole`, `src/Benzene.Auth.Core/RoleClaims.cs`) — **fixed, not just documented**

- `MeshAuthGate.HasAnyRole` now delegates to `RoleClaims.IsInAnyRole` for everything except the host's
  own `groups` claim, which it also compares with `StringComparer.Ordinal`. `RoleClaims.GetGrantedRoles`
  uses `StringComparer.Ordinal` too. The two readers agree — `#182`'s divergence is closed in this
  snapshot, not merely noted as known. (A case-sensitivity mismatch between an IdP's casing and a
  configured role name would now fail *closed* on both readers identically — an operational footgun,
  not a bypass.)

### 5. Basic auth (`src/Benzene.Auth.Basic/*`, `MeshAuthGate.EnvBasicAuthCredentialValidator`) — **clean**

- The framework package (`Benzene.Auth.Basic`) ships no default validator by design (documented: avoids
  a hardcoded-credential footgun), so timing safety is the implementer's responsibility there — a
  deliberate, documented boundary, not an oversight.
- The one shipped implementation in this repo, `MeshAuthGate.EnvBasicAuthCredentialValidator`, uses
  `FixedTimeEquals` (via `CryptographicOperations.FixedTimeEquals`) for both username and password,
  combined with non-short-circuiting `&` so a username mismatch and a password mismatch take the same
  time. No credentials appear in any log/error message on failure (`"Invalid credentials"` is the only
  detail, logged nowhere with the actual values).

### 6. `RequirePolicy` (`src/Benzene.Auth.Core/AuthorizationExtensions.cs`) — **fails closed**

- `RequirePolicy(policyName)`: if no `IAuthorizationPolicy` with that name is registered, it `throw`s
  `InvalidOperationException` (cached after first resolution, per `#179`) — this denies the request
  path entirely rather than letting it through; it is not distinguishable as a silent-allow. A
  completely missing policy is a hard failure, not a fail-open.
- `PolicyMiddleware`: `holder.Principal is null` → `Unauthorized`; `!await
  policy.IsSatisfiedAsync(...)` → `Forbidden`. No path returns "allow" by default. Whether a specific
  *implemented* policy's predicate is buggy (always-allow) is inherently outside the framework's
  ability to detect — that's an app-code concern the interface correctly leaves to the app.

---

## Findings that did clear the bar (both narrow, both proven)

### Finding A — LOW/MEDIUM: signing-key entropy floor doesn't detect a repeated multi-byte block

`MeshOidcOptions.Validate()` (`src/Benzene.Mesh.Auth.Oidc/MeshOidcOptions.cs`) rejects a signing key
with fewer than 8 *distinct byte values* — this correctly catches `"kkkk...k"` (1 distinct byte) and
`"abab...ab"` (2 distinct bytes, already tested in
`MeshOidcOptionsValidateTest.LowEntropyShortAlternatingPatternSigningKey_Throws`). It does **not**
catch an 8-byte block repeated 4× to reach the 32-byte floor, e.g. `"ABCDEFGH"` × 4 = 32 bytes with
exactly 8 distinct byte values.

**Proof:** temporarily added `EightByteRepeatingPatternPaddedTo32Bytes_DoesNotThrow_RealAttackerBar` to
`test/Benzene.Mesh.Auth.Oidc.Test/MeshOidcOptionsValidateTest.cs`, ran `dotnet test --filter
FullyQualifiedName~EightByteRepeatingPatternPaddedTo32Bytes`, and it **passed** — i.e. `Validate()`
genuinely accepts this key. Reverted immediately after (`git checkout --`, confirmed clean).

**What an attacker gains:** if an operator generates the signing key by padding a short, memorable/
guessable 8-byte block to hit the 32-byte minimum (a plausible mistake given the docstring says
"clears this by a wide margin" for "a real generated secret"), the key that looks like 256 bits of
HMAC-SHA256 keyspace is really an 8-byte (64-bit) repeating structure. This key signs *both* the CSRF
state token and, more importantly, the session cookie — which is otherwise a deterministic function of
`{Email, Exp}` — so full compromise of the signing key is a complete session-forgery vector (exactly
the harm `#177`'s fix already calls out for the 1-byte case). This doesn't make it more likely an
operator does this — but it means the validator's rejection criterion is weaker than the code comments
imply, and a future reader relying only on the doc comment ("a real generated secret ... clears this by
a wide margin") would reasonably (and wrongly) conclude this class of weak key is already excluded.

**Proportionate mitigation:** strengthen the check to look at *distinct N-byte blocks* (e.g. is the key
an exact repetition of a shorter substring?) rather than only distinct single bytes — a few lines, same
spirit as the existing check, no new dependency.

### Finding B — LOW, architectural/latent, not currently exploitable: dispatch-role gate anchored to a literal path, not the dispatch topic, unlike its sibling guard

`MeshDispatchGuardMiddleware.IsGuarded` (`src/Benzene.Mesh.Artifacts/MeshDispatchGuardMiddleware.cs`)
deliberately matches on *both* canonical path *and* topic (via the route finder), with the comment "so
a route alias that reaches the handler cannot reach it around this guard." `MeshAuthGate.InvokeAsync`'s
`dispatchRole` check (`deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs`), which enforces the actual role
requirement, matches **only** `canonicalPath == CanonicalDispatchPath` — it has no topic-based
fallback.

**Current exploitability:** none. `Startup.Configure` mounts the `mesh:dispatch` envelope at exactly
one path (`MeshDispatchGuardOptions().Path`, no config knob to change it independently), and
`MeshAuthGate.DispatchPath` is derived from the same default — verified in `Startup.cs` and
`MeshDispatchGuardOptions.cs`. There is currently no second HTTP route to the `benzene:mesh:dispatch`
topic for this asymmetry to bite on.

**What an attacker would try if this changes:** if a future change ever exposes a second HTTP route to
the same dispatch topic (which the CSRF/identity/rate-limit guard already anticipates and defends
against via its topic check), a caller with valid identity but *without* the configured `dispatchRole`
could reach `MeshDispatchMessageHandler` — a real handler invocation — via that alias while the role
gate silently never fires, even though the guard's own CSRF/identity/rate-limit checks would still
apply.

**Proportionate mitigation:** give `MeshAuthGate`'s dispatch-role check the same topic-based
route-finder fallback `MeshDispatchGuardMiddleware.IsGuarded` already has, so the two checks can never
drift on what counts as "the dispatch endpoint." Low effort, closes the asymmetry before it can ever
matter; not blocking.

---

## Bottom line

- No fail-open default, no missing signature verification, no CSRF gap, no proxy-trust bypass, and no
  unified-vs-divergent role-claim gap in the six areas flagged.
- The two findings above are real but proportionate — a validation-strength gap in a secondary defense
  (byte-length + entropy floor is not the last line of defense; a compromised signing key is already
  catastrophic regardless) and a latent architectural asymmetry with no live exploit path today.
- Also worth flagging as an accepted-but-real residual risk (already documented in-code, not newly
  discovered, but worth a reviewer re-recording): `MeshOidcOptions.SessionDuration` has no upper bound
  enforced by `Validate()`, and OIDC logout is stateless/client-side only — a leaked/stolen session
  cookie remains valid until its own `exp`, unrevocable, for however long an operator sets
  `SessionDuration` (default 24h, but nothing stops years). The `Jti` field is scaffolded for a future
  deny-list but nothing reads it yet. (Already tracked as a deliberate tradeoff in
  `docs/capability-matrix.md`'s Mesh — collector row; not re-filed as a new finding.)

Files reviewed (primary): `src/Benzene.Mesh.Auth.Oidc/*`, `src/Benzene.Auth.OAuth2/*`,
`src/Benzene.Auth.Basic/*`, `src/Benzene.Auth.Core/*`, `deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs`,
`src/Benzene.Mesh.Artifacts/MeshDispatchGuardMiddleware.cs`, `src/Benzene.Mesh.Dispatch/*`, plus
corresponding test files under `test/` and `deploy/Mesh/Benzene.Mesh.Host.Test/`.
