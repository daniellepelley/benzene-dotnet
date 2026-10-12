using System.Threading.Tasks;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Clients;

public class OutboundRoutingBuilderTest
{
    private static OutboundRoutingBuilder CreateBuilder()
    {
        return new OutboundRoutingBuilder(new MicrosoftBenzeneServiceContainer(new ServiceCollection()));
    }

    [Fact]
    public void Build_DistinctTopics_ReturnsOnePipelinePerTopic()
    {
        var builder = CreateBuilder();

        builder
            .Route("order:create", pipeline => pipeline.OnRequest(_ => { }))
            .Route("audit:log", pipeline => pipeline.OnRequest(_ => { }));

        var routes = builder.Build();

        Assert.Equal(2, routes.Count);
        Assert.Contains("order:create", routes.Keys);
        Assert.Contains("audit:log", routes.Keys);
    }

    [Fact]
    public void Build_DuplicateTopic_ThrowsDuplicateOutboundRouteException()
    {
        var builder = CreateBuilder();

        builder
            .Route("order:create", pipeline => pipeline.OnRequest(_ => { }))
            .Route("order:create", pipeline => pipeline.OnRequest(_ => { }));

        var exception = Assert.Throws<DuplicateOutboundRouteException>(() => builder.Build());
        Assert.Equal("order:create", exception.Topic);
    }

    [Fact]
    public void Build_NoRoutes_ReturnsEmptyTable()
    {
        var builder = CreateBuilder();

        var routes = builder.Build();

        Assert.Empty(routes);
    }

    [Fact]
    public async Task UseOnEveryRoute_RunsAheadOfEveryRoutesOwnMiddleware_WhicheverOrderTheyWereRegisteredIn()
    {
        var builder = CreateBuilder();
        var calls = new System.Collections.Generic.List<string>();

        builder
            .Route("order:create", pipeline => pipeline.OnRequest(ctx => calls.Add($"{ctx.Topic}:own")))
            .UseOnEveryRoute(pipeline => pipeline.OnRequest(ctx => calls.Add($"{ctx.Topic}:every")))
            .Route("audit:log", pipeline => pipeline.OnRequest(ctx => calls.Add($"{ctx.Topic}:own")));

        var routes = builder.Build();
        // Building twice must not stack the every-route middleware twice.
        routes = builder.Build();
        var resolver = new MicrosoftServiceResolverAdapter(new ServiceCollection().BuildServiceProvider());
        await routes["order:create"].HandleAsync(new OutboundContext("order:create", "x"), resolver);
        await routes["audit:log"].HandleAsync(new OutboundContext("audit:log", "x"), resolver);

        Assert.Equal(new[] { "order:create:every", "order:create:own", "audit:log:every", "audit:log:own" }, calls);
    }
}
