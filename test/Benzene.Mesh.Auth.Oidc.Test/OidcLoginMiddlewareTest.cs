using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Benzene.Mesh.Auth.Oidc.Test.Fakes;
using Xunit;

namespace Benzene.Mesh.Auth.Oidc.Test;

public class OidcLoginMiddlewareTest
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    private static (OidcLoginMiddleware<FakeHttpContext> Middleware, FakeOidcProvider Provider) CreateMiddleware(string basePath = "/mesh/auth")
    {
        var provider = new FakeOidcProvider();
        provider.AddKey("kid1");

        var options = new MeshOidcOptions
        {
            Issuer = provider.Issuer,
            ClientId = "client-id",
            ClientSecret = "client-secret",
            SigningKey = Encoding.UTF8.GetString(Key),
            AllowedEmails = new[] { "user@example.com" },
            BasePath = basePath,
            RequireHttpsMetadata = false,
        };

        var configurationManager = OidcConfigurationManagerFactory.Create(options);
        var middleware = new OidcLoginMiddleware<FakeHttpContext>(
            options, Key, configurationManager, new FakeHttpRequestAdapter(), new FakeResponseAdapter(), new FakeQueryStringReader());

        return (middleware, provider);
    }

    [Fact]
    public async Task NonMatchingPath_CallsNext()
    {
        var (middleware, provider) = CreateMiddleware();
        using var _ = provider;
        var context = new FakeHttpContext { Method = "GET", Path = "/mesh-ui" };

        var nextCalled = false;
        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.Null(context.StatusCode);
    }

    [Fact]
    public async Task MatchingPath_RedirectsToAuthorizationEndpointWithExpectedParameters()
    {
        var (middleware, provider) = CreateMiddleware();
        using var _ = provider;
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh/auth/login",
            Headers = { ["host"] = "mesh.example.com" },
        };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal(302, context.StatusCode);
        var location = context.Location!;
        Assert.StartsWith(provider.AuthorizationEndpoint, location);
        Assert.Contains("client_id=client-id", location);
        Assert.Contains("response_type=code", location);
        Assert.Contains(Uri.EscapeDataString("https://mesh.example.com/mesh/auth/callback"), location);
        Assert.Contains("scope=" + Uri.EscapeDataString("openid email"), location);
        Assert.Contains("state=", location);
    }

    [Fact]
    public async Task MatchingPath_SetsStateCookie()
    {
        var (middleware, provider) = CreateMiddleware();
        using var _ = provider;
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh/auth/login",
            Headers = { ["host"] = "mesh.example.com" },
        };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        var setCookie = Assert.Single(context.SetCookies);
        Assert.StartsWith("benzene_mesh_oidc_state=", setCookie);
        Assert.Contains("HttpOnly", setCookie);
        Assert.Contains("Secure", setCookie);
        Assert.Contains("SameSite=Lax", setCookie);
        Assert.Contains("Path=/mesh/auth", setCookie);
    }

    [Fact]
    public async Task SafeReturnTo_IsEmbeddedInState()
    {
        var (middleware, provider) = CreateMiddleware();
        using var _ = provider;
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh/auth/login",
            Headers = { ["host"] = "mesh.example.com" },
            QueryParameters = { ["returnTo"] = "/mesh-ui" },
        };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        var setCookie = context.SetCookies.Single();
        var stateValue = setCookie.Split(';')[0].Split('=')[1];
        var ok = OidcStateToken.TryValidate(Key, stateValue, stateValue, out var returnTo);

        Assert.True(ok);
        Assert.Equal("/mesh-ui", returnTo);
    }

    [Fact]
    public async Task UnsafeReturnTo_FallsBackToRoot()
    {
        var (middleware, provider) = CreateMiddleware();
        using var _ = provider;
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh/auth/login",
            Headers = { ["host"] = "mesh.example.com" },
            QueryParameters = { ["returnTo"] = "https://evil.com" },
        };

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        var setCookie = context.SetCookies.Single();
        var stateValue = setCookie.Split(';')[0].Split('=')[1];
        OidcStateToken.TryValidate(Key, stateValue, stateValue, out var returnTo);

        Assert.Equal("/", returnTo);
    }

    /// <summary>
    /// #173 / round 1's #20: before this fix, an OIDC discovery failure at request time (a misconfigured
    /// or unreachable issuer, or a transient IdP outage) had nothing catching it here at all and
    /// surfaced as an unhandled 500. Points discovery at a genuinely unreachable loopback port (nothing
    /// listening) so <c>ConfigurationManager.GetConfigurationAsync()</c> actually fails, for real - not a
    /// mocked exception.
    /// </summary>
    [Fact]
    public async Task DiscoveryFailure_DeniesCleanly_NeverThrows()
    {
        var options = new MeshOidcOptions
        {
            Issuer = $"http://localhost:{GetUnreachablePort()}",
            ClientId = "client-id",
            ClientSecret = "client-secret",
            SigningKey = Encoding.UTF8.GetString(Key),
            AllowedEmails = new[] { "user@example.com" },
            BasePath = "/mesh/auth",
            RequireHttpsMetadata = false,
        };
        var configurationManager = OidcConfigurationManagerFactory.Create(options);
        var middleware = new OidcLoginMiddleware<FakeHttpContext>(
            options, Key, configurationManager, new FakeHttpRequestAdapter(), new FakeResponseAdapter(), new FakeQueryStringReader());
        var context = new FakeHttpContext
        {
            Method = "GET",
            Path = "/mesh/auth/login",
            Headers = { ["host"] = "mesh.example.com" },
        };

        var nextCalled = false;
        await middleware.HandleAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(503, context.StatusCode);
        Assert.Empty(context.SetCookies);
        Assert.DoesNotContain("client-secret", context.Body);
    }

    /// <summary>A closed TCP port on loopback - guaranteed nothing answers, so discovery fails fast
    /// with a real connection-refused error rather than a timeout.</summary>
    private static int GetUnreachablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
