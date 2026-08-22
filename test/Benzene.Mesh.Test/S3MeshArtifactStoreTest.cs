using System.Net;
using System.Text;
using System.Threading;
using Amazon.S3;
using Amazon.S3.Model;
using Benzene.Mesh.Aws.S3;
using Moq;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Unit coverage for <see cref="S3MeshArtifactStore"/> - the write path for the mesh's entire
/// published catalog (manifest.json, services/*.json, topology.json, registry.json), previously
/// untested. Mocks <see cref="IAmazonS3"/> (an interface, no real AWS credentials or network
/// involved), matching the pattern <c>Discovery/AwsLambdaDiscoveryProviderTest.cs</c> already uses
/// for AWS SDK clients in this test project.
/// </summary>
public class S3MeshArtifactStoreTest
{
    // ---- Key() path normalization (prefix/backslash/leading-slash) - exercised indirectly through
    // the public API, since Key() itself is private: the object key actually sent to S3 is what
    // matters, not the private helper that computes it. ----

    [Theory]
    [InlineData("manifest.json", "", "manifest.json")]
    [InlineData("manifest.json", "mesh", "mesh/manifest.json")]
    [InlineData("manifest.json", "mesh/", "mesh/manifest.json")] // trailing slash on the prefix is not doubled
    [InlineData("/manifest.json", "mesh", "mesh/manifest.json")] // leading slash on the path is stripped
    [InlineData("services\\orders.json", "mesh/", "mesh/services/orders.json")] // backslashes normalized to /
    public async Task PublishAsync_NormalizesTheObjectKey(string relativePath, string prefix, string expectedKey)
    {
        var mock = new Mock<IAmazonS3>();
        string? capturedKey = null;
        mock.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => capturedKey = request.Key)
            .ReturnsAsync(new PutObjectResponse());

        var store = new S3MeshArtifactStore(mock.Object, "bucket", prefix);
        await store.PublishAsync(relativePath, "content");

        Assert.Equal(expectedKey, capturedKey);
    }

    [Fact]
    public async Task PublishAsync_WritesToTheConfiguredBucketWithJsonContentType()
    {
        var mock = new Mock<IAmazonS3>();
        PutObjectRequest? captured = null;
        mock.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PutObjectResponse());

        var store = new S3MeshArtifactStore(mock.Object, "mesh-artifacts-bucket");
        await store.PublishAsync("manifest.json", "{}");

        Assert.NotNull(captured);
        Assert.Equal("mesh-artifacts-bucket", captured!.BucketName);
        Assert.Equal("application/json", captured.ContentType);
    }

    // ---- null-on-404 mapping ----

    [Fact]
    public async Task TryReadAsync_ObjectNotFound_ReturnsNull()
    {
        var mock = new Mock<IAmazonS3>();
        mock.Setup(x => x.GetObjectAsync("bucket", "manifest.json", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("The specified key does not exist.")
            {
                StatusCode = HttpStatusCode.NotFound
            });

        var store = new S3MeshArtifactStore(mock.Object, "bucket");
        var result = await store.TryReadAsync("manifest.json");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryReadAsync_NonNotFoundS3Error_PropagatesRatherThanBeingSwallowed()
    {
        // Only a 404 means "absent" - every other S3 failure (permissions, throttling, ...) is a real
        // problem the caller needs to see, not a silent null.
        var mock = new Mock<IAmazonS3>();
        mock.Setup(x => x.GetObjectAsync("bucket", "manifest.json", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Access Denied") { StatusCode = HttpStatusCode.Forbidden });

        var store = new S3MeshArtifactStore(mock.Object, "bucket");

        await Assert.ThrowsAsync<AmazonS3Exception>(() => store.TryReadAsync("manifest.json"));
    }

    // ---- UTF-8 round trip ----

    [Fact]
    public async Task PublishAsync_ThenTryReadAsync_RoundTripsUtf8Content()
    {
        const string content = "{\"service\":\"café\",\"note\":\"ümläut & ✓\"}"; // non-ASCII, actually exercises UTF-8

        var mock = new Mock<IAmazonS3>();
        string? stored = null;
        mock.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => stored = request.ContentBody)
            .ReturnsAsync(new PutObjectResponse());
        mock.Setup(x => x.GetObjectAsync("bucket", "manifest.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(stored!))
            });

        var store = new S3MeshArtifactStore(mock.Object, "bucket");
        await store.PublishAsync("manifest.json", content);
        var result = await store.TryReadAsync("manifest.json");

        Assert.Equal(content, result);
    }
}
