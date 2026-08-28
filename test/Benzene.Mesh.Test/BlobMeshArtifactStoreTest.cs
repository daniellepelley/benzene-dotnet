using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Benzene.Mesh.Azure.Blob;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Unit coverage for <see cref="BlobMeshArtifactStore"/> - the Azure analogue of
/// <see cref="Benzene.Mesh.Aws.S3.S3MeshArtifactStore"/>, previously untested. Mocks
/// <see cref="BlobContainerClient"/>/<see cref="BlobClient"/> directly (both are designed to be
/// mockable - a protected parameterless constructor plus virtual members - the officially documented
/// way to unit test code built on the Azure SDK, with no real Azure account or network involved).
/// </summary>
public class BlobMeshArtifactStoreTest
{
    // ---- Key() path normalization (prefix/backslash/leading-slash) - exercised indirectly through
    // the public API: the blob name actually requested from the container is what matters. ----

    [Theory]
    [InlineData("manifest.json", "", "manifest.json")]
    [InlineData("manifest.json", "mesh", "mesh/manifest.json")]
    [InlineData("manifest.json", "mesh/", "mesh/manifest.json")] // trailing slash on the prefix is not doubled
    [InlineData("/manifest.json", "mesh", "mesh/manifest.json")] // leading slash on the path is stripped
    [InlineData("services\\orders.json", "mesh/", "mesh/services/orders.json")] // backslashes normalized to /
    // #242 regression pin: unlike FileSystemMeshArtifactStore, Key() does no path normalization at
    // all - a ".." segment stays a literal character sequence in the blob name, not a traversal
    // instruction, so this store's immunity to the finding is asserted, not assumed.
    [InlineData("services/../manifest.json", "", "services/../manifest.json")]
    public async Task PublishAsync_NormalizesTheBlobName(string relativePath, string prefix, string expectedBlobName)
    {
        var blobClientMock = new Mock<BlobClient>();
        blobClientMock
            .Setup(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);
        var containerMock = new Mock<BlobContainerClient>();
        string? requestedBlobName = null;
        containerMock.Setup(x => x.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => requestedBlobName = name)
            .Returns(blobClientMock.Object);

        var store = new BlobMeshArtifactStore(containerMock.Object, prefix);
        await store.PublishAsync(relativePath, "content");

        Assert.Equal(expectedBlobName, requestedBlobName);
    }

    [Fact]
    public async Task PublishAsync_SetsJsonContentType()
    {
        var blobClientMock = new Mock<BlobClient>();
        BlobUploadOptions? capturedOptions = null;
        blobClientMock
            .Setup(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, BlobUploadOptions, CancellationToken>((_, options, _) => capturedOptions = options)
            .ReturnsAsync((Response<BlobContentInfo>)null!);
        var containerMock = new Mock<BlobContainerClient>();
        containerMock.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blobClientMock.Object);

        var store = new BlobMeshArtifactStore(containerMock.Object);
        await store.PublishAsync("manifest.json", "{}");

        Assert.Equal("application/json", capturedOptions?.HttpHeaders?.ContentType);
    }

    // ---- null-on-404 mapping ----

    [Fact]
    public async Task TryReadAsync_BlobNotFound_ReturnsNull()
    {
        var blobClientMock = new Mock<BlobClient>();
        blobClientMock.Setup(x => x.DownloadContentAsync())
            .ThrowsAsync(new RequestFailedException(404, "The specified blob does not exist."));
        var containerMock = new Mock<BlobContainerClient>();
        containerMock.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blobClientMock.Object);

        var store = new BlobMeshArtifactStore(containerMock.Object);
        var result = await store.TryReadAsync("manifest.json");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryReadAsync_NonNotFoundBlobError_PropagatesRatherThanBeingSwallowed()
    {
        var blobClientMock = new Mock<BlobClient>();
        blobClientMock.Setup(x => x.DownloadContentAsync())
            .ThrowsAsync(new RequestFailedException(403, "Forbidden"));
        var containerMock = new Mock<BlobContainerClient>();
        containerMock.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blobClientMock.Object);

        var store = new BlobMeshArtifactStore(containerMock.Object);

        await Assert.ThrowsAsync<RequestFailedException>(() => store.TryReadAsync("manifest.json"));
    }

    // ---- UTF-8 round trip ----

    [Fact]
    public async Task PublishAsync_ThenTryReadAsync_RoundTripsUtf8Content()
    {
        const string content = "{\"service\":\"café\",\"note\":\"ümläut & ✓\"}"; // non-ASCII, actually exercises UTF-8

        byte[]? stored = null;
        var blobClientMock = new Mock<BlobClient>();
        blobClientMock
            .Setup(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, BlobUploadOptions, CancellationToken>((stream, _, _) =>
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                stored = memory.ToArray();
            })
            .ReturnsAsync((Response<BlobContentInfo>)null!);
        blobClientMock
            .Setup(x => x.DownloadContentAsync())
            .ReturnsAsync(() =>
            {
                var result = BlobsModelFactory.BlobDownloadResult(BinaryData.FromBytes(stored!), details: null!);
                return Response.FromValue(result, Mock.Of<Response>());
            });
        var containerMock = new Mock<BlobContainerClient>();
        containerMock.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blobClientMock.Object);

        var store = new BlobMeshArtifactStore(containerMock.Object);
        await store.PublishAsync("manifest.json", content);
        var result = await store.TryReadAsync("manifest.json");

        Assert.Equal(content, result);
    }
}
