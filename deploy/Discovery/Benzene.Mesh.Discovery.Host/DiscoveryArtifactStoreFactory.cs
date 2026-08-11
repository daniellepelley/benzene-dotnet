using Amazon.S3;
using Azure.Identity;
using Azure.Storage.Blobs;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Aws.S3;
using Benzene.Mesh.Azure.Blob;
using Benzene.Mesh.GoogleCloud.Storage;
using Google.Cloud.Storage.V1;

namespace Benzene.Mesh.Discovery.Host;

/// <summary>
/// Builds the <see cref="IMeshArtifactStore"/> the discovered registry document is written to -
/// mirrors <c>Benzene.Mesh.Host.MeshSourceRegistrar.RegisterArtifactStore</c>'s backend selection and
/// option names exactly (so the same <c>artifactStore</c> block works unmodified on both sides of the
/// discovery/host seam), but constructs the store directly rather than through Benzene's DI
/// container - this job has no container, it is a single linear run.
/// </summary>
public static class DiscoveryArtifactStoreFactory
{
    /// <summary>Valid <see cref="DiscoveryArtifactStoreConfig.Type"/> values, in the case <c>discovery.json</c> should use.</summary>
    public static readonly string[] ValidTypes = { "file", "s3", "azureBlob", "gcs" };

    /// <summary>Builds the configured store.</summary>
    /// <exception cref="InvalidOperationException">An unknown <c>type</c>, or a required option is missing.</exception>
    public static IMeshArtifactStore Build(DiscoveryHostConfig config)
    {
        var store = config.ArtifactStore;
        switch (store.Type.ToLowerInvariant())
        {
            case "file":
                return new FileSystemMeshArtifactStore(config.ArtifactRootDirectory);
            case "s3":
                {
                    var bucket = RequireOption(store.Options, "bucket", "s3");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    return new S3MeshArtifactStore(new AmazonS3Client(), bucket, prefix);
                }
            case "azureblob":
                {
                    var blobServiceUri = RequireOption(store.Options, "blobServiceUri", "azureBlob");
                    var container = RequireOption(store.Options, "container", "azureBlob");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    var containerClient = new BlobServiceClient(new Uri(blobServiceUri), new DefaultAzureCredential())
                        .GetBlobContainerClient(container);
                    return new BlobMeshArtifactStore(containerClient, prefix);
                }
            case "gcs":
                {
                    var bucket = RequireOption(store.Options, "bucket", "gcs");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    return new GcsMeshArtifactStore(StorageClient.Create(), bucket, prefix);
                }
            default:
                throw new InvalidOperationException(
                    $"Unknown artifact store type '{store.Type}'. Valid values: {string.Join(", ", ValidTypes)}.");
        }
    }

    private static string RequireOption(Dictionary<string, string>? options, string key, string storeType)
    {
        if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"artifact store '{storeType}' requires option '{key}'.");
        }
        return value;
    }

    private static string? GetOption(Dictionary<string, string>? options, string key)
        => options != null && options.TryGetValue(key, out var value) ? value : null;
}
