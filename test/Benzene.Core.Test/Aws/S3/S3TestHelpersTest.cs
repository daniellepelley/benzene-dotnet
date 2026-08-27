using System.Threading.Tasks;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.S3;
using Benzene.Aws.Lambda.S3.TestHelpers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.S3;

public class S3TestHelpersTest
{
    [Fact]
    public async Task AsS3_BuildsAnEventThatRoutesThroughTheS3Pipeline()
    {
        S3RecordContext capturedContext = null;
        var services = ServiceResolverMother.CreateServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));

        app.UseS3(message => message
            .Use(null, (context, next) =>
            {
                capturedContext = context;
                return next();
            })
        );

        var s3Event = MessageBuilder.Create("ObjectCreated:Put", Defaults.MessageAsObject).AsS3(bucketName: "my-bucket", key: "my-key");

        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(s3Event), new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()));

        Assert.NotNull(capturedContext);
        Assert.Equal("ObjectCreated:Put", capturedContext.S3EventNotificationRecord.EventName);
        Assert.Equal("my-bucket", capturedContext.S3EventNotificationRecord.S3.Bucket.Name);
        Assert.Equal("my-key", capturedContext.S3EventNotificationRecord.S3.Object.Key);
    }

    // #191: a key containing a reserved character (here the exact repro from the finding, plus a
    // '%' case) must come back out of the real getter unchanged - AsS3 has to URL-encode it going
    // in, mirroring what a real S3 event notification does, so S3ObjectKeyCodec.Decode (which
    // S3MessageBodyGetter runs on every read since #158) doesn't corrupt it.
    [Theory]
    [InlineData("invoice+2024-08-27.pdf")]
    [InlineData("100% done.txt")]
    public async Task AsS3_KeyWithReservedCharacters_RoundTripsThroughTheRealGetter(string rawKey)
    {
        S3RecordContext capturedContext = null;
        var services = ServiceResolverMother.CreateServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));

        app.UseS3(message => message
            .Use(null, (context, next) =>
            {
                capturedContext = context;
                return next();
            })
        );

        var s3Event = MessageBuilder.Create("ObjectCreated:Put", Defaults.MessageAsObject).AsS3(key: rawKey);

        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(s3Event), new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()));

        // The record now carries the wire-encoded form, not the plain key.
        Assert.NotEqual(rawKey, capturedContext.S3EventNotificationRecord.S3.Object.Key);

        var body = new S3MessageBodyGetter().GetBody(capturedContext);

        Assert.Contains($"\"key\":\"{rawKey}\"", body);
    }
}
