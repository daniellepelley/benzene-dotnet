using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Benzene.ClaimCheck;
using Benzene.ClaimCheck.Aws.S3;
using Moq;
using Xunit;

namespace Benzene.Test.ClaimCheck.S3;

public class S3ClaimCheckStoreTest
{
    private static Mock<IAmazonS3> MockS3() => new(MockBehavior.Strict);

    [Fact]
    public async Task PutAsync_IssuesKeyShapedByTopicAndDate_AndReturnsTheReference()
    {
        var s3 = MockS3();
        PutObjectRequest? put = null;
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((r, _) => put = r)
            .ReturnsAsync(new PutObjectResponse());
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket", "claim-checks/");

        var reference = await store.PutAsync("{\"a\":1}", new ClaimCheckPutContext("orders:capture"));

        Assert.Equal("my-bucket", put!.BucketName);
        Assert.Equal("{\"a\":1}", put.ContentBody);
        Assert.Equal("application/octet-stream", put.ContentType);

        // key shape: claim-checks/{topic}/{yyyy/MM/dd}/{guid}
        var today = DateTimeOffset.UtcNow;
        var expectedPrefix = $"claim-checks/orders:capture/{today:yyyy/MM/dd}/";
        Assert.StartsWith(expectedPrefix, put.Key);
        var guidPart = put.Key[expectedPrefix.Length..];
        Assert.True(Guid.TryParseExact(guidPart, "n", out _), $"expected a bare guid, got '{guidPart}'");

        Assert.Equal($"s3://my-bucket/{put.Key}", reference);
    }

    [Fact]
    public async Task PutAsync_WithNoPrefix_OmitsIt()
    {
        var s3 = MockS3();
        PutObjectRequest? put = null;
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((r, _) => put = r)
            .ReturnsAsync(new PutObjectResponse());
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket");

        await store.PutAsync("body", new ClaimCheckPutContext("orders"));

        Assert.StartsWith("orders/", put!.Key);
    }

    [Fact]
    public async Task PutAsync_ForwardsTheCancellationTokenToTheSdk()
    {
        var s3 = MockS3();
        using var cts = new CancellationTokenSource();
        CancellationToken? seen = null;
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((_, ct) => seen = ct)
            .ReturnsAsync(new PutObjectResponse());
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket");

        await store.PutAsync("body", new ClaimCheckPutContext("orders"), cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    [Fact]
    public async Task GetAsync_RoundTripsTheStoredBody()
    {
        var s3 = MockS3();
        var body = "{\"a\":1}";
        GetObjectRequest? get = null;
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<GetObjectRequest, CancellationToken>((r, _) => get = r)
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(body))
            });
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket", "claim-checks/");

        var result = await store.GetAsync("s3://my-bucket/claim-checks/orders/2026/08/13/abc123");

        Assert.Equal(body, result);
        Assert.Equal("my-bucket", get!.BucketName);
        Assert.Equal("claim-checks/orders/2026/08/13/abc123", get.Key);
    }

    [Fact]
    public async Task GetAsync_ForwardsTheCancellationTokenToTheSdk()
    {
        var s3 = MockS3();
        using var cts = new CancellationTokenSource();
        CancellationToken? seen = null;
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<GetObjectRequest, CancellationToken>((_, ct) => seen = ct)
            .ReturnsAsync(new GetObjectResponse { ResponseStream = new MemoryStream() });
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket");

        await store.GetAsync("s3://my-bucket/orders/2026/08/13/abc123", cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        var s3 = MockS3();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("NoSuchKey", ErrorType.Sender, "NoSuchKey", "req-1", HttpStatusCode.NotFound));
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket", "claim-checks/");

        var result = await store.GetAsync("s3://my-bucket/claim-checks/orders/2026/08/13/missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ForeignScheme_ThrowsMismatchWithoutCallingS3()
    {
        var s3 = MockS3();
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket", "claim-checks/");

        await Assert.ThrowsAsync<ClaimCheckStoreMismatchException>(
            () => store.GetAsync("memory://orders/abc123"));

        s3.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAsync_ForeignBucket_ThrowsMismatchWithoutCallingS3()
    {
        var s3 = MockS3();
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket", "claim-checks/");

        await Assert.ThrowsAsync<ClaimCheckStoreMismatchException>(
            () => store.GetAsync("s3://someone-elses-bucket/claim-checks/orders/2026/08/13/abc123"));

        s3.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAsync_ForeignPrefix_ThrowsMismatchWithoutCallingS3()
    {
        var s3 = MockS3();
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket", "claim-checks/");

        await Assert.ThrowsAsync<ClaimCheckStoreMismatchException>(
            () => store.GetAsync("s3://my-bucket/some-other-prefix/orders/2026/08/13/abc123"));

        s3.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAsync_NamesTheOffendingReferenceInTheException()
    {
        var s3 = MockS3();
        var store = new S3ClaimCheckStore(s3.Object, "my-bucket", "claim-checks/");
        const string reference = "s3://foreign-bucket/claim-checks/orders/2026/08/13/abc123";

        var ex = await Assert.ThrowsAsync<ClaimCheckStoreMismatchException>(() => store.GetAsync(reference));

        Assert.Equal(reference, ex.Reference);
    }
}
