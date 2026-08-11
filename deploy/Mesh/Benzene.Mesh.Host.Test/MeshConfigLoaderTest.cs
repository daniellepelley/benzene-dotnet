using Benzene.Mesh.Host;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// Before this fix, <c>MESH_CONFIG_PATH</c> pointing at a missing file loaded silently
/// (<c>optional: true</c>) - the host started with zero configured services and an empty dashboard,
/// saying nothing. Guards that a typo'd path now fails loudly at startup instead, and that the
/// legitimate unset/env-var-only path (documented in <c>deploy/Mesh/README.md</c>) still works.
/// </summary>
public class MeshConfigLoaderTest
{
    [Fact]
    public void MeshConfigPathSetToMissingFile_ThrowsNamingThePath()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json");
        var config = new ConfigurationBuilder();

        var exception = Assert.Throws<FileNotFoundException>(
            () => MeshConfigLoader.ConfigureMeshConfig(config, missingPath));

        Assert.Contains(missingPath, exception.Message);
    }

    [Fact]
    public void MeshConfigPathUnset_DoesNotThrow()
    {
        var config = new ConfigurationBuilder();

        MeshConfigLoader.ConfigureMeshConfig(config, null);
        MeshConfigLoader.ConfigureMeshConfig(config, string.Empty);

        // Nothing above threw - the unset/env-var-only path (which Host.CreateDefaultBuilder's own
        // default sources already cover) stays a no-op here.
    }

    [Fact]
    public void MeshConfigPathSetToExistingFile_AddsItAsAConfigSource()
    {
        var existingPath = Path.Combine(Path.GetTempPath(), $"mesh-config-loader-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(existingPath, "{\"artifactRootDirectory\": \"from-file\"}");
        try
        {
            var config = new ConfigurationBuilder();

            MeshConfigLoader.ConfigureMeshConfig(config, existingPath);

            Assert.Equal("from-file", config.Build()["artifactRootDirectory"]);
        }
        finally
        {
            File.Delete(existingPath);
        }
    }
}
