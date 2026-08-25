using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// Backs <c>--validate-config</c> (<see cref="Program"/>): binding and validating <c>mesh.json</c>
/// without starting the host is the only way to test a config change without deploying it. Guards that
/// a valid config binds cleanly and an invalid one throws naming the problem - the same
/// <see cref="MeshSourceRegistrar"/> rules <see cref="Startup"/> itself runs, so the two cannot
/// silently disagree.
/// </summary>
public class MeshConfigValidatorTest
{
    private static string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-config-validator-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ValidConfig_BindsAndReturnsIt()
    {
        var path = WriteTempConfig("""
            {
              "services": [ { "name": "orders-api" } ],
              "usage": [ { "source": "cloudwatch" } ],
              "fleet": { "source": "xray" }
            }
            """);
        try
        {
            var config = MeshConfigValidator.Validate(path);

            Assert.Single(config.Services);
            Assert.Equal("cloudwatch", config.Usage[0].Source);
            Assert.Equal("xray", config.Fleet.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComposeSampleShape_Validates()
    {
        // examples/K8sMesh/compose/mesh.json's shape - the pre-existing, section-free config - must
        // still validate cleanly under the new schema.
        var path = WriteTempConfig("""
            {
              "artifactRootDirectory": "/data/mesh-artifacts",
              "pollIntervalSeconds": 15,
              "services": [
                { "name": "orders", "specUrl": "http://orders:8080/benzene/spec?type=benzene", "healthUrl": "http://orders:8080/benzene/health" }
              ]
            }
            """);
        try
        {
            var config = MeshConfigValidator.Validate(path);

            Assert.Equal("/data/mesh-artifacts", config.ArtifactRootDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownUsageSource_ThrowsNamingTheBadValue()
    {
        var path = WriteTempConfig("""
            {
              "usage": [ { "source": "cloudwtach" } ]
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));

            Assert.Equal("Unknown usage source 'cloudwtach'. Valid values: cloudwatch, applicationInsights.", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingRequiredOption_ThrowsNamingTheMissingKey()
    {
        var path = WriteTempConfig("""
            {
              "fleet": { "source": "tempo" }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));

            Assert.Contains("'url'", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // WP-1(a) (#19): --validate-config runs the exact same MeshAuthGate.Validate rules Startup.Configure
    // does, including the dispatch.enabled x auth.mode satisfiability check - this catches the
    // misconfiguration before a deploy, not after one.
    [Fact]
    public void DispatchEnabledWithAuthModeNone_ThrowsNamingBothKeys()
    {
        var path = WriteTempConfig("""
            {
              "dispatch": { "enabled": true },
              "auth": { "mode": "none" }
            }
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => MeshConfigValidator.Validate(path));

            Assert.Contains("dispatch.enabled", exception.Message);
            Assert.Contains("none", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MeshConfigPathSetToMissingFile_ThrowsNamingThePath()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json");

        var exception = Assert.Throws<FileNotFoundException>(() => MeshConfigValidator.Validate(missingPath));

        Assert.Contains(missingPath, exception.Message);
    }
}
