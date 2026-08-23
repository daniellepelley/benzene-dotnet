using System.Threading.Tasks;
using Amazon.Lambda.S3Events;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.S3;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.S3;

public class S3BenzeneInvocationExtensionsTest
{
    [Fact]
    public async Task UseS3_RealEntryPoint_ResolvesIBenzeneInvocationInsideTheRecordPipeline()
    {
        // Coverage gap closed: the two tests above exercise UseBenzeneInvocation() directly on a bare
        // S3RecordContext builder, never going through the real UseS3(...) entry point an application
        // actually calls (which wires it up via CreateMiddlewarePipeline + app.Register(...) against the
        // OUTER AwsEventStreamContext-level container). This test goes through that real entry point -
        // AwsEventStreamContext -> S3LambdaHandler -> S3Application's per-record scope -> the record
        // pipeline - to prove the wiring actually holds end-to-end, not just in isolation.
        IBenzeneInvocation resolved = null;

        var services = new ServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));
        app.UseS3(s3 => s3
            .Use(null, (resolver, _, next) =>
            {
                resolved = resolver.GetService<IBenzeneInvocation>();
                return next();
            })
        );

        var s3Event = new S3Event
        {
            Records =
            [
                new S3Event.S3EventNotificationRecord
                {
                    EventSource = "aws:s3",
                    S3 = new S3Event.S3Entity { Object = new S3Event.S3ObjectEntity { Key = "object-1" } },
                    ResponseElements = new S3Event.ResponseElementsEntity { XAmzRequestId = "s3-req-e2e" },
                }
            ]
        };

        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();
        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(s3Event), resolver);

        Assert.NotNull(resolved);
        Assert.Equal("s3-req-e2e", resolved.InvocationId);
        Assert.Equal("AwsLambda", resolved.Platform);
    }

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
