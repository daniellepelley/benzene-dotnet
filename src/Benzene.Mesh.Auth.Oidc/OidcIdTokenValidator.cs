using System;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>The outcome of <see cref="OidcIdTokenValidator.ValidateAsync"/>: either a verified,
/// allowlist-ready email, or a failure reason meant for server-side logs only (see
/// <see cref="OidcCallbackMiddleware{TContext}"/>'s "no detail leakage" handling).</summary>
internal sealed class OidcIdTokenValidationResult
{
    private OidcIdTokenValidationResult(bool isValid, string? email, string? failureReason)
    {
        IsValid = isValid;
        Email = email;
        FailureReason = failureReason;
    }

    public bool IsValid { get; }
    public string? Email { get; }
    public string? FailureReason { get; }

    public static OidcIdTokenValidationResult Success(string email) => new(true, email, null);
    public static OidcIdTokenValidationResult Failure(string reason) => new(false, null, reason);
}

/// <summary>
/// Verifies a Google/OIDC ID token end to end: signature against the provider's JWKS (via the shared
/// <see cref="ConfigurationManager{OpenIdConnectConfiguration}"/>, resolved and cached by
/// <see cref="OidcConfigurationManagerFactory"/> - key rotation is handled by that same
/// auto-refresh-on-unrecognized-<c>kid</c> mechanism <c>Benzene.Auth.OAuth2</c> relies on),
/// <c>iss</c>, <c>aud</c>, <c>exp</c> (all via <see cref="Microsoft.IdentityModel.Tokens.TokenValidationParameters"/>,
/// built once at wire-up time - see <see cref="Extensions.UseMeshOidcAuth{TContext}"/>), and finally
/// the OIDC-specific check standard JWT validation does NOT do: <c>email_verified</c> must be
/// <c>true</c> before the <c>email</c> claim is trusted at all.
/// </summary>
internal sealed class OidcIdTokenValidator
{
    private readonly JsonWebTokenHandler _handler;
    private readonly TokenValidationParameters _validationParameters;

    public OidcIdTokenValidator(JsonWebTokenHandler handler, TokenValidationParameters validationParameters)
    {
        _handler = handler;
        _validationParameters = validationParameters;
    }

    public async Task<OidcIdTokenValidationResult> ValidateAsync(string idToken)
    {
        TokenValidationResult result;
        try
        {
            result = await _handler.ValidateTokenAsync(idToken, _validationParameters);
        }
        catch (Exception ex)
        {
            // ValidateTokenAsync is documented to report failures via TokenValidationResult rather than
            // throwing, but an unreachable JWKS/discovery endpoint or similar infrastructure failure can
            // still surface as an exception - treat it as an ordinary validation failure.
            return OidcIdTokenValidationResult.Failure($"ID token validation threw: {ex.Message}");
        }

        if (!result.IsValid)
        {
            // Bad signature, expired, wrong issuer/audience/algorithm - never distinguished to the
            // caller-facing side, only logged server-side (same "no detail leakage" reasoning as
            // Benzene.Auth.OAuth2 - see its CLAUDE.md).
            return OidcIdTokenValidationResult.Failure(result.Exception?.Message ?? "ID token failed validation.");
        }

        var claims = result.ClaimsIdentity;

        // Google (and OIDC generally) emits email_verified as a JSON boolean; JsonWebTokenHandler
        // surfaces it as the claim's string value ("true"/"false"). Absent entirely -> not verified ->
        // fail closed. This is the check plain JWT/bearer validation has no reason to make but an ID
        // token consumer MUST: the "email" claim is caller-asserted-but-provider-attested only when
        // email_verified is true - trusting it otherwise is exactly the "unverified email claim"
        // mistake this package's brief calls out by name.
        var emailVerifiedClaim = claims.FindFirst("email_verified")?.Value;
        var emailVerified = string.Equals(emailVerifiedClaim, "true", StringComparison.OrdinalIgnoreCase);
        if (!emailVerified)
        {
            return OidcIdTokenValidationResult.Failure("email_verified claim is missing or not true.");
        }

        var email = claims.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(email))
        {
            return OidcIdTokenValidationResult.Failure("ID token has no email claim.");
        }

        return OidcIdTokenValidationResult.Success(email.Trim().ToLowerInvariant());
    }
}
