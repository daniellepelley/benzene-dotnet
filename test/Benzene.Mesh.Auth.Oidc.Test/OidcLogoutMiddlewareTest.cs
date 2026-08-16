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

    [Fact]
    public async Task MatchingPath_ClearsSessionCookieAndRedirectsHome()
    {
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(Options(), new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext { Method = "GET", Path = "/mesh/auth/logout" };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(302, context.StatusCode);
        Assert.Equal("/", context.Location); // HomePath's default
        var setCookie = Assert.Single(context.SetCookies);
        Assert.StartsWith("benzene_mesh_session=;", setCookie);
        Assert.Contains("Max-Age=0", setCookie);
    }

    /// <summary>
    /// Regression: logout used to redirect to a hardcoded <c>/</c>. On a host that serves nothing
    /// there - e.g. <c>examples/AwsMesh</c>, whose UI is at <c>/mesh-ui</c> - that produced a silent
    /// loop rather than an error: the gate bounced <c>/</c> to login, the provider re-authenticated
    /// the still-signed-in user, and the callback returned them to <c>/</c> again, rendering a bare
    /// not-found problem document with no indication they had been signed out (they had).
    /// </summary>
    [Fact]
    public async Task MatchingPath_RedirectsToTheConfiguredHomePath_NotAHardcodedRoot()
    {
        var options = Options();
        options.HomePath = "/mesh-ui";
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(options, new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext { Method = "GET", Path = "/mesh/auth/logout" };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(302, context.StatusCode);
        Assert.Equal("/mesh-ui", context.Location);
    }
}
