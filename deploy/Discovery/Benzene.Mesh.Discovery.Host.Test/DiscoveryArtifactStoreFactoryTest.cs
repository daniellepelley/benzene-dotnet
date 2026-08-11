using Xunit;

namespace Benzene.Mesh.Discovery.Host.Test;

/// <summary>
/// Guards <see cref="DiscoveryArtifactStoreFactory"/>'s fail-fast paths and its one cloud-SDK-free
/// case (<c>file</c>). Does not exercise <c>s3</c>/<c>azureBlob</c>/<c>gcs</c> success paths for the
/// same reason <see cref="DiscoveryProviderFactoryTest"/> does not exercise a known provider name -
/// constructing those SDK clients can throw immediately without ambient region/credentials.
/// </summary>
public class DiscoveryArtifactStoreFactoryTest
{
    [Fact]
    public void FileType_BuildsAFileSystemStore_NoNetworkOrCredentialsInvolved()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"discovery-artifact-store-test-{Guid.NewGuid():N}");
        var config = new DiscoveryHostConfig { ArtifactRootDirectory = tempDir };

        var store = DiscoveryArtifactStoreFactory.Build(config);

        Assert.NotNull(store);
    }

    [Fact]
    public void UnknownType_ThrowsNamingTheValidValues()
    {
        var config = new DiscoveryHostConfig();
        config.ArtifactStore.Type = "not-a-real-store";

        var exception = Assert.Throws<InvalidOperationException>(() => DiscoveryArtifactStoreFactory.Build(config));

        Assert.Contains("not-a-real-store", exception.Message);
        foreach (var valid in DiscoveryArtifactStoreFactory.ValidTypes)
        {
            Assert.Contains(valid, exception.Message);
        }
    }

    [Fact]
    public void S3Type_MissingBucketOption_ThrowsNamingTheMissingKey()
    {
        var config = new DiscoveryHostConfig();
        config.ArtifactStore.Type = "s3";

        var exception = Assert.Throws<InvalidOperationException>(() => DiscoveryArtifactStoreFactory.Build(config));

        Assert.Contains("bucket", exception.Message);
    }

    [Fact]
    public void AzureBlobType_MissingOptions_ThrowsNamingTheMissingKey()
    {
        var config = new DiscoveryHostConfig();
        config.ArtifactStore.Type = "azureBlob";

        var exception = Assert.Throws<InvalidOperationException>(() => DiscoveryArtifactStoreFactory.Build(config));

        Assert.Contains("blobServiceUri", exception.Message);
    }

    [Fact]
    public void GcsType_MissingBucketOption_ThrowsNamingTheMissingKey()
    {
        var config = new DiscoveryHostConfig();
        config.ArtifactStore.Type = "gcs";

        var exception = Assert.Throws<InvalidOperationException>(() => DiscoveryArtifactStoreFactory.Build(config));

        Assert.Contains("bucket", exception.Message);
    }
}
