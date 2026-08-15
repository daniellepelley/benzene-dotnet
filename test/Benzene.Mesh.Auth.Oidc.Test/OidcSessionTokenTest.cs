using System;
using System.Text;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class OidcSessionTokenTest
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    [Fact]
    public void RoundTrip_ReturnsEmail()
    {
        var token = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(24));

        var ok = OidcSessionToken.TryValidate(Key, token, out var email);

        Assert.True(ok);
        Assert.Equal("user@example.com", email);
    }

    [Fact]
    public void Expired_FailsValidation()
    {
        var expiredPayload = new OidcSessionPayload("user@example.com", DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds());
        var token = SignedToken.Create(Key, expiredPayload);

        var ok = OidcSessionToken.TryValidate(Key, token, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TamperedCookie_FailsValidation()
    {
        var token = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(24));
        var parts = token.Split('.');
        var tamperedPayload = (parts[0][0] == 'a' ? 'b' : 'a') + parts[0][1..];
        var tampered = tamperedPayload + "." + parts[1];

        var ok = OidcSessionToken.TryValidate(Key, tampered, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TamperedToChangeEmail_FailsValidation()
    {
        // The specific attack this cookie must resist: an attacker edits the email in the payload to
        // impersonate a different (allowlisted) user. Build a forged token with a different email but
        // reuse the ORIGINAL signature - proves the signature is actually checked against the payload
        // content, not just present.
        var original = OidcSessionToken.Create(Key, "attacker@example.com", TimeSpan.FromHours(24));
        var originalSignature = original.Split('.')[1];

        var forgedPayload = new OidcSessionPayload("victim@example.com", DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());
        var forgedPayloadJson = System.Text.Json.JsonSerializer.Serialize(forgedPayload);
        var forgedPayloadSegment = Convert.ToBase64String(Encoding.UTF8.GetBytes(forgedPayloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var forgedToken = forgedPayloadSegment + "." + originalSignature;

        var ok = OidcSessionToken.TryValidate(Key, forgedToken, out _);

        Assert.False(ok);
    }

    [Fact]
    public void MissingCookie_FailsValidation()
    {
        var ok = OidcSessionToken.TryValidate(Key, null, out _);

        Assert.False(ok);
    }

    [Fact]
    public void CrossTokenConfusion_AStateTokenIsNotAcceptedAsASessionToken()
    {
        // The state token and session token share one signing key and both carry an "Exp" field - a
        // validly-signed state token must still be rejected here (it deserializes with a null Email),
        // not silently accepted as a session for nobody.
        var stateToken = OidcStateToken.Create(Key, "/mesh-ui");

        var ok = OidcSessionToken.TryValidate(Key, stateToken, out var email);

        Assert.False(ok);
        Assert.Equal(string.Empty, email);
    }
}
