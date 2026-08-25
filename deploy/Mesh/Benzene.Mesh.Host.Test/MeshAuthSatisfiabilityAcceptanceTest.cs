using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// WP-1(a) (work/bug-fix-designs-2026-08.md "WP-1", task #19)'s own regression test. Round 6's live
/// reproduction: with <c>dispatch.enabled: true</c> and <c>auth.mode: "none"</c>, the host booted
/// cleanly and then <c>POST /mesh/dispatch</c> came back <c>403 Forbidden</c> on every single request -
/// even one carrying a perfectly valid CSRF header - because mode <c>"none"</c> never establishes a
/// caller identity and <c>MeshDispatchGate</c>'s own identity check is fail-closed. Nothing told the
/// operator why; the host looked healthy the whole time.
/// </summary>
/// <remarks>
/// This boots the REAL <see cref="Startup"/> on a real Kestrel-hosted pipeline (the same class
/// <c>Program.cs</c> uses), exactly like <see cref="MeshDispatchRoleAcceptanceTest"/> and
/// <see cref="MeshAuthAcceptanceTest"/> - proving the fix at the level the bug was actually observed
/// at, not just against <see cref="MeshAuthGate.Validate"/> in isolation (which
/// <see cref="MeshAuthGateTest"/> already covers). After the fix, this exact configuration never
/// reaches a live, permanently-403'd dispatch endpoint at all: <c>host.StartAsync()</c> itself throws,
/// naming <c>dispatch.enabled</c> and <c>auth.mode</c>, so the misconfiguration is caught before a
/// single request is ever served.
/// </remarks>
public class MeshAuthSatisfiabilityAcceptanceTest
{
    private static async Task<string> WriteConfigAsync(string meshJson)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"mesh-auth-satisfiability-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, meshJson);
        return configPath;
    }

    private static IHost BuildHost(string configPath) =>
        global::Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) => config.AddJsonFile(configPath, optional: false))
            .ConfigureWebHost(webBuilder => webBuilder
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .UseEnvironment("Development")
                .UseStartup<Startup>())
            .Build();

    [Fact]
    public async Task DispatchEnabledWithAuthModeNone_HostFailsToStart_InsteadOfBootingAndPermanently403ingDispatch()
    {
        var configPath = await WriteConfigAsync("""
            {
              "services": [],
              "dispatch": { "enabled": true },
              "auth": { "mode": "none" }
            }
            """);

        using var host = BuildHost(configPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("dispatch.enabled", exception.Message);
        Assert.Contains("none", exception.Message);
    }

    [Fact]
    public async Task DispatchEnabledWithAuthModeProxy_HostStartsFine()
    {
        // Sibling positive case: the same shape, but a mode that establishes an identity - proves the
        // rejection above is specific to "none", not dispatch.enabled in general.
        var configPath = await WriteConfigAsync("""
            {
              "services": [],
              "dispatch": { "enabled": true },
              "auth": {
                "mode": "proxy",
                "proxy": { "userHeader": "X-Forwarded-User", "trustedProxies": ["127.0.0.1"] }
              }
            }
            """);

        using var host = BuildHost(configPath);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.Null(exception);
        await host.StopAsync();
    }
}
