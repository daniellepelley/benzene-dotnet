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
    public async Task MatchingPath_ClearsSessionCookieAndRedirectsToRoot()
    {
        var middleware = new OidcLogoutMiddleware<FakeHttpContext>(Options(), new FakeHttpRequestAdapter(), new FakeResponseAdapter());
        var context = new FakeHttpContext { Method = "GET", Path = "/mesh/auth/logout" };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(302, context.StatusCode);
        Assert.Equal("/", context.Location);
        var setCookie = Assert.Single(context.SetCookies);
        Assert.StartsWith("benzene_mesh_session=;", setCookie);
        Assert.Contains("Max-Age=0", setCookie);
    }
}
