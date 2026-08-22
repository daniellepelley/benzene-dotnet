using System.Threading.Tasks;
using Amazon.Lambda.S3Events;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.S3;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.S3;

public class S3BenzeneInvocationExtensionsTest
{
    [Fact]
    public async Task UseBenzeneInvocation_SetsInvocationIdToXAmzRequestId()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        var builder = new MiddlewarePipelineBuilder<S3RecordContext>(container);
        builder.UseBenzeneInvocation();
        builder.Use((_, next) => next());

        var pipeline = builder.Build();
        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();

        var s3Event = new S3Event
        {
            Records = new System.Collections.Generic.List<S3Event.S3EventNotificationRecord>
            {
                new()
                {
                    EventSource = "aws:s3",
                    S3 = new S3Event.S3Entity { Object = new S3Event.S3ObjectEntity { Key = "object-1" } },
                    ResponseElements = new S3Event.ResponseElementsEntity { XAmzRequestId = "s3-req-789" },
                }
            }
        };
        var context = S3RecordContext.CreateInstance(s3Event, s3Event.Records[0]);

        await pipeline.HandleAsync(context, resolver);
        var resolved = resolver.GetService<IBenzeneInvocation>();

        Assert.Equal("s3-req-789", resolved.InvocationId);
        Assert.Equal("AwsLambda", resolved.Platform);
    }

    [Fact]
    public async Task UseBenzeneInvocation_NoResponseElements_FallsBackToObjectKey()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        var builder = new MiddlewarePipelineBuilder<S3RecordContext>(container);
        builder.UseBenzeneInvocation();
        builder.Use((_, next) => next());

        var pipeline = builder.Build();
        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();

        var s3Event = new S3Event
        {
            Records = new System.Collections.Generic.List<S3Event.S3EventNotificationRecord>
            {
                new()
                {
                    EventSource = "aws:s3",
                    S3 = new S3Event.S3Entity { Object = new S3Event.S3ObjectEntity { Key = "object-2" } },
                }
            }
        };
        var context = S3RecordContext.CreateInstance(s3Event, s3Event.Records[0]);

        await pipeline.HandleAsync(context, resolver);
        var resolved = resolver.GetService<IBenzeneInvocation>();

        Assert.Equal("object-2", resolved.InvocationId);
    }
}
