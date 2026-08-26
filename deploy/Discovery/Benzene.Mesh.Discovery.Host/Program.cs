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
        var failures = new List<MeshDiscoveryProviderFailure>();
        var registry = await runner.DiscoverAsync(config.Filter.ToFilter(), failures: failures);

        // See DiscoveryPublicationDecision for the reasoning: refuse to publish only when every
        // configured provider failed (an empty registry would then read as "the fleet is gone");
        // some providers failing still publishes whichever providers succeeded.
        if (!DiscoveryPublicationDecision.ShouldPublish(providers.Count, failures.Count))
        {
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"discovery: provider '{failure.ProviderKey}' failed: {failure.ErrorType}");
            }

            Console.Error.WriteLine(
                $"discovery failed: all {providers.Count} configured provider(s) failed; refusing to publish an empty registry.");
            return 1;
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine(
                $"discovery: provider '{failure.ProviderKey}' failed and was skipped: {failure.ErrorType}");
        }

        var store = DiscoveryArtifactStoreFactory.Build(config);
        await store.PublishAsync(config.OutputPath, MeshRegistryJson.Serialize(registry));

        Console.WriteLine($"discovery: wrote {registry.Services.Length} service(s) to '{config.OutputPath}'" +
            (failures.Count > 0 ? $" ({failures.Count} of {providers.Count} provider(s) failed - see above)." : "."));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"discovery failed: {ex.Message}");
        return 1;
    }
}
