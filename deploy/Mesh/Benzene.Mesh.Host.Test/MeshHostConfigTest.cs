using Microsoft.Extensions.Configuration;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// Config schema v1 (work/enterprise/slice-1-config-schema.md) adds <c>artifactStore</c>/<c>usage</c>/
/// <c>fleet</c>/<c>topology</c>/<c>dispatch</c>/<c>auth</c> to <c>mesh.json</c>. Guards two things: a
/// config that sets none of them (the pre-existing shape, e.g. <c>examples/K8sMesh/compose/mesh.json</c>)
/// still binds to today's defaults untouched, and a config that sets every new section binds every
/// field of every section correctly.
/// </summary>
public class MeshHostConfigTest
{
    private static MeshHostConfig Bind(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-host-config-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
            return configuration.Get<MeshHostConfig>() ?? new MeshHostConfig();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComposeSampleShape_SetsOnlyArtifactRootDirectoryPollIntervalAndServices_EveryNewSectionDefaults()
    {
        // Mirrors examples/K8sMesh/compose/mesh.json - must keep binding exactly as it did before this
        // slice, since that file is untouched by it.
        var config = Bind("""
            {
              "artifactRootDirectory": "/data/mesh-artifacts",
              "pollIntervalSeconds": 15,
              "services": [
                { "name": "orders", "specUrl": "http://orders:8080/benzene/spec?type=benzene", "healthUrl": "http://orders:8080/benzene/health" }
              ]
            }
            """);

        Assert.Equal("/data/mesh-artifacts", config.ArtifactRootDirectory);
        Assert.Equal(15, config.PollIntervalSeconds);
        Assert.Single(config.Services);

        Assert.Equal("file", config.ArtifactStore.Type);
        Assert.Null(config.ArtifactStore.Options);
        Assert.Empty(config.Usage);
        Assert.Equal("none", config.Fleet.Source);
        Assert.Null(config.Fleet.Options);
        Assert.Equal("none", config.Topology.Source);
        Assert.False(config.Dispatch.Enabled);
        Assert.False(config.Dispatch.AllowInProduction);
        Assert.Equal("none", config.Auth.Mode);
    }

    [Fact]
    public void FullConfig_EveryNewSectionSet_BindsEveryField()
    {
        var config = Bind("""
            {
              "artifactRootDirectory": "mesh-artifacts",
              "pollIntervalSeconds": 60,
              "services": [],
              "artifactStore": { "type": "s3", "options": { "bucket": "my-bucket", "prefix": "mesh/" } },
              "usage": [ { "source": "cloudwatch", "options": { "namespace": "Benzene/Mesh", "windowHours": "24" } } ],
              "fleet": { "source": "xray", "options": { "correlationLookbackHours": "24" } },
              "topology": { "source": "tempo", "options": { "prometheusUrl": "http://prometheus:9090/api/v1/query", "windowMinutes": "5" } },
              "dispatch": { "enabled": true, "allowInProduction": true },
              "auth": { "mode": "none" }
            }
            """);

        Assert.Equal("s3", config.ArtifactStore.Type);
        Assert.Equal("my-bucket", config.ArtifactStore.Options?["bucket"]);
        Assert.Equal("mesh/", config.ArtifactStore.Options?["prefix"]);

        Assert.Single(config.Usage);
        Assert.Equal("cloudwatch", config.Usage[0].Source);
        Assert.Equal("Benzene/Mesh", config.Usage[0].Options?["namespace"]);

        Assert.Equal("xray", config.Fleet.Source);
        Assert.Equal("24", config.Fleet.Options?["correlationLookbackHours"]);

        Assert.Equal("tempo", config.Topology.Source);
        Assert.Equal("http://prometheus:9090/api/v1/query", config.Topology.Options?["prometheusUrl"]);

        Assert.True(config.Dispatch.Enabled);
        Assert.True(config.Dispatch.AllowInProduction);

        Assert.Equal("none", config.Auth.Mode);
    }
}
