using Microsoft.IdentityModel.Tokens;

namespace Benzene.Auth.OAuth2;

/// <summary>
/// Configuration for <see cref="Extensions.UseOAuth2Bearer{TContext}"/>. Every field below is
/// deliberately required, with no permissive silent default - see each property's remarks for why.
/// </summary>
public class OAuth2BearerOptions
{
    /// <summary>
    /// The OIDC discovery URL (".../.well-known/openid-configuration"), used to fetch and
    /// auto-refresh the JWKS. Set this OR <see cref="JwksUri"/>, not both - most identity providers
    /// (Auth0, Cognito, Azure AD, Okta) expose full OIDC discovery; <see cref="JwksUri"/> is the
    /// escape hatch for ones that only publish a bare JWKS document.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// A bare JWKS document URL, for identity providers that don't expose full OIDC discovery. Set
    /// this OR <see cref="Authority"/>, not both.
    /// </summary>
    public string? JwksUri { get; set; }

    /// <summary>
    /// Every issuer this service trusts. Required - a token whose <c>iss</c> claim isn't in this
    /// list is rejected before signature validation even runs. No default: an empty list must fail
    /// fast at wire-up (see <see cref="Extensions.UseOAuth2Bearer{TContext}"/>), not silently
    /// accept tokens from any issuer.
    /// </summary>
    public string[] ValidIssuers { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Every audience this service accepts. Required for the same reason as
    /// <see cref="ValidIssuers"/> - a token minted for a different service must not be accepted
    /// here (the classic token-confusion mistake).
    /// </summary>
    public string[] ValidAudiences { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Explicit signing-algorithm allowlist (e.g. <c>"RS256"</c> - see
    /// <see cref="Microsoft.IdentityModel.Tokens.SecurityAlgorithms"/> for the standard constants).
    /// Required, no default: a JWT validator that trusts whatever <c>alg</c> the token itself
    /// claims is vulnerable to algorithm-confusion attacks (RFC 8725 §3.1) - this library will not
    /// do that. See this package's <c>CLAUDE.md</c> for the full rationale.
    /// </summary>
    public string[] ValidAlgorithms { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Clock skew tolerance applied to <c>exp</c>/<c>nbf</c> validation. Defaults to 2 minutes. Capped
    /// at <see cref="MaxClockSkew"/> by <see cref="Validate"/> - an unbounded value here silently
    /// disables expiration enforcement in practice (a token that "expired" ten years ago would still
    /// validate), turning <c>exp</c> from a hard boundary into a suggestion.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The maximum <see cref="ClockSkew"/> <see cref="Validate"/> allows: 15 minutes. Generous enough
    /// to absorb real multi-region NTP drift, far too small to meaningfully weaken <c>exp</c>/<c>nbf</c>
    /// enforcement - unlike the wildly larger values (hours, days, years) an accidental unit mistake
    /// (e.g. constructing a <see cref="TimeSpan"/> from the wrong overload) actually produces.
    /// </summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Signing algorithms <see cref="ValidAlgorithms"/> entries are checked against - the standard JWS
    /// signing algorithms (RFC 7518 §3.1) <see cref="Microsoft.IdentityModel.Tokens.SecurityAlgorithms"/>
    /// defines constants for. Deliberately narrower than every string constant that class exposes (it
    /// also has XML-dsig URIs, key-wrap, and content-encryption algorithm names that are never a JWT
    /// <c>alg</c> value) - this is specifically the set a bearer-token validator's allowlist can
    /// legitimately contain.
    /// </summary>
    private static readonly HashSet<string> KnownSigningAlgorithms = new(StringComparer.Ordinal)
    {
        SecurityAlgorithms.HmacSha256, SecurityAlgorithms.HmacSha384, SecurityAlgorithms.HmacSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    };

    /// <summary>
    /// Whether <see cref="Authority"/>/<see cref="JwksUri"/> must be fetched over HTTPS. Defaults
    /// to <c>true</c> - fetching the document that establishes trust (the JWKS) over plain HTTP is
    /// vulnerable to a man-in-the-middle substituting a different signing key, so every real
    /// identity provider serves this over HTTPS and this stays required by default. Set to
    /// <c>false</c> only for local development/testing against a plain-HTTP fake JWKS endpoint -
    /// the same escape hatch ASP.NET Core's own <c>JwtBearerOptions.RequireHttpsMetadata</c>
    /// provides for the identical reason. Never set this <c>false</c> in production.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Validates this instance, throwing <see cref="ArgumentException"/> for any wire-up mistake
    /// that would otherwise silently under-validate every token this middleware sees. Called by
    /// <see cref="Extensions.UseOAuth2Bearer{TContext}"/> at pipeline wire-up time - fail fast,
    /// not on the first request.
    /// </summary>
    internal void Validate()
    {
        var hasAuthority = !string.IsNullOrWhiteSpace(Authority);
        var hasJwksUri = !string.IsNullOrWhiteSpace(JwksUri);

        if (hasAuthority == hasJwksUri)
        {
            throw new ArgumentException(
                $"Exactly one of {nameof(Authority)} or {nameof(JwksUri)} must be set (not both, not neither).",
                hasAuthority ? nameof(JwksUri) : nameof(Authority));
        }

        // The same bug class as MeshAuthGate's #20 ruling (see its Validate() remarks): fetching the
        // JWKS document that establishes trust over plain HTTP is a man-in-the-middle vector, and
        // letting a non-https Authority/JwksUri reach the middleware unvalidated used to fail silently
        // at request time instead - not with a crash (unlike the OIDC discovery case), but worse: every
        // token would keep failing signature validation with a generic 401, forever, with nothing in
        // the config to point at. Reject it here instead, unless the operator has explicitly opted into
        // plain HTTP via RequireHttpsMetadata: false - the same escape hatch this option exists for.
        if (RequireHttpsMetadata)
        {
            var metadataUrl = hasAuthority ? Authority : JwksUri;
            if (Uri.TryCreate(metadataUrl, UriKind.Absolute, out var metadataUri) &&
                string.Equals(metadataUri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"{(hasAuthority ? nameof(Authority) : nameof(JwksUri))} ('{metadataUrl}') is not " +
                    $"https, and {nameof(RequireHttpsMetadata)} is true (the default) - fetching the " +
                    "JWKS over plain HTTP is a man-in-the-middle risk with nothing to detect a spoofed " +
                    $"signing key. This is allowed ONLY for local development: set {nameof(RequireHttpsMetadata)} " +
                    "to false explicitly if that's genuinely the case here - never in production.",
                    hasAuthority ? nameof(Authority) : nameof(JwksUri));
            }
        }

        ValidateEntries(ValidIssuers, nameof(ValidIssuers), "issuer", "tokens from any issuer");
        ValidateEntries(ValidAudiences, nameof(ValidAudiences), "audience", "tokens minted for any audience");

        if (ValidAlgorithms is not { Length: > 0 })
        {
            throw new ArgumentException(
                $"{nameof(ValidAlgorithms)} must contain at least one allowed signing algorithm - " +
                "an empty list would trust whatever \"alg\" the token itself claims (RFC 8725 §3.1 algorithm confusion).",
                nameof(ValidAlgorithms));
        }

        foreach (var algorithm in ValidAlgorithms)
        {
            if (string.IsNullOrWhiteSpace(algorithm))
            {
                throw new ArgumentException(
                    $"{nameof(ValidAlgorithms)} contains a null/empty/whitespace entry - every entry must " +
                    "be a genuine signing algorithm name.",
                    nameof(ValidAlgorithms));
            }

            // Explicit, named rejection - RFC 8725 §3.1's canonical algorithm-confusion attack is
            // exactly "alg": "none" accepted by a validator that never meant to allow it. Called out
            // separately from the "unrecognized name" check below so the error is unambiguous about why.
            if (string.Equals(algorithm, SecurityAlgorithms.None, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"{nameof(ValidAlgorithms)} must not contain \"{SecurityAlgorithms.None}\" - accepting " +
                    "the unsigned algorithm defeats signature validation entirely (RFC 8725 §3.1 algorithm confusion).",
                    nameof(ValidAlgorithms));
            }

            if (!KnownSigningAlgorithms.Contains(algorithm))
            {
                throw new ArgumentException(
                    $"{nameof(ValidAlgorithms)} contains '{algorithm}', which is not a recognized JWS " +
                    "signing algorithm (see Microsoft.IdentityModel.Tokens.SecurityAlgorithms) - likely a " +
                    "typo that would silently make this entry unmatchable by any real token.",
                    nameof(ValidAlgorithms));
            }
        }

        if (ClockSkew < TimeSpan.Zero || ClockSkew > MaxClockSkew)
        {
            throw new ArgumentException(
                $"{nameof(ClockSkew)} ({ClockSkew}) must be between zero and {MaxClockSkew} - a larger " +
                "value silently weakens exp/nbf enforcement rather than tolerating genuine clock drift.",
                nameof(ClockSkew));
        }
    }

    /// <summary>
    /// Shared by <see cref="ValidIssuers"/>/<see cref="ValidAudiences"/>: both must be non-empty overall
    /// (checked by the caller before this runs) AND contain no entry that would defeat the allowlist -
    /// null/whitespace (never matches a real claim, so it's pure noise at best) or the literal
    /// <c>"*"</c> wildcard (a common but wrong instinct for "allow anything", which
    /// <see cref="Microsoft.IdentityModel.Tokens.TokenValidationParameters"/> does not treat specially -
    /// it would only ever match a token whose issuer/audience claim is literally the string "*", never
    /// actually acting as a wildcard, so its presence signals a config author who believed they'd
    /// disabled the check when they hadn't).
    /// </summary>
    private static void ValidateEntries(string[] entries, string propertyName, string entryNoun, string emptyListRisk)
    {
        if (entries is not { Length: > 0 })
        {
            throw new ArgumentException(
                $"{propertyName} must contain at least one trusted {entryNoun} - an empty list would accept {emptyListRisk}.",
                propertyName);
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                throw new ArgumentException(
                    $"{propertyName} contains a null/empty/whitespace entry - every entry must be a genuine {entryNoun}.",
                    propertyName);
            }

            if (entry == "*")
            {
                throw new ArgumentException(
                    $"{propertyName} contains \"*\" - this is not a wildcard to token validation (it would " +
                    $"only ever match a token whose {entryNoun} claim is literally \"*\"), so its presence " +
                    "means this allowlist does not do what it looks like it does. Remove it, or list the " +
                    $"real {entryNoun}(s) this service trusts.",
                    propertyName);
            }
        }
    }
}
