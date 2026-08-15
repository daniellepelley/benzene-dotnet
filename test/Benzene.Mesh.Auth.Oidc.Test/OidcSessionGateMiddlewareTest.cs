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
    public async Task TamperedSessionCookie_IsRejected()
    {
        var gate = CreateGate(Options());
        var session = OidcSessionToken.Create(Key, "user@example.com", TimeSpan.FromHours(24));
        var tampered = session[..^1] + (session[^1] == 'a' ? 'b' : 'a');
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh-ui",
            Headers = { ["cookie"] = $"benzene_mesh_session={tampered}", ["accept"] = "text/html" },
        };

        var nextCalled = false;
        await gate.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
    }

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
