using System.Text;
using System.Threading.Tasks;
using Benzene.Mesh.Auth.Oidc.Test.Fakes;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class OidcLogoutMiddlewareTest
{
    private static MeshOidcOptions Options() => new()
    {
        Issuer = "https://accounts.google.com",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        SigningKey = new string('k', 32),
        AllowedEmails = new[] { "user@example.com" },
        BasePath = "/mesh/auth",
    };

    [Fact]
    public async Task NonMatchingPath_CallsNext()
    {
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(Options(), new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext { Method = "GET", Path = "/mesh-ui" };

        var nextCalled = false;
        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
    }

    /// <summary>
    /// #175 / round 1's #4: a bare GET used to sign the caller out directly - a cross-site
    /// <c>&lt;img src="{BasePath}/logout"&gt;</c> is all that took, since <c>SameSite=Lax</c> still
    /// sends the session cookie along on a top-level GET navigation. Now a terminal 405, matching
    /// <c>MeshAuthGate.HandleLogoutAsync</c>'s identical ruling exactly - GET on the logout path is
    /// refused, never silently allowed through as a sign-out.
    /// </summary>
    [Fact]
    public async Task MatchingPath_Get_IsRejectedWith405()
    {
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(Options(), new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext { Method = "GET", Path = "/mesh/auth/logout" };

        var nextCalled = false;
        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(405, context.StatusCode);
        Assert.Empty(context.SetCookies);
    }

    [Fact]
    public async Task MatchingPath_PostWithoutCsrfHeader_IsRejectedWith403()
    {
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(Options(), new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext { Method = "POST", Path = "/mesh/auth/logout" };

        var nextCalled = false;
        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(403, context.StatusCode);
        Assert.Empty(context.SetCookies);
    }

    [Fact]
    public async Task MatchingPath_PostWithCsrfHeader_ClearsSessionCookieAndReturnsJson()
    {
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(Options(), new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext
        {
            Method = "POST",
            Path = "/mesh/auth/logout",
            Headers = { ["x-benzene-logout"] = "1" },
        };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(200, context.StatusCode);
        Assert.Equal("{\"redirect\":null}", context.Body);
        var setCookie = Assert.Single(context.SetCookies);
        Assert.StartsWith("benzene_mesh_session=;", setCookie);
        Assert.Contains("Max-Age=0", setCookie);
    }

    /// <summary>
    /// Case-insensitive header NAME lookup (values are compared as-is elsewhere, but the header name
    /// itself must not require the caller to send it in exactly the documented casing).
    /// </summary>
    [Fact]
    public async Task CsrfHeader_IsCaseInsensitiveByName()
    {
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(Options(), new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext
        {
            Method = "POST",
            Path = "/mesh/auth/logout",
            Headers = { ["X-Benzene-Logout"] = "1" },
        };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(200, context.StatusCode);
    }

    [Fact]
    public async Task MatchingPath_PostWithEmptyCsrfHeader_IsRejectedWith403()
    {
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(Options(), new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext
        {
            Method = "POST",
            Path = "/mesh/auth/logout",
            Headers = { ["x-benzene-logout"] = "   " },
        };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(403, context.StatusCode);
    }
}
