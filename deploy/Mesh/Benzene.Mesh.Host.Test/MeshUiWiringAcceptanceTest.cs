using System.Security.Claims;
using System.Text.RegularExpressions;
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
/// WP-1(c)/(d) (work/bug-fix-designs-2026-08.md "WP-1", tasks #4/#5): proves <c>Startup.Configure</c>
/// actually passes <c>dispatchUrl</c>/<c>logoutUrl</c> into <c>UseMeshUi(...)</c> - the plumbing those
/// parameters exist for having landed already (<c>Benzene.Mesh.Ui.MeshUiExtensions</c>/
/// <c>MeshUiMiddleware</c>/<c>MeshUiPage</c>, covered by <c>MeshUiPageTest</c>), but nothing in this
/// host called them until this fix. Boots the REAL <see cref="Startup"/> on a real Kestrel-hosted
/// pipeline and reads the rendered <c>/mesh-ui</c> HTML back, exactly like
/// <see cref="MeshDispatchRoleAcceptanceTest"/>/<see cref="MeshAuthAcceptanceTest"/> - the only way to
/// prove the wiring reaches the page a browser actually gets, not merely that the extension method
/// accepts the parameter.
/// </summary>
[Collection(EnvVarMutatingTestCollection.Name)]
public class MeshUiWiringAcceptanceTest
{
    // The JS bundle references its own data-* attribute names as string literals (to read them back
    // off the document root at runtime), so a plain substring check against the whole page would find
    // "data-dispatch-url"/"data-logout-url" regardless of whether the attribute was actually injected -
    // scope every negative assertion to just the <html ...> opening tag, matching
    // test/Benzene.Mesh.Test/MeshUiPageTest.cs's own HtmlTag helper.
    private static string HtmlTag(string html) => Regex.Match(html, "<html[^>]*>").Value;

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

    private static async Task<TestHost> StartHostAsync(string meshJson, (string Name, string Value)[]? envVars = null, bool fakeOidcAuthority = false)
    {
        foreach (var (name, value) in envVars ?? Array.Empty<(string, string)>())
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"mesh-ui-wiring-acceptance-{Guid.NewGuid():N}.json");
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
                    if (fakeOidcAuthority)
                    {
                        // See MeshAuthAcceptanceTest's remarks - avoids a real network call.
                        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                        {
                            options.RequireHttpsMetadata = false;
                            options.Configuration = new OpenIdConnectConfiguration
                            {
                                Issuer = "https://idp.example.com",
                                AuthorizationEndpoint = "https://idp.example.com/authorize",
                                TokenEndpoint = "https://idp.example.com/token",
                            };
                        });
                    }
                }))
            .Build();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new TestHost { Host = host, Client = client };
    }

    // --- (d) #5: dispatchUrl -----------------------------------------------------------------------

    [Fact]
    public async Task DispatchEnabled_MeshUiPage_CarriesDispatchUrl()
    {
        var meshJson = """
            {
              "services": [],
              "dispatch": { "enabled": true, "allowInProduction": true },
              "auth": {
                "mode": "proxy",
                "proxy": { "userHeader": "X-Forwarded-User", "trustedProxies": ["127.0.0.1"] }
              }
            }
            """;
        await using var host = await StartHostAsync(meshJson);
        host.Client.DefaultRequestHeaders.Add("X-Forwarded-User", "alice@example.com");

        var html = await host.Client.GetStringAsync("/mesh-ui");

        Assert.Contains("data-dispatch-url=\"/benzene/invoke\"", HtmlTag(html));
    }

    [Fact]
    public async Task DispatchDisabled_MeshUiPage_DoesNotCarryDispatchUrl()
    {
        var meshJson = """
            {
              "services": [],
              "auth": { "mode": "none" }
            }
            """;
        await using var host = await StartHostAsync(meshJson);

        var html = await host.Client.GetStringAsync("/mesh-ui");

        Assert.DoesNotContain("data-dispatch-url", HtmlTag(html));
    }

    // --- (c) #4: logoutUrl (oidc mode only) --------------------------------------------------------

    [Fact]
    public async Task OidcMode_MeshUiPage_CarriesLogoutUrl()
    {
        var meshJson = """
            {
              "services": [],
              "auth": { "mode": "oidc", "oidc": { "authority": "https://idp.example.com", "clientId": "test-client" } }
            }
            """;
        await using var host = await StartHostAsync(
            meshJson, new[] { ("MESH_OIDC_CLIENT_SECRET", "shh") }, fakeOidcAuthority: true);

        // /mesh-ui is served through MeshAuthGate's oidc branch (see MeshAuthAcceptanceTest), so a
        // request needs a valid signed-in session cookie to reach the page at all - forge one the same
        // way the cookie handler itself would, using its own TicketDataFormat, so this proves the
        // wiring against a genuinely authenticated request rather than sidestepping auth.
        var request = new HttpRequestMessage(HttpMethod.Get, "/mesh-ui");
        request.Headers.Add("Cookie", await SignedInCookieAsync(host.Host, "alice@example.com"));

        var html = await (await host.Client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("data-logout-url=\"/mesh/auth/logout\"", HtmlTag(html));
    }

    /// <summary>
    /// Builds a <c>Cookie</c> header value for an already-authenticated session, using the SAME
    /// <see cref="CookieAuthenticationOptions.TicketDataFormat"/> and cookie name the running host's
    /// own cookie handler would - the standard way to drive a cookie-authenticated request in a test
    /// without performing a real interactive login round-trip against an IdP.
    /// </summary>
    private static async Task<string> SignedInCookieAsync(IHost host, string email)
    {
        var cookieOptions = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Email, email) }, CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new global::Microsoft.AspNetCore.Authentication.AuthenticationTicket(
            new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);
        var cookieValue = cookieOptions.TicketDataFormat.Protect(ticket);
        await Task.CompletedTask;
        return $"{cookieOptions.Cookie.Name}={Uri.EscapeDataString(cookieValue)}";
    }

    [Fact]
    public async Task NonOidcMode_MeshUiPage_DoesNotCarryLogoutUrl()
    {
        var meshJson = """
            {
              "services": [],
              "auth": { "mode": "none" }
            }
            """;
        await using var host = await StartHostAsync(meshJson);

        var html = await host.Client.GetStringAsync("/mesh-ui");

        Assert.DoesNotContain("data-logout-url", HtmlTag(html));
    }
}
