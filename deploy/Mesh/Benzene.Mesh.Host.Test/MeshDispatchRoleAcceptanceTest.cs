using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// The P0 fix's own regression test (work/enterprise/slice-2-auth.md task 2.5, corrected
/// 2026-08-22): boots the REAL <see cref="Startup"/> on a real Kestrel-hosted pipeline - the same
/// class <c>Program.cs</c> uses - with <c>dispatch.enabled: true</c> and <c>auth.dispatchRole</c>
/// configured, and proves all three things the prior review found missing:
/// <list type="number">
/// <item><description>a principal without the configured role is refused (403) before <c>MeshDispatchMessageHandler</c> runs;</description></item>
/// <item><description>a principal with the role gets past that check (proven by reaching the handler's own business response, not a role refusal);</description></item>
/// <item><description>the dispatch guard - CSRF/identity/rate-limit - is actually wired into THIS host's pipeline, not merely present as unused code in <c>Benzene.Mesh.Artifacts</c>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Auth mode <c>proxy</c> drives this rather than <c>basic</c>/<c>oidc</c>: it is the one mode whose
/// identity can carry a caller-chosen role per request (via the <c>groupsHeader</c> this fix also
/// added to <see cref="MeshAuthProxyConfig"/>, mirroring oauth2-proxy's own <c>X-Forwarded-Groups</c>
/// convention), which is what lets one test host, exercised twice, prove both the "missing role" and
/// "has role" outcomes. <c>services: []</c> deliberately - a role-holding caller's dispatch request
/// still resolves no service to invoke, so the "past the auth check" assertion is the handler's own
/// <c>NotFound</c> business response (proof the request reached <c>MeshDispatchMessageHandler</c>),
/// never a network call to a real target.
/// </remarks>
public class MeshDispatchRoleAcceptanceTest
{
    private const string DispatchPath = "/mesh/dispatch";
    private const string DispatchTopic = "benzene:mesh:dispatch";
    private const string DispatchHeaderName = "X-Benzene-Dispatch";

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

    private static async Task<TestHost> StartHostAsync(string meshJson)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"mesh-dispatch-role-acceptance-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, meshJson);

        var host = global::Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) => config.AddJsonFile(configPath, optional: false))
            .ConfigureWebHost(webBuilder => webBuilder
                .UseKestrel()
                // Port 0 - see MeshAuthAcceptanceTest's remarks for why this (not a pre-selected port).
                .UseUrls("http://127.0.0.1:0")
                .UseEnvironment("Development")
                .UseStartup<Startup>())
            .Build();

        // MeshDispatchGate reads ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT straight off the process
        // environment (see EnvironmentVariableMeshDispatchEnvironment), not IWebHostEnvironment - so
        // UseEnvironment("Development") above governs ASP.NET's own environment, but not this. Set
        // explicitly so dispatch's own Production self-refusal never fires here.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new TestHost { Host = host, Client = client };
    }

    private static string MeshJson(string? dispatchRole)
    {
        var dispatchRoleLine = dispatchRole == null ? string.Empty : $"\"dispatchRole\": \"{dispatchRole}\",";
        return $$"""
            {
              "services": [],
              "dispatch": { "enabled": true },
              "auth": {
                "mode": "proxy",
                {{dispatchRoleLine}}
                "proxy": {
                  "userHeader": "X-Forwarded-User",
                  "groupsHeader": "X-Forwarded-Groups",
                  "trustedProxies": ["127.0.0.1"]
                }
              }
            }
            """;
    }

    private static HttpRequestMessage DispatchRequest(bool includeCsrfHeader = true)
    {
        var envelope = """{"topic":"benzene:mesh:dispatch","headers":{},"body":"{\"service\":\"orders-api\",\"topic\":\"some:topic\",\"body\":\"{}\"}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, DispatchPath)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", "alice@example.com");
        if (includeCsrfHeader)
        {
            request.Headers.TryAddWithoutValidation(DispatchHeaderName, "1");
        }

        return request;
    }

    // --- (a) missing the configured role: rejected before the handler runs -----------------------

    [Fact]
    public async Task DispatchRoleConfigured_CallerWithoutRole_IsForbiddenBeforeHandlerRuns()
    {
        await using var host = await StartHostAsync(MeshJson(dispatchRole: "mesh-admins"));
        var request = DispatchRequest();
        // No X-Forwarded-Groups header at all - alice is authenticated but holds no role/group.

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The role check's own detail (AuthorizationExtensions.RequireRole), not the guard's bare
        // {"error":"forbidden"} - proves THIS refusal is the role gate, not the CSRF/identity guard
        // (that distinction is what the next two tests pin down).
        Assert.Contains("mesh-admins", body);
        Assert.DoesNotContain("No service named", body);
    }

    // --- (b) holding the configured role: gets past the auth check -----------------------------

    [Fact]
    public async Task DispatchRoleConfigured_CallerWithRole_PassesAuthCheckAndReachesHandler()
    {
        await using var host = await StartHostAsync(MeshJson(dispatchRole: "mesh-admins"));
        var request = DispatchRequest();
        request.Headers.TryAddWithoutValidation("X-Forwarded-Groups", "mesh-admins");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Not the role refusal (403) - the request reached MeshDispatchMessageHandler, which then
        // reports its own, entirely different outcome: no service named "orders-api" is registered
        // (services: [] above). That business-level NotFound is the proof of "past the auth check" -
        // a network call to a real target is neither needed nor wanted for this assertion.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("No service named", body);
    }

    // --- (c) the guard itself is wired into this host, not just present as unused code -----------

    [Fact]
    public async Task DispatchGuard_IsWiredIntoThisHost_MissingCsrfHeaderIsRefused()
    {
        // No dispatchRole configured here - isolates this assertion to the guard alone, so a failure
        // can never be misread as the role check above.
        await using var host = await StartHostAsync(MeshJson(dispatchRole: null));
        var request = DispatchRequest(includeCsrfHeader: false);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Groups", "mesh-admins");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // MeshDispatchGuardMiddleware.DenyAsync's exact, fixed body - only the guard writes this
        // shape; AuthorizationExtensions.RequireRole's refusal (test above) reads completely
        // differently. This is what proves the guard is mounted in front of the dispatch envelope in
        // THIS host's Configure(), not merely available for a host to opt into.
        Assert.Equal("{\"error\":\"forbidden\"}", body);
    }

    // --- dispatch is reachable at all now (the pre-existing "no HTTP path reaches it" gap) --------

    [Fact]
    public async Task DispatchTopic_WithNoRoleConfigured_ReachesHandlerPastGuard()
    {
        await using var host = await StartHostAsync(MeshJson(dispatchRole: null));
        var request = DispatchRequest();

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // The handler's own business response (no service named "orders-api" is registered), not a
        // guard refusal or an unmatched route - proof the envelope this fix adds actually reaches
        // MeshDispatchMessageHandler over HTTP, closing the pre-existing "no HTTP path reaches
        // mesh:dispatch" gap the STOPPED HERE comment (removed by this fix) used to document.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("No service named", body);
    }
}
