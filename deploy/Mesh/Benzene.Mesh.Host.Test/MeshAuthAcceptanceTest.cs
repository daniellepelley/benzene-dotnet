using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// The slice's acceptance test (work/enterprise/slice-2-auth.md task 2.7): boots the REAL
/// <see cref="Startup"/> (the same class <c>Program.cs</c> uses, via <c>UseStartup&lt;Startup&gt;</c> on
/// a real Kestrel-hosted pipeline - not a hand-reconstructed one) and proves an unauthenticated request
/// to every one of the five surfaces slice-2-auth.md names is refused in every non-<c>none</c> mode, and
/// that <c>mode: "none"</c> leaves all of them open.
/// </summary>
/// <remarks>
/// Run against BOTH artifact-store branches - <c>artifactStore.type: "file"</c> (paths under
/// <c>/artifacts/</c>, served by <c>UseStaticFiles</c> entirely outside the Benzene pipeline) and
/// <c>"s3"</c> (root-relative paths, served by <c>Benzene.Mesh.Artifacts.UseMeshArtifacts()</c> inside
/// it) - per the brief: "already inside the pipeline" is exactly the kind of assumption this test exists
/// to stop taking on faith. The s3 store needs no real AWS credentials for the BLOCKED assertions
/// (<see cref="MeshAuthGate"/> short-circuits before the request ever reaches <c>UseMeshArtifacts()</c>'s
/// S3 client); see <c>AwsMeshParityTest</c>'s remarks for why constructing (but not resolving/using) an
/// <c>AmazonS3Client</c> needs no live credentials either way.
///
/// Manually verified while writing this test (the "Done when" bar 2.7 sets): commenting out
/// <c>Startup.Configure</c>'s <c>app.UseMiddleware&lt;MeshAuthGate&gt;(...)</c> line turns every
/// "blocked" assertion below into a failure (404/500 instead of 401), for both artifact-store branches -
/// proving this test is actually pinned to the gate's placement, not merely to *a* 401 from somewhere else.
/// </remarks>
public class MeshAuthAcceptanceTest
{
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string[] BlockedPaths(bool fileStore) => fileStore
        ? new[] { "/mesh-ui", "/mesh-spec-ui.html", "/artifacts/manifest.json", "/artifacts/services/orders-api.json" }
        : new[] { "/mesh-ui", "/mesh-spec-ui.html", "manifest.json", "services/orders-api.json" };

    private sealed class TestHost : IAsyncDisposable
    {
        public required IHost Host { get; init; }
        public required HttpClient Client { get; init; }
        public IReadOnlyDictionary<string, string?> PreviousEnv { get; init; } = new Dictionary<string, string?>();

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
            Client.Dispose();
            foreach (var (name, value) in PreviousEnv)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static async Task<TestHost> StartHostAsync(string meshJson, (string Name, string Value)[]? envVars = null, bool fakeOidcAuthority = false, bool allowAutoRedirect = true)
    {
        var previous = new Dictionary<string, string?>();
        foreach (var (name, value) in envVars ?? Array.Empty<(string, string)>())
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"mesh-auth-acceptance-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, meshJson);
        var port = GetFreeTcpPort();

        var host = global::Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) => config.AddJsonFile(configPath, optional: false))
            .ConfigureWebHost(webBuilder => webBuilder
                .UseKestrel()
                .UseUrls($"http://127.0.0.1:{port}")
                .UseEnvironment("Development")
                .UseStartup<Startup>()
                .ConfigureServices(services =>
                {
                    if (fakeOidcAuthority)
                    {
                        // Avoids a real network call to fetch the discovery document. Must be
                        // Configure, not PostConfigure: OpenIdConnectPostConfigureOptions (registered
                        // by AddOpenIdConnect itself) is what wraps Options.Configuration in a
                        // StaticConfigurationManager instead of building a network-fetching
                        // ConfigurationManager from Options.Authority - but only if Configuration is
                        // already set by the time IT runs, and every Configure delegate runs before
                        // every PostConfigure delegate regardless of registration order. The standard
                        // way to exercise AddOpenIdConnect's challenge/redirect behaviour offline; token
                        // validation itself is untouched Microsoft-owned code, not what this pins down.
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

        await host.StartAsync();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        };
        return new TestHost { Host = host, Client = client, PreviousEnv = previous };
    }

    private static string MeshJson(bool fileStore, string authJson)
    {
        var artifactStore = fileStore
            ? string.Empty
            : "\"artifactStore\": { \"type\": \"s3\", \"options\": { \"bucket\": \"acceptance-test-bucket\" } },";
        return $$"""
            {
              "services": [],
              {{artifactStore}}
              "auth": {{authJson}}
            }
            """;
    }

    // --- mode "none" leaves everything open (the file-store branch; s3's open case needs real AWS
    // credentials to serve real content and isn't the point of this test - the blocked assertions
    // below are) --------------------------------------------------------------------------------

    [Fact]
    public async Task NoneMode_FileStore_EveryPathIsNotBlockedByAuth()
    {
        await using var host = await StartHostAsync(MeshJson(fileStore: true, """{ "mode": "none" }"""));

        foreach (var path in BlockedPaths(fileStore: true))
        {
            var response = await host.Client.GetAsync(path);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // --- mode "proxy" (task 2.2) -----------------------------------------------------------------

    [Fact]
    public async Task ProxyMode_FileStore_UnauthenticatedRequestToEveryPathIsUnauthorized()
    {
        var auth = """{ "mode": "proxy", "proxy": { "userHeader": "X-Forwarded-User", "trustedProxies": ["127.0.0.1"] } }""";
        await using var host = await StartHostAsync(MeshJson(fileStore: true, auth));

        foreach (var path in BlockedPaths(fileStore: true))
        {
            var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task ProxyMode_S3Store_UnauthenticatedRequestToEveryPathIsUnauthorized()
    {
        // The trap task 2.7 exists to catch: with the non-file store, /artifacts is already served
        // from inside the Benzene pipeline (UseMeshArtifacts()) - proving the gate still blocks it
        // is what stops "already inside the pipeline" being taken on faith.
        var auth = """{ "mode": "proxy", "proxy": { "userHeader": "X-Forwarded-User", "trustedProxies": ["127.0.0.1"] } }""";
        // Real Kestrel host startup constructs MeshPollBackgroundService (an IHostedService), which
        // transitively needs an IAmazonS3 client to exist - AWS_REGION lets it CONSTRUCT (no real AWS
        // call happens either way, since MeshAuthGate short-circuits every blocked request below
        // before it ever reaches UseMeshArtifacts()). See AwsMeshParityTest's remarks for the same
        // "construction needs no credentials, only resolution risks a real client" distinction.
        await using var host = await StartHostAsync(MeshJson(fileStore: false, auth), new[] { ("AWS_REGION", "us-east-1") });

        foreach (var path in BlockedPaths(fileStore: false))
        {
            var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task ProxyMode_TrustedPeerWithHeader_MeshUiIsServed()
    {
        var auth = """{ "mode": "proxy", "proxy": { "userHeader": "X-Forwarded-User", "trustedProxies": ["127.0.0.1"] } }""";
        await using var host = await StartHostAsync(MeshJson(fileStore: true, auth));
        host.Client.DefaultRequestHeaders.Add("X-Forwarded-User", "alice@example.com");

        var response = await host.Client.GetAsync("/mesh-ui");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- mode "basic" (task 2.3) -----------------------------------------------------------------

    [Fact]
    public async Task BasicMode_FileStore_UnauthenticatedRequestToEveryPathIsUnauthorized()
    {
        await using var host = await StartHostAsync(
            MeshJson(fileStore: true, """{ "mode": "basic" }"""),
            new[] { ("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret") });

        foreach (var path in BlockedPaths(fileStore: true))
        {
            var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.Contains("WWW-Authenticate"));
        }
    }

    [Fact]
    public async Task BasicMode_S3Store_UnauthenticatedRequestToEveryPathIsUnauthorized()
    {
        await using var host = await StartHostAsync(
            MeshJson(fileStore: false, """{ "mode": "basic" }"""),
            new[] { ("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret"), ("AWS_REGION", "us-east-1") });

        foreach (var path in BlockedPaths(fileStore: false))
        {
            var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task BasicMode_CorrectCredentials_MeshUiIsServed()
    {
        await using var host = await StartHostAsync(
            MeshJson(fileStore: true, """{ "mode": "basic" }"""),
            new[] { ("MESH_BASIC_USER", "admin"), ("MESH_BASIC_PASSWORD", "s3cret") });
        host.Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:s3cret")));

        var response = await host.Client.GetAsync("/mesh-ui");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- mode "oidc" (task 2.4) - refused means redirected to the authority, not a 401 --------------

    [Fact]
    public async Task OidcMode_FileStore_UnauthenticatedRequestToEveryPathRedirectsToAuthority()
    {
        var auth = """{ "mode": "oidc", "oidc": { "authority": "https://idp.example.com", "clientId": "test-client" } }""";
        await using var host = await StartHostAsync(
            MeshJson(fileStore: true, auth),
            new[] { ("MESH_OIDC_CLIENT_SECRET", "shh") },
            fakeOidcAuthority: true,
            allowAutoRedirect: false);

        foreach (var path in BlockedPaths(fileStore: true))
        {
            var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("idp.example.com", response.Headers.Location?.ToString());
        }
    }
}
