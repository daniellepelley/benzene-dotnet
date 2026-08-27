using System;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>The session cookie's payload: the verified <c>email</c> (already lowercased), an expiry,
/// and a per-session <c>Jti</c>. Email/Exp are not secret (the mesh already shows the email is
/// allowlisted; this only proves the browser presenting the cookie logged in once), so signing
/// (tamper-evidence) rather than encrypting is the right property - see this package's
/// <c>CLAUDE.md</c>.</summary>
/// <param name="Email">The verified, already-lowercased <c>email</c> claim.</param>
/// <param name="Exp">Unix-seconds expiry.</param>
/// <param name="Jti">
/// #178: a random, per-issuance identifier - present so a FUTURE server-side deny-list could revoke one
/// specific session without breaking the cookie's wire shape, not because anything reads or checks it
/// today. Today's logout is client-side only (it only ever clears the CALLER's own cookie - see this
/// package's <c>CLAUDE.md</c> "Stateless logout" section), so a copy of a session cookie that has
/// already left the original holder's browser stays valid, unrevocable, until its own <c>Exp</c> -
/// this field alone does not change that; it only makes closing that gap possible later without a
/// cookie-format break. Defaults to <c>""</c> (rather than being required) so a pre-existing serialized
/// payload - or a test that only cares about <c>Email</c>/<c>Exp</c> - still deserializes/constructs
/// cleanly.
/// </param>
internal sealed record OidcSessionPayload(string Email, long Exp, string Jti = "");

/// <summary>Mints and validates the session cookie as a signed, expiring token (see
/// <see cref="SignedToken"/>).</summary>
internal static class OidcSessionToken
{
    /// <summary>Creates a new signed session token for <paramref name="email"/>, valid for
    /// <paramref name="duration"/> from now. Carries a fresh random <c>Jti</c> - see
    /// <see cref="OidcSessionPayload"/>'s remarks.</summary>
    public static string Create(byte[] key, string email, TimeSpan duration)
    {
        var exp = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeSeconds();
        var jti = Guid.NewGuid().ToString("N");
        return SignedToken.Create(key, new OidcSessionPayload(email, exp, jti));
    }

    /// <summary>
    /// Validates a session cookie value: signature must verify and the token must not be expired.
    /// Deliberately does NOT re-check the allowlist here - the caller (<see cref="OidcSessionGateMiddleware{TContext}"/>)
    /// does that against the live, current <see cref="MeshOidcOptions.AllowedEmails"/>, not whatever
    /// was true when the cookie was minted, so revoking an email takes effect on the next request even
    /// for an existing, otherwise-still-valid session.
    /// </summary>
    public static bool TryValidate(byte[] key, string? cookieValue, out string email)
    {
        email = string.Empty;

        if (!SignedToken.TryParse<OidcSessionPayload>(key, cookieValue, out var payload) || payload is null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > payload.Exp)
        {
            return false;
        }

        // Defense in depth against cross-token confusion: the state token and the session token share
        // one signing key and both happen to carry an "Exp" field (see this package's CLAUDE.md), so a
        // syntactically-valid-but-wrong-shaped payload (e.g. a state token's JSON deserialized as this
        // type) would otherwise pass with a null Email - reject that explicitly rather than relying on
        // every caller to separately guard a null/empty email before using it.
        if (string.IsNullOrWhiteSpace(payload.Email))
        {
            return false;
        }

        email = payload.Email;
        return true;
    }
}
