using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.EventBridge;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.EventBridge;

public class EventBridgeBenzeneInvocationExtensionsTest
{
    [Fact]
    public async Task UseBenzeneInvocation_SetsInvocationIdToEventId()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        var builder = new MiddlewarePipelineBuilder<EventBridgeContext>(container);
        builder.UseBenzeneInvocation();
        builder.Use((_, next) => next());

        var pipeline = builder.Build();
        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();

        var context = new EventBridgeContext(new EventBridgeEvent { Id = "eb-evt-789" });

        await pipeline.HandleAsync(context, resolver);
        var resolved = resolver.GetService<IBenzeneInvocation>();

        Assert.Equal("eb-evt-789", resolved.InvocationId);
        Assert.Equal("AwsLambda", resolved.Platform);
    }
}
