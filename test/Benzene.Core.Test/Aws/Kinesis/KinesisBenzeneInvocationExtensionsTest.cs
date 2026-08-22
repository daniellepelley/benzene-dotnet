using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.Kinesis;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.Kinesis;

public class KinesisBenzeneInvocationExtensionsTest
{
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
