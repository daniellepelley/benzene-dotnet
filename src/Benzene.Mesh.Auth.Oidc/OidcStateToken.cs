using System;
using System.Security.Cryptography;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>The CSRF state token's payload: a random nonce (belt-and-braces alongside the
/// double-submit check below), the validated <c>returnTo</c> path to restore after login, and an
/// expiry. Carried as both the <c>state</c> query parameter sent to the provider AND the value of the
/// short-lived state cookie set at <c>/login</c> - see <see cref="OidcStateToken.TryValidate"/>.</summary>
internal sealed record OidcStatePayload(string Nonce, string ReturnTo, long Exp);

/// <summary>
/// Mints and validates the OAuth2 <c>state</c> parameter as a signed, short-lived token (see
/// <see cref="SignedToken"/>) - the CSRF/replay defense for the login-&gt;callback round trip.
/// </summary>
internal static class OidcStateToken
{
    /// <summary>How long a state token remains valid - long enough for a real login (provider
    /// consent screen, 2FA, etc.), short enough that a captured/leaked value is useless soon after.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// <summary>Creates a new signed state token embedding the given (already-validated) return path.</summary>
    public static string Create(byte[] key, string returnTo)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var exp = DateTimeOffset.UtcNow.Add(Ttl).ToUnixTimeSeconds();
        return SignedToken.Create(key, new OidcStatePayload(nonce, returnTo, exp));
    }

    /// <summary>
    /// Validates a callback's <c>state</c> query parameter against the state cookie set at
    /// <c>/login</c>: both must be present, byte-identical (double-submit - an attacker who can only
    /// steer a victim to a crafted callback URL can never have set the victim's own HttpOnly cookie),
    /// and the token itself must carry a valid signature and be unexpired. Returns the embedded
    /// <c>returnTo</c> on success.
    /// </summary>
    public static bool TryValidate(byte[] key, string? stateFromQuery, string? stateFromCookie, out string returnTo)
    {
        returnTo = "/";

        if (string.IsNullOrEmpty(stateFromQuery) || string.IsNullOrEmpty(stateFromCookie))
        {
            return false;
        }

        if (!ConstantTimeEquals(stateFromQuery, stateFromCookie))
        {
            return false;
        }

        if (!SignedToken.TryParse<OidcStatePayload>(key, stateFromCookie, out var payload) || payload is null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > payload.Exp)
        {
            return false;
        }

        // Defense in depth against cross-token confusion (see OidcSessionToken.TryValidate's identical
        // reasoning): reject explicitly rather than handing back a null/empty Nonce or ReturnTo.
        if (string.IsNullOrWhiteSpace(payload.Nonce) || string.IsNullOrWhiteSpace(payload.ReturnTo))
        {
            return false;
        }

        returnTo = payload.ReturnTo;
        return true;
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
