using System;
using System.Net.Http;
using System.Threading.Tasks;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Artifacts;
using Benzene.Microsoft.Dependencies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// End-to-end guard for the AzureMesh refresh endpoint's protection model (#21), exercised through a
/// real ASP.NET Core <see cref="TestServer"/> host rather than against
/// <c>MeshRefreshGuardMiddleware</c> in isolation (that middleware's own behaviour is pinned by
/// <see cref="MeshRefreshGuardMiddlewareTest"/>). Examples aren't in the CI gate, so - exactly as
/// <see cref="AwsMeshRefreshEndpointTest"/> does for the AwsMesh (API Gateway) example - this
/// library-side test stands in for <c>examples/AzureMesh/Mesh/Startup.cs</c>'s wiring.
/// <para>
/// Unlike AwsMesh, AzureMesh has no login gate in front of it (see its README's "Security posture"
/// section): <c>UseMeshRefreshGuard</c> is the ONLY thing standing between a public caller and a
/// discovery/aggregation pass on this example, so this test's job is narrowly to pin that the guard
/// is actually wired in front of <c>POST /mesh/refresh</c> - not to re-litigate the guard's own CSRF/
/// throttle behaviour, which <see cref="MeshRefreshGuardMiddlewareTest"/> already covers.
/// </para>
/// <para>
/// Reuses <see cref="AwsMeshRefreshEndpointTest.SpyAggregateHandler"/> rather than declaring a second
/// handler at the same <c>POST /mesh/refresh</c> route: handler discovery in Benzene is a single
/// process-wide union of every <c>AddMessageHandlers</c> call (see that type's own remarks), and
/// several tests in this assembly discover handlers by a whole-assembly scan - a second class at an
/// identical route would collide with theirs. The topic name is incidental to what this test checks
/// (that the guard sits in front of the handler), so sharing the double costs nothing - EXCEPT its
/// static <c>Invocations</c> counter, which this test deliberately never touches (xUnit can run this
/// class and <see cref="AwsMeshRefreshEndpointTest"/> concurrently, in different collections, so a
/// shared static counter would race); the response status code alone is sufficient evidence of
/// whether the guard let the request through.
/// </para>
/// </summary>
public class AzureMeshRefreshEndpointTest
{
    private sealed class StubArtifactStore : IMeshArtifactStore
    {
        private readonly string? _manifest;

        public StubArtifactStore(string? manifest) => _manifest = manifest;

        public Task PublishAsync(string relativePath, string content) => Task.CompletedTask;

        public Task<string?> TryReadAsync(string relativePath) => Task.FromResult(_manifest);
    }

    private static string ManifestGeneratedAt(DateTimeOffset at)
        => "{\"generatedAtUtc\":\"" + at.ToString("O") + "\",\"services\":[]}";

    /// <summary>
    /// Mirrors <c>examples/AzureMesh/Mesh/Startup.cs</c>'s <c>Configure</c>: the refresh guard runs
    /// directly in front of the message handlers, on the same pipeline, exactly as it does there.
    /// </summary>
    private static async Task<IHost> BuildHostAsync(string? manifest = null)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddSingleton<IMeshArtifactStore>(new StubArtifactStore(manifest));
                    services.UsingBenzene(x => x
                        .AddBenzene()
                        .AddMessageHandlers(new[] { typeof(AwsMeshRefreshEndpointTest.SpyAggregateHandler) }));
                });
                webHost.Configure(app => app.UseBenzene(asp => asp.UseHttp(http => http
                    .UseMeshRefreshGuard()
                    .UseMessageHandlers(typeof(AwsMeshRefreshEndpointTest.SpyAggregateHandler)))));
            });

        return await hostBuilder.StartAsync();
    }

    [Fact]
    public async Task PostRefresh_WithTheCustomHeader_RunsThePass()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/mesh/refresh");
        request.Headers.Add("X-Benzene-Refresh", "1");
        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// Regression for #21: before this fix, AzureMesh's <c>POST /mesh/refresh</c> had no guard at
    /// all, so a bare cross-site-forgeable POST (no custom header, exactly what a cross-site
    /// <c>&lt;form method="post"&gt;</c> produces) ran a real discovery+aggregation pass. With the
    /// guard wired, the same request must be refused and must run nothing.
    /// </summary>
    [Fact]
    public async Task PostRefresh_WithoutTheCustomHeader_IsForbiddenAndRunsNothing()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();

        var response = await client.PostAsync("/mesh/refresh", null);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostRefresh_InsideTheThrottleWindow_IsThrottledAndRunsNothing()
    {
        using var host = await BuildHostAsync(ManifestGeneratedAt(DateTimeOffset.UtcNow.AddSeconds(-2)));
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/mesh/refresh");
        request.Headers.Add("X-Benzene-Refresh", "1");
        var response = await client.SendAsync(request);

        Assert.Equal((System.Net.HttpStatusCode)429, response.StatusCode);
    }

    [Fact]
    public async Task PostRefresh_OutsideTheThrottleWindow_RunsThePass()
    {
        using var host = await BuildHostAsync(ManifestGeneratedAt(DateTimeOffset.UtcNow.AddHours(-1)));
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/mesh/refresh");
        request.Headers.Add("X-Benzene-Refresh", "1");
        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }
}
