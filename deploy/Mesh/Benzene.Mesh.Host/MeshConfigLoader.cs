using Microsoft.Extensions.Configuration;

namespace Benzene.Mesh.Host;

/// <summary>
/// Loads the <c>MESH_CONFIG_PATH</c>-referenced <c>mesh.json</c> into the host's configuration.
/// Extracted from <c>Program.cs</c>'s top-level statements so it is directly testable.
/// </summary>
public static class MeshConfigLoader
{
    /// <summary>
    /// Adds <paramref name="meshConfigPath"/> as a JSON config source, if set.
    /// </summary>
    /// <param name="config">The configuration builder to add the source to.</param>
    /// <param name="meshConfigPath">
    /// The value of <c>MESH_CONFIG_PATH</c>, or <c>null</c>/empty if unset - in which case this is a
    /// no-op, leaving the host to run on <c>Host.CreateDefaultBuilder</c>'s own default sources
    /// (environment variables etc.) alone. That is the legitimate env-var-only local-dev path this
    /// package's README documents.
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="meshConfigPath"/> is set but no file exists there. Loading it as
    /// <c>optional: true</c> would otherwise start the host with zero configured services and an
    /// empty dashboard, saying nothing - the single worst failure mode for a typo'd path, so this
    /// fails loudly at startup instead, naming the path.
    /// </exception>
    public static void ConfigureMeshConfig(IConfigurationBuilder config, string? meshConfigPath)
    {
        if (string.IsNullOrEmpty(meshConfigPath))
        {
            return;
        }

        if (!File.Exists(meshConfigPath))
        {
            throw new FileNotFoundException(
                $"MESH_CONFIG_PATH is set to '{meshConfigPath}', but no file exists there.",
                meshConfigPath);
        }

        config.AddJsonFile(meshConfigPath, optional: false, reloadOnChange: false);
    }
}
