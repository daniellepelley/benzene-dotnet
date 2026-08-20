using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Benzene.ClaimCheck;
using Benzene.ClaimCheck.Azure.Blob;
using Moq;
using Xunit;

namespace Benzene.Test.ClaimCheck.Blob;

// No Azurite integration test in this pass, deliberately (see work/archive/claim-check-plan-2026-08.md, Phase 5): the
// emulator fixtures are heavy, and Benzene.ClaimCheck.Aws.S3's LocalStack integration test already
// proves the offload/hydrate middleware pair end to end against a real object store - a second
// emulator-backed test here would exercise the same middleware behavior against a different SDK, not
// anything new. These are unit tests against a mocked BlobContainerClient/BlobClient - both have
// virtual members precisely so Moq can proxy them, the same technique
// test/Benzene.Core.Test/Clients/Azure/QueueStorage/QueueStorageHealthCheckTest.cs uses for QueueClient.
public class BlobClaimCheckStoreTest
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static (Mock<BlobContainerClient> Container, Mock<BlobClient> Blob) Mocks(string containerName = "claims")
    {
        var blob = new Mock<BlobClient>();
        var container = new Mock<BlobContainerClient>();
        container.Setup(x => x.Name).Returns(containerName);
        container.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        return (container, blob);
    }

    private static Response<T> MockedResponse<T>(T value) => Response.FromValue(value, Mock.Of<Response>());

    private static void SetUpUpload(Mock<BlobClient> blob, Action<Stream, BlobUploadOptions, CancellationToken>? onUpload = null)
    {
        var info = BlobsModelFactory.BlobContentInfo(new ETag("etag"), DateTimeOffset.UtcNow, Array.Empty<byte>(), null, null, 0);
        var setup = blob.Setup(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()));
        if (onUpload != null)
        {
            setup.Callback(onUpload);
        }
        setup.ReturnsAsync(MockedResponse(info));
    }

    private static void SetUpDownload(Mock<BlobClient> blob, string body)
    {
        var result = BlobsModelFactory.BlobDownloadResult(BinaryData.FromString(body), null!);
        blob.Setup(x => x.DownloadContentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(MockedResponse(result));
    }

    [Fact]
    public async Task PutAsync_IssuesExpectedKeyAndReference_AndUploadsTheBodyVerbatim()
    {
        var (container, blob) = Mocks("claims");
        string? requestedKey = null;
        string? uploadedBody = null;
        container.Setup(x => x.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(k => requestedKey = k)
            .Returns(blob.Object);
        SetUpUpload(blob, (stream, _, _) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            uploadedBody = reader.ReadToEnd();
            stream.Position = 0;
        });
        var store = new BlobClaimCheckStore(container.Object, "claim-checks/", () => Now);

        var reference = await store.PutAsync("{\"big\":\"payload\"}", new ClaimCheckPutContext("orders"));

        Assert.NotNull(requestedKey);
        Assert.Matches(new Regex(@"^claim-checks/orders/2026/08/13/[0-9a-f]{32}$"), requestedKey!);
        Assert.Equal($"azblob://claims/{requestedKey}", reference);
        Assert.Equal("{\"big\":\"payload\"}", uploadedBody);
    }

    [Fact]
    public async Task PutAsync_ForwardsTheCancellationToken()
    {
        var (container, blob) = Mocks();
        CancellationToken? forwarded = null;
        SetUpUpload(blob, (_, _, ct) => forwarded = ct);
        var store = new BlobClaimCheckStore(container.Object, now: () => Now);
        using var cts = new CancellationTokenSource();

        await store.PutAsync("body", new ClaimCheckPutContext("orders"), cts.Token);

        Assert.Equal(cts.Token, forwarded);
    }

    [Fact]
    public async Task GetAsync_RoundTripsStoredContent()
    {
        var (container, blob) = Mocks("claims");
        SetUpDownload(blob, "{\"big\":\"payload\"}");
        var store = new BlobClaimCheckStore(container.Object, "claim-checks/", () => Now);

        var body = await store.GetAsync("azblob://claims/claim-checks/orders/2026/08/13/abc123");

        Assert.Equal("{\"big\":\"payload\"}", body);
    }

    [Fact]
    public async Task GetAsync_ForwardsTheCancellationToken()
    {
        var (container, blob) = Mocks("claims");
        CancellationToken? forwarded = null;
        var result = BlobsModelFactory.BlobDownloadResult(BinaryData.FromString("x"), null!);
        blob.Setup(x => x.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => forwarded = ct)
            .ReturnsAsync(MockedResponse(result));
        var store = new BlobClaimCheckStore(container.Object, "claim-checks/", () => Now);
        using var cts = new CancellationTokenSource();

        await store.GetAsync("azblob://claims/claim-checks/orders/2026/08/13/abc123", cts.Token);

        Assert.Equal(cts.Token, forwarded);
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        var (container, blob) = Mocks("claims");
        blob.Setup(x => x.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        var store = new BlobClaimCheckStore(container.Object, "claim-checks/", () => Now);

        var body = await store.GetAsync("azblob://claims/claim-checks/orders/2026/08/13/missing");

        Assert.Null(body);
    }

    [Fact]
    public async Task GetAsync_NonNotFoundFailure_Propagates()
    {
        var (container, blob) = Mocks("claims");
        blob.Setup(x => x.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "boom"));
        var store = new BlobClaimCheckStore(container.Object, "claim-checks/", () => Now);

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            store.GetAsync("azblob://claims/claim-checks/orders/2026/08/13/abc123"));
    }

    [Fact]
    public async Task GetAsync_ForeignScheme_ThrowsMismatch()
    {
        var (container, _) = Mocks("claims");
        var store = new BlobClaimCheckStore(container.Object, "claim-checks/", () => Now);

        var ex = await Assert.ThrowsAsync<ClaimCheckStoreMismatchException>(() =>
            store.GetAsync("s3://claims/claim-checks/orders/2026/08/13/abc123"));
        Assert.Equal("s3://claims/claim-checks/orders/2026/08/13/abc123", ex.Reference);
    }

    [Fact]
    public async Task GetAsync_ForeignContainer_ThrowsMismatch()
    {
        var (container, _) = Mocks("claims");
        var store = new BlobClaimCheckStore(container.Object, "claim-checks/", () => Now);

        await Assert.ThrowsAsync<ClaimCheckStoreMismatchException>(() =>
            store.GetAsync("azblob://someone-elses-container/claim-checks/orders/2026/08/13/abc123"));
    }

    [Fact]
    public async Task GetAsync_KeyOutsideConfiguredPrefix_ThrowsMismatch()
    {
        var (container, _) = Mocks("claims");
        var store = new BlobClaimCheckStore(container.Object, "claim-checks/", () => Now);

        // Same container, well-formed key, but not under this store's configured prefix.
        await Assert.ThrowsAsync<ClaimCheckStoreMismatchException>(() =>
            store.GetAsync("azblob://claims/other-prefix/orders/2026/08/13/abc123"));
    }

    [Fact]
    public async Task PutAsync_NormalizesPrefix_WithOrWithoutTrailingSlash_ToTheSameKeyShape()
    {
        var (container1, blob1) = Mocks("claims");
        string? key1 = null;
        container1.Setup(x => x.GetBlobClient(It.IsAny<string>())).Callback<string>(k => key1 = k).Returns(blob1.Object);
        SetUpUpload(blob1);
        var store1 = new BlobClaimCheckStore(container1.Object, "claim-checks", () => Now);

        var (container2, blob2) = Mocks("claims");
        string? key2 = null;
        container2.Setup(x => x.GetBlobClient(It.IsAny<string>())).Callback<string>(k => key2 = k).Returns(blob2.Object);
        SetUpUpload(blob2);
        var store2 = new BlobClaimCheckStore(container2.Object, "claim-checks/", () => Now);

        await store1.PutAsync("body", new ClaimCheckPutContext("orders"));
        await store2.PutAsync("body", new ClaimCheckPutContext("orders"));

        Assert.StartsWith("claim-checks/orders/", key1);
        Assert.StartsWith("claim-checks/orders/", key2);
        Assert.DoesNotContain("claim-checks//", key1);
        Assert.DoesNotContain("claim-checks//", key2);
    }
}
