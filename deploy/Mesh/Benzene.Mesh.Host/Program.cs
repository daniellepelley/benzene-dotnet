using Benzene.Mesh.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// benzene-mesh --validate-config: binds and validates mesh.json without starting the host - the only
// way to test a config change without deploying it. Reuses MeshConfigValidator, which itself reuses
// Startup's own registration rules (MeshSourceRegistrar) - the two paths cannot silently disagree
// about what a config means because there is only one set of rules.
if (args.Contains("--validate-config"))
{
    return ValidateConfig(Environment.GetEnvironmentVariable("MESH_CONFIG_PATH"));
}

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        // MESH_CONFIG_PATH points at a bind-mounted mesh.json (see deploy/Mesh/README.md) - the
        // primary config path for a multi-service registry. Individual scalars (ArtifactRootDirectory,
        // PollIntervalSeconds) can also be overridden via plain environment variables, since
        // Host.CreateDefaultBuilder already adds those; only the JSON file needs wiring explicitly here.
        MeshConfigLoader.ConfigureMeshConfig(config, Environment.GetEnvironmentVariable("MESH_CONFIG_PATH"));
    })
    .ConfigureWebHost(webBuilder => webBuilder
        .UseKestrel()
        .UseStartup<Startup>())
    .Build();

// Logged once, here, before host.Run() starts MeshPollBackgroundService's first pass - Startup's
// ConfigureServices has already registered the bound MeshHostConfig as a singleton by the time
// Build() returns. See MeshConfigSummary's remarks for why this exists and how redaction works.
host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Benzene.Mesh.Host.Program")
    .LogInformation("{Summary}", MeshConfigSummary.Format(host.Services.GetRequiredService<MeshHostConfig>()));

host.Run();

return 0;

int ValidateConfig(string? meshConfigPath)
{
    try
    {
        var config = MeshConfigValidator.Validate(meshConfigPath);
        Console.WriteLine("mesh.json is valid.");
        Console.WriteLine($"  artifactStore: {config.ArtifactStore.Type}");
        Console.WriteLine($"  services: {config.Services.Length}");
        Console.WriteLine($"  usage: {(config.Usage.Length == 0 ? "(none)" : string.Join(", ", config.Usage.Select(u => u.Source)))}");
        Console.WriteLine($"  fleet: {config.Fleet.Source}");
        Console.WriteLine($"  topology: {config.Topology.Source}");
        Console.WriteLine($"  dispatch: {(config.Dispatch.Enabled ? $"enabled (allowInProduction={config.Dispatch.AllowInProduction})" : "disabled")}");
        Console.WriteLine($"  auth: {config.Auth.Mode} (ingestion={config.Auth.Ingestion.Mode})");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"mesh.json is invalid: {ex.Message}");
        return 1;
    }
}
