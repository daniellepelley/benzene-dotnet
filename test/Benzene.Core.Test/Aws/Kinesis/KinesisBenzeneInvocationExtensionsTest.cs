using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.Kinesis;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.Kinesis;

public class KinesisBenzeneInvocationExtensionsTest
{
    [Fact]
    public async Task UseKinesisStream_RealEntryPoint_ResolvesIBenzeneInvocationInsideThePipeline()
    {
        // Coverage gap closed: the test below exercises UseBenzeneInvocation() directly on a bare
        // StreamContext<KinesisEventRecord> builder, never going through the real
        // UseKinesisStream(...) entry point an application actually calls (which wires it up via
        // CreateMiddlewarePipeline + app.Register(...) against the OUTER AwsEventStreamContext-level
        // container). This test goes through that real entry point - AwsEventStreamContext ->
        // KinesisLambdaHandler -> KinesisStreamApplication's per-batch scope -> the stream pipeline -
        // to prove the wiring actually holds end-to-end.
        IBenzeneInvocation resolved = null;

        var services = new ServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));
        app.UseKinesisStream(kinesis => kinesis
            .Use(null, (resolver, _, next) =>
            {
                resolved = resolver.GetService<IBenzeneInvocation>();
                return next();
            })
            .UseStream<KinesisEventRecord>((_, _) => Task.CompletedTask)
        );

        var kinesisEvent = new KinesisEvent
        {
            Records = new List<KinesisEventRecord>
            {
                new()
                {
                    EventSource = "aws:kinesis",
                    EventId = "shardId-000000000000:1",
                    Kinesis = new KinesisRecordData
                    {
                        PartitionKey = "pk1",
                        SequenceNumber = "1",
                        Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("one"))
                    }
                }
            }
        };

        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();
        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(kinesisEvent), resolver);

        Assert.NotNull(resolved);
        Assert.False(string.IsNullOrEmpty(resolved.InvocationId));
        Assert.Equal("AwsLambda", resolved.Platform);
    }

    [Fact]
    public async Task UseBenzeneInvocation_ResolvesInsideTheStreamPipeline()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        var builder = new MiddlewarePipelineBuilder<StreamContext<KinesisEventRecord>>(container);
        builder.UseBenzeneInvocation();
        builder.Use((_, next) => next());

        var pipeline = builder.Build();
        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();

        var context = new StreamContext<KinesisEventRecord>(EmptyRecords());

        await pipeline.HandleAsync(context, resolver);
        var resolved = resolver.GetService<IBenzeneInvocation>();

        Assert.False(string.IsNullOrEmpty(resolved.InvocationId));
        Assert.Equal("AwsLambda", resolved.Platform);
    }

    private static async System.Collections.Generic.IAsyncEnumerable<KinesisEventRecord> EmptyRecords()
    {
        yield break;
    }
}
