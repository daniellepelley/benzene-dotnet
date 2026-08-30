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

    [Theory]
    [InlineData("invoice+2024-08-27.pdf")]
    [InlineData("reports/100%-done.csv")]
    [InlineData("résumé/naïve-café.pdf")]
    public async Task AsS3_ReservedOrUnicodeCharactersInKey_RoundTripThroughTheRealProductionGetters(string realKey)
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

        var s3Event = MessageBuilder.Create("ObjectCreated:Put", Defaults.MessageAsObject).AsS3(bucketName: "my-bucket", key: realKey);

        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(s3Event), new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()));

        Assert.NotNull(capturedContext);

        // The record itself carries the wire-encoded form (matching a real S3 event notification) -
        // decoding it must recover the exact original key, with no corruption from '+', '%', or
        // non-ASCII characters.
        var decodedFromRecord = S3ObjectKeyCodec.Decode(capturedContext.S3EventNotificationRecord.S3.Object.Key);
        Assert.Equal(realKey, decodedFromRecord);

        // And the real production getters used by handlers must observe the same round-trip.
        var body = new S3MessageBodyGetter().GetBody(capturedContext);
        Assert.Contains($"\"key\":\"{realKey}\"", body);

        var headers = new S3MessageHeadersGetter().GetHeaders(capturedContext);
        Assert.Equal(realKey, headers["key"]);
    }
}
