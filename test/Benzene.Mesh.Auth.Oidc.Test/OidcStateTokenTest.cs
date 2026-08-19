using System;
using System.Text;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class OidcStateTokenTest
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    [Fact]
    public void MatchingQueryAndCookie_ValidatesAndReturnsReturnTo()
    {
        var state = OidcStateToken.Create(Key, "/mesh-ui");

        var ok = OidcStateToken.TryValidate(Key, state, state, out var returnTo);

        Assert.True(ok);
        Assert.Equal("/mesh-ui", returnTo);
    }

    [Fact]
    public void MismatchedQueryAndCookie_FailsCsrfCheck()
    {
        var stateA = OidcStateToken.Create(Key, "/mesh-ui");
        var stateB = OidcStateToken.Create(Key, "/mesh-ui");

        // Two independently-minted, individually-valid tokens - the double-submit check must still
        // reject them as a pair since they are not byte-identical (this is what actually defeats an
        // attacker who can forge a URL but not the victim's cookie).
        var ok = OidcStateToken.TryValidate(Key, stateA, stateB, out _);

        Assert.False(ok);
    }

    [Fact]
    public void MissingCookie_FailsValidation()
    {
        var state = OidcStateToken.Create(Key, "/mesh-ui");

        var ok = OidcStateToken.TryValidate(Key, state, null, out _);

        Assert.False(ok);
    }

    [Fact]
    public void MissingQueryState_FailsValidation()
    {
        var state = OidcStateToken.Create(Key, "/mesh-ui");

        var ok = OidcStateToken.TryValidate(Key, null, state, out _);

        Assert.False(ok);
    }

    [Fact]
    public void BothMissing_FailsValidation()
    {
        var ok = OidcStateToken.TryValidate(Key, null, null, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TamperedState_FailsValidation()
    {
        var state = OidcStateToken.Create(Key, "/mesh-ui");

        // Flip the FIRST character of the signature segment, not the last character of the token.
        // The signature is 32 bytes = 43 base64url characters, and 43 * 6 = 258 bits, so the final
        // character carries only 4 significant bits - two characters differing solely in the two
        // dropped bits decode to an identical signature, leaving the "tampered" token byte-identical
        // to the real one and correctly accepted. That made this test fail on roughly 7% of runs.
        // Character 0 of either segment is always fully significant, which is the idiom the rest of
        // these tamper tests already use (see SignedTokenTest).
        var parts = state.Split('.');
        var tampered = parts[0] + "." + (parts[1][0] == 'a' ? 'b' : 'a') + parts[1][1..];
        Assert.NotEqual(state, tampered);

        var ok = OidcStateToken.TryValidate(Key, tampered, tampered, out _);

        Assert.False(ok);
    }

    [Fact]
    public void CrossTokenConfusion_ASessionTokenIsNotAcceptedAsAStateToken()
    {
        // The session token and state token share one signing key and both carry an "Exp" field - a
        // validly-signed session token presented as both the query state and the state cookie must
        // still be rejected (it deserializes with a null Nonce/ReturnTo), not silently accepted.
        var sessionToken = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(1));

        var ok = OidcStateToken.TryValidate(Key, sessionToken, sessionToken, out var returnTo);

        Assert.False(ok);
        Assert.Equal("/", returnTo);
    }

    [Fact]
    public void ReplayAfterExpiry_FailsValidation()
    {
        // Exercise TryValidate's expiry check directly via SignedToken, since OidcStateToken.Create
        // hardcodes a 10-minute TTL - construct an already-expired payload the same way Create() would,
        // but with Exp in the past.
        var expiredPayload = new OidcStatePayload("nonce", "/mesh-ui", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
        var expiredToken = SignedToken.Create(Key, expiredPayload);

        var ok = OidcStateToken.TryValidate(Key, expiredToken, expiredToken, out _);

        Assert.False(ok);
    }
}
