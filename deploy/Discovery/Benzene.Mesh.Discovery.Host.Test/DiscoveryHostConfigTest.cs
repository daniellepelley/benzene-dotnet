using Microsoft.Extensions.Configuration;
using Xunit;

namespace Benzene.Mesh.Discovery.Host.Test;

/// <summary>
/// Guards <c>discovery.json</c> binding: the defaults a config that sets nothing gets, every field
/// binding correctly when set, and <see cref="DiscoveryFilterConfig.ToFilter"/> producing the
/// presence-only tag match <c>MeshDiscoveryFilter</c> expects (task 3.3 - the filter was previously
/// never configurable; every caller in the codebase did <c>new MeshDiscoveryFilter()</c>).
/// </summary>
public class DiscoveryHostConfigTest
{
    private static DiscoveryHostConfig Bind(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"discovery-host-config-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
            return configuration.Get<DiscoveryHostConfig>() ?? new DiscoveryHostConfig();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmptyConfig_DefaultsToNoProvidersAndBenzeneTagKeyAndFileStore()
    {
        var config = Bind("{}");

        Assert.Empty(config.Providers);
        Assert.Equal("benzene", config.Filter.TagKey);
        Assert.Null(config.Filter.Regions);
        Assert.Null(config.Filter.Namespace);
        Assert.Equal("file", config.ArtifactStore.Type);
        Assert.Equal("registry.json", config.OutputPath);
    }

    [Fact]
    public void FullConfig_BindsEveryField()
    {
        var config = Bind("""
            {
              "providers": [ "awsLambda", "kubernetes" ],
              "filter": { "tagKey": "team-orders", "regions": [ "us-east-1", "eu-west-1" ], "namespace": "orders-ns" },
              "artifactRootDirectory": "/tmp/discovery-artifacts",
              "artifactStore": { "type": "s3", "options": { "bucket": "discovery-bucket", "prefix": "mesh/" } },
              "outputPath": "discovery/registry.json"
            }
            """);

        Assert.Equal(new[] { "awsLambda", "kubernetes" }, config.Providers);
        Assert.Equal("team-orders", config.Filter.TagKey);
        Assert.Equal(new[] { "us-east-1", "eu-west-1" }, config.Filter.Regions);
        Assert.Equal("orders-ns", config.Filter.Namespace);
        Assert.Equal("/tmp/discovery-artifacts", config.ArtifactRootDirectory);
        Assert.Equal("s3", config.ArtifactStore.Type);
        Assert.Equal("discovery-bucket", config.ArtifactStore.Options?["bucket"]);
        Assert.Equal("discovery/registry.json", config.OutputPath);
    }

    [Fact]
    public void FilterToFilter_TagKeyOnly_ProducesPresenceOnlyTagMatch()
    {
        var config = new DiscoveryFilterConfig { TagKey = "team-orders" };

        var filter = config.ToFilter();

        var tag = Assert.Single(filter.Tags);
        Assert.Equal("team-orders", tag.Key);
        Assert.Null(tag.Value);
    }

    [Fact]
    public void FilterToFilter_RegionsAndNamespace_CarryThrough()
    {
        var config = new DiscoveryFilterConfig
        {
            TagKey = "benzene",
            Regions = new[] { "us-east-1" },
            Namespace = "orders-ns",
        };

        var filter = config.ToFilter();

        Assert.Equal(new[] { "us-east-1" }, filter.Regions);
        Assert.Equal("orders-ns", filter.Namespace);
    }
}
