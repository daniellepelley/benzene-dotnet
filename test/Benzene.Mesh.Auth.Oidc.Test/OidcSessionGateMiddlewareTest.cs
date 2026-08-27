using System;
using System.Text;
using System.Threading.Tasks;
using Benzene.Mesh.Auth.Oidc.Test.Fakes;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class OidcSessionGateMiddlewareTest
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    private static MeshOidcOptions Options() => new()
    {
        Issuer = "https://accounts.google.com",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        SigningKey = Encoding.UTF8.GetString(Key),
        AllowedEmails = new[] { "user@example.com" },
        BasePath = "/mesh/auth",
    };

    private static OidcSessionGateMiddleware<FakeHttpContext> CreateGate(MeshOidcOptions options) =>
        new(options, Key, new FakeHttpRequestAdapter(), new FakeResponseAdapter(), new FakeQueryStringReader());

    [Fact]
    public async Task ValidSessionForAllowlistedEmail_CallsNext()
    {
        var options = Options();
        var gate = CreateGate(options);
        var session = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(24));
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh-ui",
            Headers = { ["cookie"] = $"benzene_mesh_session={session}" },
        };

        var nextCalled = false;
        await gate.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.Null(context.StatusCode);
    }

    [Fact]
    public async Task NoSessionCookie_HtmlRequest_RedirectsToLogin()
    {
        var gate = CreateGate(Options());
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh-ui",
            Headers = { ["accept"] = "text/html,application/xhtml+xml" },
        };

        var nextCalled = false;
        await gate.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(302, context.StatusCode);
        Assert.NotNull(context.Location);
        Assert.StartsWith("/mesh/auth/login?returnTo=", context.Location);
        Assert.Contains(Uri.EscapeDataString("/mesh-ui"), context.Location);
    }

    [Fact]
    public async Task NoSessionCookie_JsonFetch_Returns401NotRedirect()
    {
        var gate = CreateGate(Options());
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/benzene/invoke",
            Headers = { ["accept"] = "application/json" },
        };

        var nextCalled = false;
        await gate.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(401, context.StatusCode);
        Assert.Null(context.Location);
    }

    [Fact]
    public async Task MissingAcceptHeader_Returns401NotRedirect()
    {
        // No Accept header at all - treated as "not a browser navigation", the safer default (a 302
        // response body is never useful to a caller that didn't ask for HTML).
        var gate = CreateGate(Options());
        var context = new FakeHttpContext { Method = "POST", Path = "/mesh/refresh" };

        await gate.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(401, context.StatusCode);
    }

    [Fact]
    public async Task ExpiredSession_IsRejected()
    {
        var options = Options();
        var gate = CreateGate(options);
        var expiredPayload = new OidcSessionPayload("user@example.com", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
        var expiredSession = SignedToken.Create(Key, expiredPayload);
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh-ui",
            Headers = { ["cookie"] = $"benzene_mesh_session={expiredSession}", ["accept"] = "text/html" },
        };

        var nextCalled = false;
        await gate.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(302, context.StatusCode);
    }

    [Fact]
    public async Task TamperedSessionPayload_IsRejected()
    {
        // Flips a character INSIDE the payload segment - the half an attacker would actually edit,
        // to change the email or push out the expiry. Every character there is fully significant,
        // so the edit always changes the signed bytes.
        var session = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(24));
        var dot = session.IndexOf('.');
        var at = dot / 2;
        var tampered = session[..at] + (session[at] == 'A' ? 'B' : 'A') + session[(at + 1)..];
        Assert.NotEqual(session, tampered);

        Assert.False(await SessionCookieIsAccepted(tampered));
    }

    [Fact]
    public async Task TamperedSessionSignature_IsRejected()
    {
        // The signature half, tampered at the BYTE level rather than by editing a base64 character.
        //
        // This test used to flip the token's last character, which is flaky by construction: the
        // signature is 32 bytes, which is 43 base64url characters, and 43 * 6 = 258 bits - so the
        // final character carries only 4 significant bits and its low 2 bits are dropped on decode.
        // Characters differing only in those bits decode to identical signatures, so the "tampered"
        // token was byte-identical to the real one and was correctly ACCEPTED, failing the test.
        // Measured at roughly 7% of runs: often enough to redden CI now and then, rarely enough to
        // be dismissed as noise - on a test whose whole job is to prove tamper-evidence.
        var session = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(24));
        var dot = session.IndexOf('.');
        var payload = session[..dot];
        var signature = Base64UrlDecodeForTest(session[(dot + 1)..]);

        signature[0] ^= 0x01; // one bit, in a byte that is always significant
        var tampered = payload + "." + Base64UrlEncodeForTest(signature);
        Assert.NotEqual(session, tampered);

        Assert.False(await SessionCookieIsAccepted(tampered));
    }

    [Fact]
    public async Task SessionSignedWithADifferentKey_IsRejected()
    {
        // Same shape, valid signature - but under a key this gate does not hold.
        var otherKey = Encoding.UTF8.GetBytes("ffffffffffffffffffffffffffffffff");
        var session = OidcSessionToken.Create(otherKey, "user@example.com", TimeSpan.FromHours(24));

        Assert.False(await SessionCookieIsAccepted(session));
    }

    /// <summary>Runs one cookie through the gate and reports whether it reached the next middleware.</summary>
    private static async Task<bool> SessionCookieIsAccepted(string cookieValue)
    {
        var gate = CreateGate(Options());
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh-ui",
            Headers = { ["cookie"] = $"benzene_mesh_session={cookieValue}", ["accept"] = "text/html" },
        };

        var nextCalled = false;
        await gate.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });
        return nextCalled;
    }

    private static byte[] Base64UrlDecodeForTest(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncodeForTest(byte[] value) =>
        Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [Fact]
    public async Task ValidSessionButEmailRemovedFromAllowlist_IsRejected()
    {
        // Re-checked against the LIVE allowlist, not just trusted from the cookie - this is the
        // "revoking access takes effect immediately" property.
        var options = Options();
        options.AllowedEmails = new[] { "someone-else@example.com" };
        var gate = CreateGate(options);
        var session = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(24));
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh-ui",
            Headers = { ["cookie"] = $"benzene_mesh_session={session}", ["accept"] = "text/html" },
        };

        var nextCalled = false;
        await gate.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(302, context.StatusCode);
    }

    /// <summary>
    /// #180: <c>returnTo</c> used to be built from the LOWERCASED request (path included, not just
    /// header names), so a case-sensitive deep link - an S3 object key, a service-cased JSON route -
    /// 404'd after a successful login even though the ORIGINAL request would have resolved fine. The
    /// path segment in the redirect must come back in its original casing.
    /// </summary>
    [Fact]
    public async Task NoSessionCookie_HtmlRequest_RedirectsToLogin_PreservingOriginalPathCasing()
    {
        var gate = CreateGate(Options());
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/Mesh-UI/Reports/MyReport.JSON",
            Headers = { ["accept"] = "text/html" },
        };

        await gate.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(302, context.StatusCode);
        Assert.NotNull(context.Location);
        Assert.Contains(Uri.EscapeDataString("/Mesh-UI/Reports/MyReport.JSON"), context.Location);
        // The BasePath prefix of the redirect target itself is fine to be whatever casing the option
        // was configured with - this assertion is specifically about the embedded original path, not
        // about the login URL's own casing.
    }

    [Fact]
    public async Task EmptyAllowlist_RejectsEveryone_EvenWithValidSignature()
    {
        var options = Options();
        options.AllowedEmails = Array.Empty<string>();
        var gate = CreateGate(options);
        var session = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(24));
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh-ui",
            Headers = { ["cookie"] = $"benzene_mesh_session={session}", ["accept"] = "application/json" },
        };

        await gate.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(401, context.StatusCode);
    }
}
