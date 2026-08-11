using Benzene.Abstractions.DI;
using Benzene.Mesh.Contracts;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// <see cref="MeshSourceRegistrar"/> is the one place config schema v1's <c>source</c>/<c>type</c> names
/// are known - a typo must fail loudly at startup naming the valid values (a silent fallback to a
/// default is the single worst failure mode this host can ship), and each valid name must register what
/// it says it does. Every assertion here checks the registration list
/// (<see cref="IBenzeneServiceContainer.IsTypeRegistered{TService}"/>), never a resolved instance - per
/// <c>work/enterprise/slice-1-config-schema.md</c>, this must not need live cloud credentials.
/// </summary>
/// <remarks>
/// The <c>gcs</c> artifact-store type is deliberately not exercised past its missing-option fail-fast
/// case here: <c>Benzene.Mesh.GoogleCloud.Storage.Extensions.AddMeshAggregatorWithGcs(services, registry,
/// bucket, prefix)</c> - the overload this registrar calls - builds its <c>StorageClient</c> via
/// <c>StorageClient.Create()</c> EAGERLY, at registration time, not lazily behind a factory the way the S3
/// (<c>TryAddSingleton&lt;IAmazonS3&gt;(_ => new AmazonS3Client())</c>) and Azure Blob
/// (<c>new BlobServiceClient(uri, new DefaultAzureCredential())</c>) equivalents do. Confirmed by hand:
/// <c>StorageClient.Create()</c> without Application Default Credentials throws
/// <c>InvalidOperationException</c> immediately (it does not hang), so a "gcs" registration - and
/// <c>--validate-config</c> against a "gcs" config - fails in any environment without GCP ADC configured,
/// independent of whether the config itself is well-formed. Out of scope to fix here (it would mean
/// changing <c>Benzene.Mesh.GoogleCloud.Storage</c>, a package this slice does not otherwise touch) -
/// flagged in the slice report instead.
/// </remarks>
public class MeshSourceRegistrarTest
{
    private static IBenzeneServiceContainer NewContainer() => new MicrosoftBenzeneServiceContainer(new ServiceCollection());

    private static MeshServiceRegistry EmptyRegistry() => new(Array.Empty<MeshServiceRegistryEntry>());

    // --- artifactStore.type -------------------------------------------------------------------

    [Fact]
    public void RegisterArtifactStore_TypeFile_RegistersArtifactStore()
    {
        var container = NewContainer();
        var config = new MeshHostConfig { ArtifactStore = new MeshArtifactStoreConfig { Type = "file" } };

        MeshSourceRegistrar.RegisterArtifactStore(container, EmptyRegistry(), config);

        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Aggregator.IMeshArtifactStore>());
    }

    [Fact]
    public void RegisterArtifactStore_TypeS3_RegistersS3ClientAndArtifactStore()
    {
        var container = NewContainer();
        var config = new MeshHostConfig
        {
            ArtifactStore = new MeshArtifactStoreConfig
            {
                Type = "s3",
                Options = new Dictionary<string, string> { ["bucket"] = "my-bucket" },
            },
        };

        MeshSourceRegistrar.RegisterArtifactStore(container, EmptyRegistry(), config);

        Assert.True(container.IsTypeRegistered<Amazon.S3.IAmazonS3>());
        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Aggregator.IMeshArtifactStore>());
    }

    [Fact]
    public void RegisterArtifactStore_TypeS3_MissingBucket_ThrowsNamingTheMissingKey()
    {
        var container = NewContainer();
        var config = new MeshHostConfig { ArtifactStore = new MeshArtifactStoreConfig { Type = "s3" } };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterArtifactStore(container, EmptyRegistry(), config));

        Assert.Contains("'bucket'", exception.Message);
    }

    [Fact]
    public void RegisterArtifactStore_TypeAzureBlob_RegistersArtifactStore()
    {
        var container = NewContainer();
        var config = new MeshHostConfig
        {
            ArtifactStore = new MeshArtifactStoreConfig
            {
                Type = "azureBlob",
                Options = new Dictionary<string, string>
                {
                    ["blobServiceUri"] = "https://example.blob.core.windows.net",
                    ["container"] = "mesh-artifacts",
                },
            },
        };

        MeshSourceRegistrar.RegisterArtifactStore(container, EmptyRegistry(), config);

        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Aggregator.IMeshArtifactStore>());
    }

    [Fact]
    public void RegisterArtifactStore_TypeAzureBlob_MissingContainer_ThrowsNamingTheMissingKey()
    {
        var container = NewContainer();
        var config = new MeshHostConfig
        {
            ArtifactStore = new MeshArtifactStoreConfig
            {
                Type = "azureBlob",
                Options = new Dictionary<string, string> { ["blobServiceUri"] = "https://example.blob.core.windows.net" },
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterArtifactStore(container, EmptyRegistry(), config));

        Assert.Contains("'container'", exception.Message);
    }

    [Fact]
    public void RegisterArtifactStore_TypeGcs_MissingBucket_ThrowsNamingTheMissingKey()
    {
        // Fails before ever reaching Benzene.Mesh.GoogleCloud.Storage's eager StorageClient.Create() -
        // see the class-level remarks on why the gcs happy path isn't exercised here.
        var container = NewContainer();
        var config = new MeshHostConfig { ArtifactStore = new MeshArtifactStoreConfig { Type = "gcs" } };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterArtifactStore(container, EmptyRegistry(), config));

        Assert.Contains("'bucket'", exception.Message);
    }

    [Fact]
    public void RegisterArtifactStore_UnknownType_ThrowsListingValidValues()
    {
        var container = NewContainer();
        var config = new MeshHostConfig { ArtifactStore = new MeshArtifactStoreConfig { Type = "sharepoint" } };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterArtifactStore(container, EmptyRegistry(), config));

        Assert.Equal("Unknown artifact store type 'sharepoint'. Valid values: file, s3, azureBlob, gcs.", exception.Message);
    }

    // --- usage[].source -------------------------------------------------------------------------

    [Fact]
    public void RegisterUsageSources_CloudWatch_RegistersCloudWatchClientAndUsageSource()
    {
        var container = NewContainer();

        MeshSourceRegistrar.RegisterUsageSources(container, new[] { new MeshUsageSourceConfig { Source = "cloudwatch" } });

        Assert.True(container.IsTypeRegistered<Amazon.CloudWatch.IAmazonCloudWatch>());
        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Contracts.IMeshUsageSource>());
    }

    [Fact]
    public void RegisterUsageSources_ApplicationInsights_RegistersUsageSource()
    {
        var container = NewContainer();
        var config = new MeshUsageSourceConfig
        {
            Source = "applicationInsights",
            Options = new Dictionary<string, string> { ["workspaceId"] = "11111111-1111-1111-1111-111111111111" },
        };

        MeshSourceRegistrar.RegisterUsageSources(container, new[] { config });

        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Contracts.IMeshUsageSource>());
    }

    [Fact]
    public void RegisterUsageSources_ApplicationInsights_MissingWorkspaceId_ThrowsNamingTheMissingKey()
    {
        var container = NewContainer();
        var config = new MeshUsageSourceConfig { Source = "applicationInsights" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterUsageSources(container, new[] { config }));

        Assert.Contains("'workspaceId'", exception.Message);
    }

    [Fact]
    public void RegisterUsageSources_UnknownSource_ThrowsListingValidValues()
    {
        var container = NewContainer();
        var config = new MeshUsageSourceConfig { Source = "cloudwtach" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterUsageSources(container, new[] { config }));

        Assert.Equal("Unknown usage source 'cloudwtach'. Valid values: cloudwatch, applicationInsights.", exception.Message);
    }

    // --- fleet.source -------------------------------------------------------------------------

    [Fact]
    public void RegisterFleet_SourceNone_RegistersNothingAndReturnsFalse()
    {
        var container = NewContainer();

        var registered = MeshSourceRegistrar.RegisterFleet(container, new MeshFleetConfig { Source = "none" });

        Assert.False(registered);
        Assert.False(container.IsTypeRegistered<Benzene.Mesh.Collector.IMeshFleetReadModel>());
    }

    [Fact]
    public void RegisterFleet_SourceXRay_RegistersFleetReadModelAndReturnsTrue()
    {
        var container = NewContainer();

        var registered = MeshSourceRegistrar.RegisterFleet(container, new MeshFleetConfig { Source = "xray" });

        Assert.True(registered);
        Assert.True(container.IsTypeRegistered<Amazon.XRay.IAmazonXRay>());
        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Collector.IMeshFleetReadModel>());
    }

    [Fact]
    public void RegisterFleet_SourceTempo_RegistersFleetReadModelAndReturnsTrue()
    {
        var container = NewContainer();
        var config = new MeshFleetConfig { Source = "tempo", Options = new Dictionary<string, string> { ["url"] = "http://tempo:3200" } };

        var registered = MeshSourceRegistrar.RegisterFleet(container, config);

        Assert.True(registered);
        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Collector.IMeshFleetReadModel>());
    }

    [Fact]
    public void RegisterFleet_SourceTempo_MissingUrl_ThrowsNamingTheMissingKey()
    {
        var container = NewContainer();
        var config = new MeshFleetConfig { Source = "tempo" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterFleet(container, config));

        Assert.Contains("'url'", exception.Message);
    }

    [Fact]
    public void RegisterFleet_SourceJaeger_RegistersFleetReadModelAndReturnsTrue()
    {
        var container = NewContainer();
        var config = new MeshFleetConfig { Source = "jaeger", Options = new Dictionary<string, string> { ["url"] = "http://jaeger:16686" } };

        var registered = MeshSourceRegistrar.RegisterFleet(container, config);

        Assert.True(registered);
        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Collector.IMeshFleetReadModel>());
    }

    [Fact]
    public void RegisterFleet_UnknownSource_ThrowsListingValidValues()
    {
        var container = NewContainer();
        var config = new MeshFleetConfig { Source = "zipkin" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterFleet(container, config));

        Assert.Equal("Unknown fleet source 'zipkin'. Valid values: none, xray, tempo, jaeger.", exception.Message);
    }

    // --- topology.source -------------------------------------------------------------------------

    [Fact]
    public void RegisterTopology_SourceNone_RegistersNothingAndReturnsFalse()
    {
        var container = NewContainer();

        var registered = MeshSourceRegistrar.RegisterTopology(container, new MeshTopologyConfig { Source = "none" });

        Assert.False(registered);
    }

    [Fact]
    public void RegisterTopology_SourceTempo_RegistersTopologyBuilderAndReturnsTrue()
    {
        var container = NewContainer();
        var config = new MeshTopologyConfig { Source = "tempo", Options = new Dictionary<string, string> { ["prometheusUrl"] = "http://prometheus:9090/api/v1/query" } };

        var registered = MeshSourceRegistrar.RegisterTopology(container, config);

        Assert.True(registered);
        Assert.True(container.IsTypeRegistered<Benzene.Mesh.Tracing.Tempo.TempoServiceGraphTopologyBuilder>());
    }

    [Fact]
    public void RegisterTopology_SourceTempo_MissingPrometheusUrl_ThrowsNamingTheMissingKey()
    {
        var container = NewContainer();
        var config = new MeshTopologyConfig { Source = "tempo" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterTopology(container, config));

        Assert.Contains("'prometheusUrl'", exception.Message);
    }

    [Fact]
    public void RegisterTopology_UnknownSource_ThrowsListingValidValues()
    {
        var container = NewContainer();
        var config = new MeshTopologyConfig { Source = "kiali" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MeshSourceRegistrar.RegisterTopology(container, config));

        Assert.Equal("Unknown topology source 'kiali'. Valid values: none, tempo.", exception.Message);
    }
}
