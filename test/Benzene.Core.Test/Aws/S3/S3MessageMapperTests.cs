using Amazon.Lambda.S3Events;
using Benzene.Aws.Lambda.S3;
using Xunit;

namespace Benzene.Test.Aws.S3;

public class S3MessageMapperTests
{
    private static S3RecordContext CreateContext(string eventName)
    {
        var record = new S3Event.S3EventNotificationRecord
        {
            EventName = eventName
        };
        return S3RecordContext.CreateInstance(new S3Event { Records = [record] }, record);
    }

    private static S3RecordContext CreateContextWithKey(string rawKey)
    {
        var record = new S3Event.S3EventNotificationRecord
        {
            EventName = "ObjectCreated:Put",
            S3 = new S3Event.S3Entity
            {
                Bucket = new S3Event.S3BucketEntity { Name = "benzene-test-bucket" },
                Object = new S3Event.S3ObjectEntity { Key = rawKey }
            }
        };
        return S3RecordContext.CreateInstance(new S3Event { Records = [record] }, record);
    }

    [Fact]
    public void GetTopic_UsesTheS3EventName()
    {
        var topic = new S3MessageTopicGetter().GetTopic(CreateContext("ObjectCreated:Put"));

        Assert.Equal("ObjectCreated:Put", topic.Id);
    }

    [Fact]
    public void GetBody_IncludesTheEventName()
    {
        var body = new S3MessageBodyGetter().GetBody(CreateContext("ObjectCreated:Put"));

        Assert.Contains("ObjectCreated:Put", body);
    }

    [Fact]
    public void GetHeaders_IncludesTheEventName()
    {
        var headers = new S3MessageHeadersGetter().GetHeaders(CreateContext("ObjectCreated:Put"));

        Assert.Equal("ObjectCreated:Put", headers["eventName"]);
    }

    [Fact]
    public void GetHeaders_NoS3Entity_OmitsBucketAndKeyHeaders()
    {
        // The record's S3 property (bucket/object metadata) is null unless explicitly populated -
        // GetHeaders chains through it with null-conditional access, so this proves it degrades to
        // omitting those headers rather than throwing.
        var headers = new S3MessageHeadersGetter().GetHeaders(CreateContext("ObjectCreated:Put"));

        Assert.False(headers.ContainsKey("bucketName"));
        Assert.False(headers.ContainsKey("key"));
    }

    [Fact]
    public void GetBody_NoS3Entity_SerializesWithoutThrowing()
    {
        var body = new S3MessageBodyGetter().GetBody(CreateContext("ObjectCreated:Put"));

        Assert.Contains("\"bucketName\":null", body);
        Assert.Contains("\"key\":null", body);
    }

    [Fact]
    public void GetBody_UrlEncodedKey_IsDecoded()
    {
        // S3 event notifications URL-encode the key (space -> '+', reserved chars percent-encoded).
        // GetObjectAsync needs the decoded form, or it returns NoSuchKey.
        var body = new S3MessageBodyGetter().GetBody(CreateContextWithKey("my+folder/file+with+spaces%26more.txt"));

        Assert.Contains("\"key\":\"my folder/file with spaces&more.txt\"", body);
    }

    [Fact]
    public void GetHeaders_UrlEncodedKey_KeyHeaderIsDecodedAndKeyRawPreservesEncoding()
    {
        var headers = new S3MessageHeadersGetter().GetHeaders(CreateContextWithKey("my+folder/file+with+spaces%26more.txt"));

        Assert.Equal("my folder/file with spaces&more.txt", headers["key"]);
        Assert.Equal("my+folder/file+with+spaces%26more.txt", headers["keyRaw"]);
    }
}
