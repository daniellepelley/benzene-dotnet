using Benzene.Mesh.Contracts;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Mesh.Host;

/// <summary>
/// Binds and validates <c>mesh.json</c> without starting the host - backs <c>--validate-config</c>
/// (see <see cref="Program"/>). Runs the exact same registration calls <see cref="Startup"/> does
/// (<see cref="MeshSourceRegistrar"/>) against a throwaway container that is discarded afterwards, so
/// a config that validates is guaranteed to start the same way - there is only one copy of the rules,
/// not two that could silently drift apart.
/// </summary>
public static class MeshConfigValidator
{
    /// <summary>
    /// Binds <paramref name="meshConfigPath"/> (if set) plus environment variables into a
    /// <see cref="MeshHostConfig"/>, then runs every section's registration
    /// (<see cref="MeshSourceRegistrar"/>) against a throwaway container to prove the config is valid.
    /// No network call is made - every adapter package registers its cloud client lazily, so this
    /// needs no live credentials, only a well-formed config.
    /// </summary>
    /// <param name="meshConfigPath">The <c>MESH_CONFIG_PATH</c> value, or null/empty for env-vars-only.</param>
    /// <returns>The bound, validated config.</returns>
    /// <exception cref="FileNotFoundException"><paramref name="meshConfigPath"/> is set but no file exists there.</exception>
    /// <exception cref="InvalidOperationException">An unknown source/type name, or a required option is missing.</exception>
    public static MeshHostConfig Validate(string? meshConfigPath)
    {
        var configBuilder = new ConfigurationBuilder().AddEnvironmentVariables();
        MeshConfigLoader.ConfigureMeshConfig(configBuilder, meshConfigPath);
        var config = configBuilder.Build().Get<MeshHostConfig>() ?? new MeshHostConfig();

        var registry = new MeshServiceRegistry(config.Services.Select(s => s.ToEntry()).ToArray());
        var container = new MicrosoftBenzeneServiceContainer(new ServiceCollection());

        MeshSourceRegistrar.RegisterArtifactStore(container, registry, config);
        MeshSourceRegistrar.RegisterUsageSources(container, config.Usage);
        MeshSourceRegistrar.RegisterFleet(container, config.Fleet);
        MeshSourceRegistrar.RegisterTopology(container, config.Topology);

        return config;
    }
}
