using System.Linq;
using System.Net;
using System.Net.Sockets;
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
/// #247/#248's own regression test: boots the REAL <see cref="Startup"/> on a real Kestrel-hosted
/// pipeline - the same class <c>Program.cs</c> uses - with <c>dispatch.enabled: true</c> and a
/// configured <c>dispatch.maxResponseBytes</c>, and dispatches to a REAL loopback target service that
/// answers with an oversized body, proving the CONFIGURED cap is what actually bounds the response -
/// not <c>Benzene.Mesh.Dispatch.HttpMeshServiceDispatcher.DefaultMaxResponseBytes</c>, which
/// <see cref="Startup"/>'s wiring comment documents as the value <c>UseMeshDispatch</c> would otherwise
/// always use with no way to override it.
/// </summary>
/// <remarks>
/// The target's body (20,000 'x' bytes) is deliberately UNDER the built-in default cap (131,072) but
/// far OVER the configured one (500) used here - so if <see cref="Startup"/>'s shadow-registered
/// <c>HttpMeshServiceDispatcher</c> (see its own remarks on why it registers one ahead of
/// <c>UseMeshDispatch</c>) were silently losing to the default-cap instance
/// <c>UseMeshDispatch</c> registers internally, this test would see the FULL, untruncated 20,000-byte
/// body instead of a ~500-byte truncated one - a false pass is not possible by accident here.
/// </remarks>
public class MeshDispatchResponseCapAcceptanceTest
{
    private const string DispatchPath = "/mesh/dispatch";
    private const string DispatchHeaderName = "X-Benzene-Dispatch";
    private const int TargetBodyBytes = 20_000; // comfortably under HttpMeshServiceDispatcher.DefaultMaxResponseBytes (131,072)
    private const int ConfiguredMaxResponseBytes = 500; // comfortably under TargetBodyBytes

    /// <summary>A minimal loopback HTTP target standing in for a real mesh service's <c>/benzene-message</c> endpoint.</summary>
    private sealed class FakeTargetService : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public string InvokeUrl { get; }

        public FakeTargetService()
        {
            var port = GetFreeTcpPort();
            // "localhost", not "127.0.0.1" - matching FakeJwksServer/FakeOidcProvider's established
            // HttpListener prefix precedent in this repo's test suite.
            InvokeUrl = $"http://localhost:{port}/benzene-message";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    // Every request gets the same oversized body, regardless of path/method - this fake
                    // exists only to be dispatched to; MeshPollBackgroundService's own spec/health fetches
                    // (unavoidable once a real service is registered) hitting it too is harmless noise -
                    // a failed poll pass is logged and swallowed (see MeshPollBackgroundService's remarks).
                    var bytes = Encoding.UTF8.GetBytes(new string('x', TargetBodyBytes));
                    context.Response.ContentType = "text/plain";
                    context.Response.StatusCode = 200;
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    context.Response.OutputStream.Close();
                }
                catch (Exception)
                {
                    // Best-effort - the client-side call observes the failure either way.
                }
            }
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
    }

    private sealed class TestHost : IAsyncDisposable
    {
        public required IHost Host { get; init; }
        public required HttpClient Client { get; init; }
        public required FakeTargetService Target { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
            Client.Dispose();
            Target.Dispose();
        }
    }

    private static async Task<TestHost> StartHostAsync(int? maxResponseBytes)
    {
        var target = new FakeTargetService();
        var maxResponseBytesLine = maxResponseBytes.HasValue ? $"\"maxResponseBytes\": {maxResponseBytes.Value}," : string.Empty;
        var meshJson = $$"""
            {
              "services": [
                { "name": "orders-api", "specUrl": "{{target.InvokeUrl}}", "sourceOptions": { "invokeUrl": "{{target.InvokeUrl}}" } }
              ],
              "dispatch": { "enabled": true, {{maxResponseBytesLine}} "maxPerMinutePerTarget": 0, "maxPerMinutePerIdentity": 0 },
              "auth": {
                "mode": "proxy",
                "proxy": { "userHeader": "X-Forwarded-User", "trustedProxies": ["127.0.0.1"] }
              }
            }
            """;
        var configPath = Path.Combine(Path.GetTempPath(), $"mesh-dispatch-response-cap-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, meshJson);

        var host = global::Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) => config.AddJsonFile(configPath, optional: false))
            .ConfigureWebHost(webBuilder => webBuilder
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .UseEnvironment("Development")
                .UseStartup<Startup>())
            .Build();

        // Same reason MeshDispatchRoleAcceptanceTest sets this: MeshDispatchGate reads the process
        // environment directly, not IWebHostEnvironment.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new TestHost { Host = host, Client = client, Target = target };
    }

    private static HttpRequestMessage DispatchRequest()
    {
        var envelope = """{"topic":"benzene:mesh:dispatch","headers":{},"body":"{\"service\":\"orders-api\",\"topic\":\"some:topic\",\"body\":\"{}\"}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, DispatchPath)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", "alice@example.com");
        request.Headers.TryAddWithoutValidation(DispatchHeaderName, "1");
        return request;
    }

    [Fact]
    public async Task ConfiguredMaxResponseBytes_TruncatesTheTargetsResponse_AtTheConfiguredCap_NotTheDefault()
    {
        await using var host = await StartHostAsync(ConfiguredMaxResponseBytes);

        var response = await host.Client.SendAsync(DispatchRequest());
        var body = await response.Content.ReadAsStringAsync();

        // Proves the request actually reached the dispatcher against the real target (not a guard
        // refusal or a "no service" business error) before asserting anything about truncation.
        Assert.DoesNotContain("No service named", body);

        var xCount = body.Count(c => c == 'x');
        Assert.True(xCount <= ConfiguredMaxResponseBytes,
            $"expected at most {ConfiguredMaxResponseBytes} 'x' bytes (the CONFIGURED cap), got {xCount} - " +
            "the shadow-registered dispatcher did not win, or the cap did not apply.");
        Assert.True(xCount < TargetBodyBytes, "the target's full body reached the caller untruncated.");
        Assert.Contains("response truncated", body);
    }

    [Fact]
    public async Task NoMaxResponseBytesConfigured_UsesTheBuiltInDefault_TargetBodyPassesThroughUntruncated()
    {
        // Regression pin for the wiring itself: with no config override, behavior must be UNCHANGED
        // from before #247/#248 - the target's 20,000-byte body is well under the 131,072-byte default,
        // so it must reach the caller whole, not truncated by some stray leftover cap.
        await using var host = await StartHostAsync(maxResponseBytes: null);

        var response = await host.Client.SendAsync(DispatchRequest());
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("No service named", body);
        Assert.Equal(TargetBodyBytes, body.Count(c => c == 'x'));
        Assert.DoesNotContain("response truncated", body);
    }
}
