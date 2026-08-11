using Benzene.Mesh.Contracts;
using Benzene.Mesh.Discovery.Host;
using Microsoft.Extensions.Configuration;

// A job, not a server: run discovery once, write the registry document, exit. No web host, no poll
// loop - see ../README.md for why. The exit code is the only contract a scheduler (EventBridge rule,
// Cloud Scheduler job, Kubernetes CronJob) needs: 0 succeeded, non-zero failed and needs attention.
return await RunAsync();

async Task<int> RunAsync()
{
    try
    {
        var configBuilder = new ConfigurationBuilder().AddEnvironmentVariables();
        DiscoveryConfigLoader.ConfigureDiscoveryConfig(configBuilder, Environment.GetEnvironmentVariable("DISCOVERY_CONFIG_PATH"));
        var config = configBuilder.Build().Get<DiscoveryHostConfig>() ?? new DiscoveryHostConfig();

        var providers = DiscoveryProviderFactory.Build(config.Providers);
        var runner = new MeshDiscoveryRunner(providers);
        var registry = await runner.DiscoverAsync(config.Filter.ToFilter());

        var store = DiscoveryArtifactStoreFactory.Build(config);
        await store.PublishAsync(config.OutputPath, MeshRegistryJson.Serialize(registry));

        Console.WriteLine($"discovery: wrote {registry.Services.Length} service(s) to '{config.OutputPath}'.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"discovery failed: {ex.Message}");
        return 1;
    }
}
