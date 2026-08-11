using Microsoft.Extensions.Configuration;

namespace Benzene.Mesh.Discovery.Host;

/// <summary>
/// Loads the <c>DISCOVERY_CONFIG_PATH</c>-referenced <c>discovery.json</c> into this job's
/// configuration. Mirrors <c>Benzene.Mesh.Host.MeshConfigLoader</c> exactly, including its
/// fail-loudly-on-a-missing-path stance - copied rather than shared, since the two deployables share
/// no code (see <c>CLAUDE.md</c>).
/// </summary>
public static class DiscoveryConfigLoader
{
    /// <summary>
    /// Adds <paramref name="discoveryConfigPath"/> as a JSON config source, if set.
    /// </summary>
    /// <param name="config">The configuration builder to add the source to.</param>
    /// <param name="discoveryConfigPath">
    /// The value of <c>DISCOVERY_CONFIG_PATH</c>, or <c>null</c>/empty if unset - in which case this
    /// is a no-op, leaving every setting to come from plain environment variables (already wired by
    /// <c>Program.cs</c>) or this job's own defaults.
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="discoveryConfigPath"/> is set but no file exists there. Loading it as
    /// <c>optional: true</c> would otherwise run discovery with zero configured providers and write
    /// an empty registry document, saying nothing about the typo - this fails loudly at startup
    /// instead, naming the path.
    /// </exception>
    public static void ConfigureDiscoveryConfig(IConfigurationBuilder config, string? discoveryConfigPath)
    {
        if (string.IsNullOrEmpty(discoveryConfigPath))
        {
            return;
        }

        if (!File.Exists(discoveryConfigPath))
        {
            throw new FileNotFoundException(
                $"DISCOVERY_CONFIG_PATH is set to '{discoveryConfigPath}', but no file exists there.",
                discoveryConfigPath);
        }

        config.AddJsonFile(discoveryConfigPath, optional: false, reloadOnChange: false);
    }
}
