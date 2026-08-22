using System.Net;
using System.Text;
using Benzene.Mesh.GoogleCloud.Storage;
using Google;
using Google.Apis.Download;
using Google.Apis.Upload;
using Google.Cloud.Storage.V1;
using Moq;
using Xunit;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace Benzene.Mesh.Test;

/// <summary>
/// Unit coverage for <see cref="GcsMeshArtifactStore"/> - the Google Cloud analogue of
/// <see cref="Benzene.Mesh.Aws.S3.S3MeshArtifactStore"/>/<see cref="Benzene.Mesh.Azure.Blob.BlobMeshArtifactStore"/>,
/// previously untested. Mocks <see cref="StorageClient"/> directly - an abstract class with virtual
/// upload/download members, the documented way to unit test code built on the Google Cloud client
/// libraries, with no real GCP project or network involved.
/// </summary>
public class GcsMeshArtifactStoreTest
{
    // ---- Key() path normalization (prefix/backslash/leading-slash) - exercised indirectly through
    // the public API: the object name actually requested from the client is what matters. ----

    [Theory]
    [InlineData("manifest.json", "", "manifest.json")]
    [InlineData("manifest.json", "mesh", "mesh/manifest.json")]
    [InlineData("manifest.json", "mesh/", "mesh/manifest.json")] // trailing slash on the prefix is not doubled
    [InlineData("/manifest.json", "mesh", "mesh/manifest.json")] // leading slash on the path is stripped
    [InlineData("services\\orders.json", "mesh/", "mesh/services/orders.json")] // backslashes normalized to /
    public async Task PublishAsync_NormalizesTheObjectName(string relativePath, string prefix, string expectedObjectName)
    {
        var mock = new Mock<StorageClient>();
        string? requestedObjectName = null;
        mock.Setup(x => x.UploadObjectAsync(
                "bucket", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<UploadObjectOptions>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<IUploadProgress>>()))
            .Callback<string, string, string, Stream, UploadObjectOptions, CancellationToken, IProgress<IUploadProgress>>(
                (_, objectName, _, _, _, _, _) => requestedObjectName = objectName)
            .ReturnsAsync(new GcsObject());

        var store = new GcsMeshArtifactStore(mock.Object, "bucket", prefix);
        await store.PublishAsync(relativePath, "content");

        Assert.Equal(expectedObjectName, requestedObjectName);
    }

    [Fact]
    public async Task PublishAsync_UsesTheConfiguredBucketAndJsonContentType()
    {
        var mock = new Mock<StorageClient>();
        string? capturedBucket = null;
        string? capturedContentType = null;
        mock.Setup(x => x.UploadObjectAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<UploadObjectOptions>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<IUploadProgress>>()))
            .Callback<string, string, string, Stream, UploadObjectOptions, CancellationToken, IProgress<IUploadProgress>>(
                (bucket, _, contentType, _, _, _, _) =>
                {
                    capturedBucket = bucket;
                    capturedContentType = contentType;
                })
            .ReturnsAsync(new GcsObject());

        var store = new GcsMeshArtifactStore(mock.Object, "mesh-artifacts-bucket");
        await store.PublishAsync("manifest.json", "{}");

        Assert.Equal("mesh-artifacts-bucket", capturedBucket);
        Assert.Equal("application/json", capturedContentType);
    }

    // ---- null-on-404 mapping ----

    [Fact]
    public async Task TryReadAsync_ObjectNotFound_ReturnsNull()
    {
        var mock = new Mock<StorageClient>();
        mock.Setup(x => x.DownloadObjectAsync(
                "bucket", It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<DownloadObjectOptions>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<IDownloadProgress>>()))
            .ThrowsAsync(new GoogleApiException("storage", "not found") { HttpStatusCode = HttpStatusCode.NotFound });

        var store = new GcsMeshArtifactStore(mock.Object, "bucket");
        var result = await store.TryReadAsync("manifest.json");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryReadAsync_NonNotFoundGcsError_PropagatesRatherThanBeingSwallowed()
    {
        var mock = new Mock<StorageClient>();
        mock.Setup(x => x.DownloadObjectAsync(
                "bucket", It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<DownloadObjectOptions>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<IDownloadProgress>>()))
            .ThrowsAsync(new GoogleApiException("storage", "forbidden") { HttpStatusCode = HttpStatusCode.Forbidden });

        var store = new GcsMeshArtifactStore(mock.Object, "bucket");

        await Assert.ThrowsAsync<GoogleApiException>(() => store.TryReadAsync("manifest.json"));
    }

    // ---- UTF-8 round trip ----

    [Fact]
    public async Task PublishAsync_ThenTryReadAsync_RoundTripsUtf8Content()
    {
        const string content = "{\"service\":\"café\",\"note\":\"ümläut & ✓\"}"; // non-ASCII, actually exercises UTF-8

        byte[]? stored = null;
        var mock = new Mock<StorageClient>();
        mock.Setup(x => x.UploadObjectAsync(
                "bucket", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<UploadObjectOptions>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<IUploadProgress>>()))
            .Callback<string, string, string, Stream, UploadObjectOptions, CancellationToken, IProgress<IUploadProgress>>(
                (_, _, _, source, _, _, _) =>
                {
                    using var memory = new MemoryStream();
                    source.CopyTo(memory);
                    stored = memory.ToArray();
                })
            .ReturnsAsync(new GcsObject());
        mock.Setup(x => x.DownloadObjectAsync(
                "bucket", It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<DownloadObjectOptions>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<IDownloadProgress>>()))
            .Callback<string, string, Stream, DownloadObjectOptions, CancellationToken, IProgress<IDownloadProgress>>(
                (_, _, destination, _, _, _) => destination.Write(stored!, 0, stored!.Length))
            .ReturnsAsync(new GcsObject());

        var store = new GcsMeshArtifactStore(mock.Object, "bucket");
        await store.PublishAsync("manifest.json", content);
        var result = await store.TryReadAsync("manifest.json");

        Assert.Equal(content, result);
    }
}
