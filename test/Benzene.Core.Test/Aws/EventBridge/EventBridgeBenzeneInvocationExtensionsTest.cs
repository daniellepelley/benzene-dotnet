using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.EventBridge;
using Benzene.Aws.Lambda.EventBridge.TestHelpers;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.EventBridge;

public class EventBridgeBenzeneInvocationExtensionsTest
{
    [Fact]
    public async Task UseEventBridge_RealEntryPoint_ResolvesIBenzeneInvocationInsideThePipeline()
    {
        // Coverage gap closed: the test below exercises UseBenzeneInvocation() directly on a bare
        // EventBridgeContext builder, never going through the real UseEventBridge(...) entry point an
        // application actually calls (which wires it up via CreateMiddlewarePipeline + app.Register(...)
        // against the OUTER AwsEventStreamContext-level container). This test goes through that real
        // entry point - AwsEventStreamContext -> EventBridgeLambdaHandler -> EventBridgeApplication's
        // per-event scope -> the pipeline - to prove the wiring actually holds end-to-end.
        IBenzeneInvocation resolved = null;

        var services = new ServiceCollection();
        var app = new MiddlewarePipelineBuilder<AwsEventStreamContext>(new MicrosoftBenzeneServiceContainer(services));
        app.UseEventBridge(eventBridge => eventBridge
                .Use(null, (resolver, _, next) =>
                {
                    resolved = resolver.GetService<IBenzeneInvocation>();
                    return next();
                }),
            // This test is about invocation-id/platform resolution, not message routing - the inline
            // middleware never sets a MessageResult, so escalating on that (#229's null-result fix)
            // would be unrelated noise here.
            options => options.RaiseOnFailureStatus = false
        );

        var request = MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsEventBridge();

        using var factory = new MicrosoftServiceResolverFactory(services);
        using var resolver = factory.CreateScope();
        await app.Build().HandleAsync(AwsEventStreamContextBuilder.Build(request), resolver);

        Assert.NotNull(resolved);
        Assert.Equal(request.Id, resolved.InvocationId);
        Assert.Equal("AwsLambda", resolved.Platform);
    }

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
