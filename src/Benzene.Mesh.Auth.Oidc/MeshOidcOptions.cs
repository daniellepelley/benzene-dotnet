using Microsoft.IdentityModel.Tokens;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// Configuration for <see cref="Extensions.UseMeshOidcAuth{TContext}"/>. Provider-agnostic: any real
/// OpenID Connect provider that publishes standard discovery
/// (<c>{Issuer}/.well-known/openid-configuration</c>) works here - Google, Microsoft Entra ID, Okta,
/// Auth0, etc. Every field below is deliberately required (no permissive default) except where noted -
/// see each property's remarks for why.
/// </summary>
public class MeshOidcOptions
{
    /// <summary>
    /// The provider's issuer URL, e.g. <c>https://accounts.google.com</c>. Discovery is fetched once
    /// (and cached/auto-refreshed - see <see cref="Extensions.UseMeshOidcAuth{TContext}"/>) from
    /// <c>{Issuer}/.well-known/openid-configuration</c>, which supplies the authorization endpoint,
    /// token endpoint, and JWKS URI - no per-provider endpoint hardcoding in this package.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The OAuth2 client id registered with the provider. Not secret - safe to embed in the
    /// browser-facing authorization redirect.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The OAuth2 client secret. A real secret - must come from environment/DI configuration, never a
    /// hardcoded/example default (see <see cref="Validate"/>).
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The HMAC-SHA256 key used to sign the CSRF state token and the session cookie. A real secret -
    /// must come from environment/DI configuration (e.g. a Terraform-generated <c>random_password</c>),
    /// never hand-typed or committed. <see cref="Validate"/> fails fast if this is missing, too short
    /// (under 32 bytes), or too low in distinct-byte entropy (e.g. a repeated character) to be a genuine
    /// secret, rather than silently signing with a weak/guessable key - see <see cref="Validate"/>'s
    /// remarks for exactly what that floor does and does not catch.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// The base path this auth surface is mounted at, e.g. <c>/mesh/auth</c> (the default). The three
    /// routes are derived from it: <c>{BasePath}/login</c>, <c>{BasePath}/callback</c>,
    /// <c>{BasePath}/logout</c>.
    /// </summary>
    public string BasePath { get; set; } = "/mesh/auth";

    /// <summary>
    /// Where this host's landing page actually lives - used as the redirect target after logout, and as
    /// the fallback <c>returnTo</c> when a login arrives without a (valid) one. Defaults to <c>/</c>,
    /// which is correct only for a host that genuinely serves something at its root.
    /// </summary>
    /// <remarks>
    /// Set this whenever the thing being protected is NOT at the root - e.g. a mesh serving its UI at
    /// <c>/mesh-ui</c>. Leaving it at <c>/</c> there produces a confusing loop rather than an obvious
    /// error: logout clears the session and redirects to <c>/</c>, the gate bounces that to login, the
    /// provider silently re-authenticates an already-signed-in user, and the callback returns them to
    /// <c>/</c> - which resolves no route and renders a bare <c>not-found</c> problem document. The user
    /// sees a 404 and no sign that they were ever signed out (they were).
    /// </remarks>
    public string HomePath { get; set; } = "/";

    /// <summary>The allowed <c>email</c> claim values (case-insensitive, exact match - no substring or
    /// domain matching). Re-checked on every gated request, not just cached into the session cookie, so
    /// removing an email here revokes access on the very next request even for an existing session.
    /// An empty list denies every login (locks the mesh out entirely) - a legitimate, if extreme,
    /// configuration, not a startup error.</summary>
    public string[] AllowedEmails { get; set; } = System.Array.Empty<string>();

    /// <summary>The OAuth2 scope requested at the authorization endpoint. Defaults to
    /// <c>"openid email"</c> - the minimum needed to receive a verifiable <c>email</c> claim in the ID
    /// token. Widening this (e.g. adding <c>profile</c>) is harmless but unnecessary for this
    /// allowlist-only gate.</summary>
    public string Scope { get; set; } = "openid email";

    /// <summary>
    /// How long an issued session cookie remains valid. Defaults to 24 hours.
    /// </summary>
    /// <remarks>
    /// #178: deliberately unbounded (not capped by <see cref="Validate"/>) - but that is a real
    /// tradeoff, not an oversight, and it matters more here than it would elsewhere: logout in this
    /// package is client-side only (it expires the cookie; see <c>OidcLogoutMiddleware</c> and this
    /// package's <c>CLAUDE.md</c> "Stateless logout" section), so there is no server-side revocation list
    /// at all - a stolen/leaked cookie's <c>exp</c> claim is the ONLY thing that ever ends its validity;
    /// a legitimate logout only clears the ORIGINAL holder's own cookie, it does nothing to a copy
    /// already in an attacker's hands (see <see cref="OidcSessionPayload"/>'s <c>Jti</c> for what a
    /// future deny-list would need to close that gap). A very long <see cref="SessionDuration"/> (this
    /// package will happily accept a literal 10 years) directly extends how long such a leaked cookie
    /// stays valid, with nothing anywhere able to shorten that after the fact. Set this to the shortest
    /// duration your users can tolerate re-authenticating at, not a "just in case" long value.
    /// </remarks>
    public System.TimeSpan SessionDuration { get; set; } = System.TimeSpan.FromHours(24);

    /// <summary>
    /// The <c>Path</c> attribute on the session cookie - how much of the host's URL space the browser
    /// sends it back to. Defaults to <c>/</c> because the gate must see the cookie on every route it
    /// protects (Mesh UI, catalog artifacts, refresh, dispatch), which span the whole mesh host, not
    /// just <see cref="BasePath"/>. Narrow this only if the mesh is mounted under a known sub-path
    /// shared with other, unrelated routes on the same origin.
    /// </summary>
    public string CookiePath { get; set; } = "/";

    /// <summary>
    /// The signing algorithms accepted on the ID token, e.g. <c>RS256</c> (the default, and what every
    /// mainstream OIDC provider signs ID tokens with). Required and non-empty for the same
    /// algorithm-confusion reason as <c>Benzene.Auth.OAuth2.OAuth2BearerOptions.ValidAlgorithms</c>
    /// (RFC 8725 §3.1) - a validator that trusts whatever <c>alg</c> the token itself claims is
    /// attackable; this stays an explicit allowlist, never derived from the token.
    /// </summary>
    public string[] ValidAlgorithms { get; set; } = new[] { "RS256" };

    /// <summary>
    /// Whether the OIDC discovery document must be fetched over HTTPS. Defaults to <c>true</c> -
    /// fetching the document that establishes which keys/endpoints are trusted over plain HTTP is a
    /// man-in-the-middle vector, so every real provider is HTTPS and this stays required. The only
    /// legitimate reason to set it <c>false</c> is a loopback fake discovery endpoint in tests (see
    /// <c>Benzene.Auth.OAuth2.OAuth2BearerOptions.RequireHttpsMetadata</c>'s identical remarks). Never
    /// set this <c>false</c> in production.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Explicit override for this host's own public base URL (e.g.
    /// <c>https://abc123.execute-api.eu-west-1.amazonaws.com</c>), used to build the absolute
    /// <c>redirect_uri</c> sent to the provider. When null/empty (the default) it's derived per-request
    /// from the client-supplied <c>Host</c> header (and <c>X-Forwarded-Proto</c>, defaulting to
    /// <c>https</c> when absent) - correct for API Gateway and any standard reverse proxy. A forged
    /// <c>Host</c> header cannot be turned into a working redirect, since the provider independently
    /// enforces its own registered "Authorized redirect URIs" allowlist and simply refuses to redirect
    /// anywhere that doesn't exactly match - that check, not this derivation, is what actually closes
    /// off host-header-based redirect abuse. Set this explicitly for one less moving part / one less
    /// thing to reason about if the host sits behind something that doesn't forward those headers
    /// faithfully, or for stricter defense-in-depth that doesn't trust the incoming <c>Host</c> at all.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Validates this instance, throwing <see cref="System.ArgumentException"/> for any wire-up mistake
    /// that would otherwise leave the mesh unauthenticated or signing with a weak key. Called by
    /// <see cref="Extensions.UseMeshOidcAuth{TContext}"/> at pipeline wire-up time - fail fast, not on
    /// the first request.
    /// </summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer))
        {
            throw new System.ArgumentException($"{nameof(Issuer)} is required.", nameof(Issuer));
        }

        // #173 / round 1's #20: a non-https Issuer used to reach OIDC discovery unvalidated and crash
        // with an unhandled 500 the first time discovery metadata was actually fetched - mid-request,
        // not at startup. Mirrors deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.Validate's identical check
        // for the identical gap (see its remarks for the full rationale); that fix landed only in the
        // Mesh Host and was never carried into this package until now. RequireHttpsMetadata false is
        // this package's own documented test-only escape hatch (see its own remarks) - honoured here
        // too, so a loopback fake-provider test never has to flip a second knob to stay valid.
        if (RequireHttpsMetadata &&
            System.Uri.TryCreate(Issuer, System.UriKind.Absolute, out var issuerUri) &&
            string.Equals(issuerUri.Scheme, "http", System.StringComparison.OrdinalIgnoreCase))
        {
            throw new System.ArgumentException(
                $"{nameof(Issuer)} ('{Issuer}') is not https, and {nameof(RequireHttpsMetadata)} is " +
                "true (the default) - fetching OIDC discovery/JWKS metadata over plain HTTP is a man-" +
                "in-the-middle risk with nothing to detect a spoofed issuer. This is allowed ONLY for a " +
                "loopback provider you run yourself with no TLS (e.g. in tests): set " +
                $"{nameof(RequireHttpsMetadata)} to false explicitly if that's genuinely the case here - " +
                "never in production.",
                nameof(Issuer));
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new System.ArgumentException($"{nameof(ClientId)} is required.", nameof(ClientId));
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new System.ArgumentException(
                $"{nameof(ClientSecret)} is required - set it from environment/secret configuration, never a hardcoded default.",
                nameof(ClientSecret));
        }

        // 32 bytes (256 bits) is the minimum a genuine HMAC-SHA256 secret should be - short enough
        // that an accidental placeholder ("changeme", a short guessable string) is rejected, without
        // this package trying to be a general-purpose secret-strength auditor.
        var signingKeyBytes = string.IsNullOrEmpty(SigningKey) ? System.Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(SigningKey);
        if (string.IsNullOrWhiteSpace(SigningKey) || signingKeyBytes.Length < 32)
        {
            throw new System.ArgumentException(
                $"{nameof(SigningKey)} is required and must be at least 32 bytes - a real, " +
                "randomly-generated secret (e.g. Terraform's random_password), never a hardcoded or " +
                "guessable default. This signs the CSRF state token and the session cookie.",
                nameof(SigningKey));
        }

        // #177: byte-length alone lets a 32-character REPEATED character ("kkkk...k") straight
        // through - and this key signs not just the CSRF state token but a session cookie that is a
        // deterministic function of {Email, Exp} with zero randomness of its own, so a low-entropy key
        // is a complete session-forgery vector, not just weak-secret hygiene. This is deliberately NOT a
        // real entropy estimator (it would not catch a guessable dictionary phrase that happens to use
        // many distinct characters) - it is a cheap floor on the distinct byte values actually used for
        // signing (see Extensions.UseMeshOidcAuth: the raw UTF-8 bytes of this string, exactly as
        // written - never decoded from hex/base64), pragmatic enough to catch the obvious placeholder
        // shapes (a single repeated character, or too few distinct values overall). By itself it does
        // NOT catch every low-entropy shape - see the period check immediately below for the one this
        // floor alone misses (#286). A real generated secret - typed as hex, base64, or a mixed-case/
        // digit/symbol passphrase - clears BOTH checks by a wide margin.
        if (DistinctByteCount(signingKeyBytes) < MinimumDistinctSigningKeyBytes)
        {
            throw new System.ArgumentException(
                $"{nameof(SigningKey)} does not look like a real, randomly-generated secret - it has " +
                $"fewer than {MinimumDistinctSigningKeyBytes} distinct byte values across its whole " +
                "length. A repeated or near-constant string (e.g. \"kkkk...k\") is a full session-forgery " +
                "vector: this key signs a session cookie that is otherwise a deterministic function of " +
                "{Email, Exp}. Use a real generated secret (e.g. Terraform's random_password, " +
                "`openssl rand -base64 32`), never a hand-typed placeholder.",
                nameof(SigningKey));
        }

        // #286: the distinct-byte floor above counts distinct VALUES anywhere in the key, so it does
        // not catch a short block repeated to fill the key - e.g. "ABCDEFGH" x 4 = 32 bytes with
        // exactly 8 distinct byte values, clearing MinimumDistinctSigningKeyBytes, while actually being
        // only 8 bytes (64 bits) of real keyspace. Catch that shape directly: reject a key that is an
        // exact tiling of a proper substring whose period is under HALF the key's total length. A key
        // built from exactly two tiles of its own half (e.g. a 16-byte block repeated twice to fill 32
        // bytes) is deliberately still accepted - there is no shorter period to catch there, and two-
        // tile repetition of a long-enough block is indistinguishable from a real secret by this cheap
        // check (see HasLowPeriodRepeatingBlock's remarks).
        if (HasLowPeriodRepeatingBlock(signingKeyBytes))
        {
            throw new System.ArgumentException(
                $"{nameof(SigningKey)} does not look like a real, randomly-generated secret - it is an " +
                "exact repetition of a much shorter block (its period is under half its total length). " +
                "A repeating block like \"ABCDEFGH\" tiled to reach the length floor can have plenty of " +
                "distinct byte values while still being a small, guessable amount of real keyspace - and " +
                "this key signs a session cookie that is otherwise a deterministic function of " +
                "{Email, Exp}. Use a real generated secret (e.g. Terraform's random_password, " +
                "`openssl rand -base64 32`), never a hand-typed placeholder.",
                nameof(SigningKey));
        }

        if (string.IsNullOrWhiteSpace(BasePath) || !BasePath.StartsWith('/'))
        {
            throw new System.ArgumentException($"{nameof(BasePath)} must be a non-empty, absolute path.", nameof(BasePath));
        }

        // HomePath is a redirect target (post-logout, and the login fallback), so it gets the same
        // open-redirect guard as a caller-supplied returnTo - a misconfigured absolute URL here would
        // otherwise hand every logout to another origin.
        if (!ReturnToValidator.IsSafe(HomePath))
        {
            throw new System.ArgumentException(
                $"{nameof(HomePath)} must be a same-origin, path-absolute URL (e.g. \"/mesh-ui\") - never " +
                "an absolute or protocol-relative URL, which would turn logout into an open redirect.",
                nameof(HomePath));
        }

        if (ValidAlgorithms is not { Length: > 0 })
        {
            throw new System.ArgumentException(
                $"{nameof(ValidAlgorithms)} must contain at least one allowed signing algorithm - " +
                "an empty list would trust whatever \"alg\" the ID token itself claims (RFC 8725 §3.1 algorithm confusion).",
                nameof(ValidAlgorithms));
        }

        // #244: this package's own doc (and this property's remarks, above) claims parity with
        // Benzene.Auth.OAuth2.OAuth2BearerOptions.ValidAlgorithms' algorithm-confusion hardening
        // (round 11 #174) - until now that was only true of the non-empty check above. Port the same
        // three checks: null/whitespace entries, "none" rejected by name, and an allowlist match
        // against KnownSigningAlgorithms (duplicated below, not shared via project reference - see
        // that constant's remarks for why).
        foreach (var algorithm in ValidAlgorithms)
        {
            if (string.IsNullOrWhiteSpace(algorithm))
            {
                throw new System.ArgumentException(
                    $"{nameof(ValidAlgorithms)} contains a null/empty/whitespace entry - every entry must " +
                    "be a genuine signing algorithm name.",
                    nameof(ValidAlgorithms));
            }

            // Explicit, named rejection - RFC 8725 §3.1's canonical algorithm-confusion attack is
            // exactly "alg": "none" accepted by a validator that never meant to allow it. Called out
            // separately from the "unrecognized name" check below so the error is unambiguous about why.
            if (string.Equals(algorithm, SecurityAlgorithms.None, System.StringComparison.OrdinalIgnoreCase))
            {
                throw new System.ArgumentException(
                    $"{nameof(ValidAlgorithms)} must not contain \"{SecurityAlgorithms.None}\" - accepting " +
                    "the unsigned algorithm defeats signature validation entirely (RFC 8725 §3.1 algorithm confusion).",
                    nameof(ValidAlgorithms));
            }

            if (!KnownSigningAlgorithms.Contains(algorithm))
            {
                throw new System.ArgumentException(
                    $"{nameof(ValidAlgorithms)} contains '{algorithm}', which is not a recognized JWS " +
                    "signing algorithm (see Microsoft.IdentityModel.Tokens.SecurityAlgorithms) - likely a " +
                    "typo that would silently make this entry unmatchable by any real ID token.",
                    nameof(ValidAlgorithms));
            }
        }
    }

    /// <summary>
    /// #244: signing algorithms <see cref="ValidAlgorithms"/> entries are checked against -
    /// deliberately a byte-for-byte duplicate of
    /// <c>Benzene.Auth.OAuth2.OAuth2BearerOptions.KnownSigningAlgorithms</c>, not a shared reference.
    /// That constant is <c>private</c> (making it reachable from here would mean either widening its
    /// access purely to serve this one cross-package read, or adding a project reference from this
    /// provider-agnostic, deliberately minimal-dependency package - see this package's own
    /// <c>CLAUDE.md</c> "Dependencies" section - onto an entire unrelated bearer-token-auth package
    /// just to borrow one field), which is worse than the small duplication risk here. If the two ever
    /// drift, fix both - <c>OAuth2BearerOptions.KnownSigningAlgorithms</c>'s remarks are the source of
    /// truth for what belongs on this list and why.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> KnownSigningAlgorithms = new(System.StringComparer.Ordinal)
    {
        SecurityAlgorithms.HmacSha256, SecurityAlgorithms.HmacSha384, SecurityAlgorithms.HmacSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    };

    /// <summary>
    /// #177's pragmatic entropy floor: the minimum number of DISTINCT byte values a
    /// <see cref="SigningKey"/> must contain, regardless of its total length. Not derived from any real
    /// entropy calculation - just low enough that a genuinely random 32+ byte secret in any common
    /// shape (raw ASCII passphrase, hex, base64) clears it by a wide margin, and high enough to reject
    /// the degenerate placeholder shapes this exists to catch (a single repeated character, or a short
    /// alternating pattern stretched to meet the length check).
    /// </summary>
    private const int MinimumDistinctSigningKeyBytes = 8;

    private static int DistinctByteCount(byte[] bytes)
    {
        var seen = new System.Collections.Generic.HashSet<byte>();
        foreach (var b in bytes)
        {
            seen.Add(b);
        }

        return seen.Count;
    }

    /// <summary>
    /// #286: true when <paramref name="bytes"/> is an exact repetition of one of its own proper
    /// prefixes whose length (period) is strictly under half the array's total length - i.e. the array
    /// tiles a block at least three times, or tiles a block exactly twice where that block is itself
    /// under half the total length (impossible - two tiles of a block always sum to exactly double the
    /// block, so "under half" only ever fires for three-or-more tiles). Deliberately does NOT flag a
    /// key made of exactly two tiles of a block that IS half the key's length (e.g. a 16-byte block
    /// repeated twice to fill 32 bytes) - a real 16+ byte secret duplicated once is not meaningfully
    /// distinguishable from 32 bytes of real keyspace by a cheap structural check, and flagging it would
    /// reject shapes like <c>"0123456789abcdef" x 2</c> that this file's own tests already treat as
    /// acceptable. What this DOES catch: a short block (e.g. 8 bytes) tiled 3+ times to reach the
    /// length floor - real keyspace equal to the block, not the whole key.
    /// </summary>
    private static bool HasLowPeriodRepeatingBlock(byte[] bytes)
    {
        var length = bytes.Length;
        for (var period = 1; period < length / 2; period++)
        {
            if (length % period != 0)
            {
                continue;
            }

            var isTiled = true;
            for (var i = period; i < length; i++)
            {
                if (bytes[i] != bytes[i % period])
                {
                    isTiled = false;
                    break;
                }
            }

            if (isTiled)
            {
                return true;
            }
        }

        return false;
    }
}
