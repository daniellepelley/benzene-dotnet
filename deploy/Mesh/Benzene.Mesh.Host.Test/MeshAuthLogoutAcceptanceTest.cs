using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// WP-1(c) (work/bug-fix-designs-2026-08.md "WP-1", task #4)'s own regression test: boots the REAL
/// <see cref="Startup"/> on a real Kestrel-hosted pipeline, exactly like
/// <see cref="MeshAuthAcceptanceTest"/>/<see cref="MeshDispatchRoleAcceptanceTest"/>, and drives
/// <c>POST /mesh/auth/logout</c> against it.
/// </summary>
[Collection(EnvVarMutatingTestCollection.Name)]
public class MeshAuthLogoutAcceptanceTest
{
    private const string LogoutPath = "/mesh/auth/logout";
    private const string LogoutHeaderName = "X-Benzene-Logout";

    private sealed class TestHost : IAsyncDisposable
    {
        public required IHost Host { get; init; }
        public required HttpClient Client { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
            Client.Dispose();
        }
    }

    private static async Task<TestHost> StartHostAsync(
        string meshJson, (string Name, string Value)[]? envVars = null, string? endSessionEndpoint = null)
    {
        foreach (var (name, value) in envVars ?? Array.Empty<(string, string)>())
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"mesh-auth-logout-acceptance-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, meshJson);

        var host = global::Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) => config.AddJsonFile(configPath, optional: false))
            .ConfigureWebHost(webBuilder => webBuilder
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .UseEnvironment("Development")
                .UseStartup<Startup>()
                .ConfigureServices(services =>
                {
                    // See MeshAuthAcceptanceTest's remarks - avoids a real network call for discovery.
                    services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                    {
                        options.RequireHttpsMetadata = false;
                        options.Configuration = new OpenIdConnectConfiguration
                        {
                            Issuer = "https://idp.example.com",
                            AuthorizationEndpoint = "https://idp.example.com/authorize",
                            TokenEndpoint = "https://idp.example.com/token",
                            EndSessionEndpoint = endSessionEndpoint,
                        };
                    });
                }))
            .Build();

        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new TestHost { Host = host, Client = client };
    }

    private static string SignedInCookie(IHost host, string email)
    {
        var cookieOptions = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Email, email) }, CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new global::Microsoft.AspNetCore.Authentication.AuthenticationTicket(
            new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);
        var cookieValue = cookieOptions.TicketDataFormat.Protect(ticket);
        return $"{cookieOptions.Cookie.Name}={Uri.EscapeDataString(cookieValue)}";
    }

    private static string MeshJson() => """
        {
          "services": [],
          "auth": { "mode": "oidc", "oidc": { "authority": "https://idp.example.com", "clientId": "test-client" } }
        }
        """;

    private static (string Name, string Value)[] EnvVars() => new[] { ("MESH_OIDC_CLIENT_SECRET", "shh") };

    // --- GET is rejected (CSRF-forced logout) -------------------------------------------------------

    [Fact]
    public async Task Get_IsMethodNotAllowed()
    {
        await using var host = await StartHostAsync(MeshJson(), EnvVars());

        var response = await host.Client.GetAsync(LogoutPath);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // --- POST without the CSRF header is refused ------------------------------------------------

    [Fact]
    public async Task Post_MissingCsrfHeader_IsForbidden()
    {
        await using var host = await StartHostAsync(MeshJson(), EnvVars());
        var request = new HttpRequestMessage(HttpMethod.Post, LogoutPath);
        request.Headers.Add("Cookie", SignedInCookie(host.Host, "alice@example.com"));

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- POST with the CSRF header signs out and answers {"redirect": ...} -----------------------

    [Fact]
    public async Task Post_WithCsrfHeader_SignsOutAndClearsCookie()
    {
        await using var host = await StartHostAsync(MeshJson(), EnvVars());
        var request = new HttpRequestMessage(HttpMethod.Post, LogoutPath);
        request.Headers.Add("Cookie", SignedInCookie(host.Host, "alice@example.com"));
        request.Headers.Add(LogoutHeaderName, "1");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal) &&
                     value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Post_WithCsrfHeader_NoEndSessionEndpointDiscovered_RedirectIsNull()
    {
        await using var host = await StartHostAsync(MeshJson(), EnvVars(), endSessionEndpoint: null);
        var request = new HttpRequestMessage(HttpMethod.Post, LogoutPath);
        request.Headers.Add("Cookie", SignedInCookie(host.Host, "alice@example.com"));
        request.Headers.Add(LogoutHeaderName, "1");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("redirect").ValueKind);
    }

    [Fact]
    public async Task Post_WithCsrfHeader_EndSessionEndpointDiscovered_RedirectPointsAtIt()
    {
        await using var host = await StartHostAsync(
            MeshJson(), EnvVars(), endSessionEndpoint: "https://idp.example.com/logout");
        var request = new HttpRequestMessage(HttpMethod.Post, LogoutPath);
        request.Headers.Add("Cookie", SignedInCookie(host.Host, "alice@example.com"));
        request.Headers.Add(LogoutHeaderName, "1");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var redirect = json.RootElement.GetProperty("redirect").GetString();

        Assert.StartsWith("https://idp.example.com/logout", redirect);
        Assert.Contains("post_logout_redirect_uri=", redirect);
    }

    // --- non-oidc modes: the route is simply not there -------------------------------------------

    [Fact]
    public async Task NoneMode_Post_IsNotFound()
    {
        var meshJson = """
            {
              "services": [],
              "auth": { "mode": "none" }
            }
            """;
        await using var host = await StartHostAsync(meshJson);
        var request = new HttpRequestMessage(HttpMethod.Post, LogoutPath);
        request.Headers.Add(LogoutHeaderName, "1");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
