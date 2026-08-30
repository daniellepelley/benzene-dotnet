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
                }),
            // This test is about the test-helper-built event reaching the pipeline, not message
            // routing - the inline middleware never sets a MessageResult, so escalating on that
            // (#229's null-result fix) would be unrelated noise here.
            configure: options => options.RaiseOnFailureStatus = false
        );

        var s3Event = MessageBuilder.Create("ObjectCreated:Put", Defaults.MessageAsObject).AsS3(bucketName: "my-bucket", key: "my-key");

        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(s3Event), new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()));

        Assert.NotNull(capturedContext);
        Assert.Equal("ObjectCreated:Put", capturedContext.S3EventNotificationRecord.EventName);
        Assert.Equal("my-bucket", capturedContext.S3EventNotificationRecord.S3.Bucket.Name);
        Assert.Equal("my-key", capturedContext.S3EventNotificationRecord.S3.Object.Key);
    }
}
