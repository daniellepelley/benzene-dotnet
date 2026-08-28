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
/// Regression test for #34/#35 (work/archive/bug-fix-designs-round7-10-2026-08.md "WP-D"): boots the REAL
/// <see cref="Startup"/> on a real Kestrel-hosted pipeline - the same class <c>Program.cs</c> uses -
/// with <c>dispatch.enabled: true</c>, and proves the dispatch guard's 128 KiB payload cap actually
/// bounds a CHUNKED <c>Transfer-Encoding</c> request (no <c>Content-Length</c> header at all), not just
/// one that politely declares its size.
/// </summary>
/// <remarks>
/// Before the fix, <c>MeshDispatchGuardMiddleware</c>'s size check read <c>Content-Length</c> alone,
/// which is 0 ("absent") for a chunked request - so an oversized chunked body sailed straight past the
/// 128 KiB cap into the dispatch handler. <see cref="HttpClient"/> in .NET automatically sends a
/// request as chunked (no <c>Content-Length</c> header) when the content doesn't declare one up front,
/// which <see cref="StringContent"/> does NOT do here because <c>TransferEncodingChunked</c> is forced
/// explicitly below - the same shape a hand-rolled attacker client would send.
/// </remarks>
[Collection(EnvVarMutatingTestCollection.Name)]
public class MeshDispatchSizeGuardAcceptanceTest
{
    private const string DispatchPath = "/mesh/dispatch";
    private const string DispatchHeaderName = "X-Benzene-Dispatch";

    // Comfortably over the guard's 128 KiB (131,072 byte) default cap.
    private const int OversizedBodyBytes = 140_000;

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

    private static Task<TestHost> StartHostAsync() => StartHostAsync(maxRequestBytes: null);

    private static async Task<TestHost> StartHostAsync(int? maxRequestBytes)
    {
        // #247/#248: maxRequestBytes is null by default (today's behavior, unchanged) - a value here
        // lets a test prove mesh.json's dispatch.maxRequestBytes actually TIGHTENS the guard below its
        // 128 KiB compile-time default, not just that the default itself still works.
        var maxRequestBytesLine = maxRequestBytes.HasValue ? $"\"maxRequestBytes\": {maxRequestBytes.Value}," : string.Empty;
        var meshJson = $$"""
            {
              "services": [],
              "dispatch": { "enabled": true, {{maxRequestBytesLine}} "maxPerMinutePerTarget": 0, "maxPerMinutePerIdentity": 0 },
              "auth": {
                "mode": "proxy",
                "proxy": {
                  "userHeader": "X-Forwarded-User",
                  "trustedProxies": ["127.0.0.1"]
                }
              }
            }
            """;
        var configPath = Path.Combine(Path.GetTempPath(), $"mesh-dispatch-size-guard-{Guid.NewGuid():N}.json");
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
        // environment directly, not IWebHostEnvironment, so dispatch's Production self-refusal must be
        // headed off explicitly here too.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        // HTTP/1.1 explicitly: chunked Transfer-Encoding is an HTTP/1.1 wire concept - HttpClient
        // defaults to it against a Kestrel loopback endpoint, but pin it so this test can't silently
        // start exercising HTTP/2 (which frames bodies differently and wouldn't reproduce #35 at all).
        var client = new HttpClient { BaseAddress = new Uri(address), DefaultRequestVersion = HttpVersion.Version11 };
        return new TestHost { Host = host, Client = client };
    }

    private static HttpRequestMessage ChunkedDispatchRequest(int bodyBytes)
    {
        // A dispatch-shaped envelope padded with a large "body" field so the OVERALL request is
        // oversized - the guard bounds the whole request, not just a parsed field.
        var padding = new string('a', bodyBytes);
        var envelope = $$"""{"topic":"benzene:mesh:dispatch","headers":{},"body":"{\"service\":\"orders-api\",\"topic\":\"some:topic\",\"body\":\"{{padding}}\"}"}""";

        var request = new HttpRequestMessage(HttpMethod.Post, DispatchPath)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", "alice@example.com");
        request.Headers.TryAddWithoutValidation(DispatchHeaderName, "1");
        // Forces chunked Transfer-Encoding: HttpClient then sends NO Content-Length header at all -
        // exactly the shape ContentLength() used to read as 0 ("absent") rather than "oversized".
        request.Headers.TransferEncodingChunked = true;

        return request;
    }

    [Fact]
    public async Task ChunkedOversizedDispatchRequest_IsRefused_NotABypass()
    {
        await using var host = await StartHostAsync();
        var request = ChunkedDispatchRequest(OversizedBodyBytes);

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Before the fix this reached MeshDispatchMessageHandler (its own "No service named" business
        // response, or occasionally a framework-level 400/413 from something else entirely) rather than
        // the guard's own, clearly-labelled refusal - the guard's own threat model (bound the payload
        // before anything is parsed) was defeated. The guard's own envelope-shaped refusal is what
        // proves THIS check caught it, not some other layer.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("\"statusCode\":\"bad-request\"", body);
        Assert.DoesNotContain("No service named", body);
    }

    [Fact]
    public async Task ChunkedRequestWithinTheLimit_IsAllowedThrough_ReachingTheHandler()
    {
        await using var host = await StartHostAsync();
        var request = ChunkedDispatchRequest(100); // tiny - well under the 128 KiB cap

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Not the size guard's 413 - the request reached MeshDispatchMessageHandler, which reports its
        // own business outcome (no service named "orders-api" is registered; services: [] above).
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("No service named", body);
    }

    // --- #247/#248: dispatch.maxRequestBytes actually reaches this host's real guard, not just the
    // options object MeshSourceRegistrarTest exercises in isolation. ------------------------------

    [Fact]
    public async Task ConfiguredMaxRequestBytes_TightensTheGuard_BelowTheCompileTimeDefault()
    {
        // A body comfortably under the 128 KiB DEFAULT cap, but over a small CONFIGURED one - passes
        // today's default guard, must be refused once dispatch.maxRequestBytes is configured smaller.
        const int configuredMaxRequestBytes = 1_000;
        const int bodyBytes = 5_000;

        await using var host = await StartHostAsync(configuredMaxRequestBytes);
        var request = ChunkedDispatchRequest(bodyBytes);

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("\"statusCode\":\"bad-request\"", body);
        // MeshDispatchGuardMiddleware's own deny message formats the cap with "N0" (thousands
        // separators) - "1,000", not "1000" - confirming the CONFIGURED value (not the 128 KiB
        // default) is what the guard is actually enforcing.
        Assert.Contains(configuredMaxRequestBytes.ToString("N0"), body);
    }

    [Fact]
    public async Task ConfiguredMaxRequestBytes_StillAllowsARequestWithinTheConfiguredCap()
    {
        const int configuredMaxRequestBytes = 10_000;

        await using var host = await StartHostAsync(configuredMaxRequestBytes);
        var request = ChunkedDispatchRequest(100); // well under the configured cap

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("No service named", body);
    }
}
