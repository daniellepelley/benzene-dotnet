using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// A minimal "HMAC-SHA256-signed, base64url JSON payload" codec, shared by the CSRF state token
/// (<see cref="OidcStateToken"/>) and the session cookie (<see cref="OidcSessionToken"/>). Both
/// payloads are non-secret ({email, exp} and {nonce, returnTo, exp}) - the property this codec
/// guarantees is tamper-evidence, not confidentiality, so this signs rather than encrypts (see this
/// package's <c>CLAUDE.md</c> for the rationale). Wire shape: <c>base64url(json) + "." +
/// base64url(HMAC-SHA256(key, base64url(json)))</c> - deliberately not a JWT (no algorithm negotiation,
/// so no algorithm-confusion surface to defend here at all).
/// </summary>
internal static class SignedToken
{
    /// <summary>Serializes and signs <paramref name="payload"/> under <paramref name="key"/>.</summary>
    public static string Create<T>(byte[] key, T payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var payloadSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        var signature = ComputeSignature(key, payloadSegment);
        return payloadSegment + "." + Base64UrlEncode(signature);
    }

    /// <summary>
    /// Verifies <paramref name="token"/>'s signature under <paramref name="key"/> (constant-time) and,
    /// only if it matches, deserializes the payload. Returns false for a missing/malformed token, a
    /// signature mismatch (tampered or signed under a different key), or a payload that fails to
    /// deserialize as <typeparamref name="T"/> - callers cannot distinguish which, by design (there is
    /// nothing a caller should do differently for any of these).
    /// </summary>
    public static bool TryParse<T>(byte[] key, string? token, out T? payload)
    {
        payload = default;

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var separatorIndex = token.IndexOf('.');
        if (separatorIndex < 0 || separatorIndex == token.Length - 1)
        {
            return false;
        }

        var payloadSegment = token[..separatorIndex];
        var signatureSegment = token[(separatorIndex + 1)..];

        byte[] providedSignature;
        try
        {
            providedSignature = Base64UrlDecode(signatureSegment);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = ComputeSignature(key, payloadSegment);

        // Constant-time: a timing difference between "signature wrong" and "signature right but JSON
        // malformed" would otherwise let an attacker binary-search a forged signature into existence.
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(payloadSegment));
            payload = JsonSerializer.Deserialize<T>(json);
            return payload is not null;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] ComputeSignature(byte[] key, string payloadSegment)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadSegment));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new FormatException("Invalid base64url segment length.");
        }

        return Convert.FromBase64String(padded);
    }
}
