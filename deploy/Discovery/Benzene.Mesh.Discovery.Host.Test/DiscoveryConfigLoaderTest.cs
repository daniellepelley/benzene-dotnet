using Microsoft.Extensions.Configuration;
using Xunit;

namespace Benzene.Mesh.Discovery.Host.Test;

/// <summary>Mirrors <c>Benzene.Mesh.Host.Test.MeshConfigLoaderTest</c> for the discovery job's config loader.</summary>
public class DiscoveryConfigLoaderTest
{
    [Fact]
    public void NullPath_IsANoOp()
    {
        var builder = new ConfigurationBuilder();

        var exception = Record.Exception(() => DiscoveryConfigLoader.ConfigureDiscoveryConfig(builder, null));

        Assert.Null(exception);
        Assert.Empty(builder.Sources);
    }

    [Fact]
    public void EmptyPath_IsANoOp()
    {
        var builder = new ConfigurationBuilder();

        var exception = Record.Exception(() => DiscoveryConfigLoader.ConfigureDiscoveryConfig(builder, string.Empty));

        Assert.Null(exception);
        Assert.Empty(builder.Sources);
    }

    [Fact]
    public void MissingPath_ThrowsFileNotFoundNamingThePath()
    {
        var builder = new ConfigurationBuilder();
        var missingPath = Path.Combine(Path.GetTempPath(), $"no-such-discovery-config-{Guid.NewGuid():N}.json");

        var exception = Assert.Throws<FileNotFoundException>(
            () => DiscoveryConfigLoader.ConfigureDiscoveryConfig(builder, missingPath));

        Assert.Contains(missingPath, exception.Message);
    }

    [Fact]
    public void ExistingPath_AddsItAsAJsonSource()
    {
        var path = Path.Combine(Path.GetTempPath(), $"discovery-config-loader-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"outputPath\": \"registry.json\" }");
        try
        {
            var builder = new ConfigurationBuilder();

            DiscoveryConfigLoader.ConfigureDiscoveryConfig(builder, path);

            var config = builder.Build().Get<DiscoveryHostConfig>();
            Assert.Equal("registry.json", config?.OutputPath);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
