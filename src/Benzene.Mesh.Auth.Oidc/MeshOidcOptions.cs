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
    /// never hand-typed or committed. <see cref="Validate"/> fails fast if this is missing or too short
    /// to be a genuine secret, rather than silently signing with a weak/guessable key.
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

    /// <summary>How long an issued session cookie remains valid. Defaults to 24 hours.</summary>
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
        if (string.IsNullOrWhiteSpace(SigningKey) || System.Text.Encoding.UTF8.GetByteCount(SigningKey) < 32)
        {
            throw new System.ArgumentException(
                $"{nameof(SigningKey)} is required and must be at least 32 bytes - a real, " +
                "randomly-generated secret (e.g. Terraform's random_password), never a hardcoded or " +
                "guessable default. This signs the CSRF state token and the session cookie.",
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
    }
}
