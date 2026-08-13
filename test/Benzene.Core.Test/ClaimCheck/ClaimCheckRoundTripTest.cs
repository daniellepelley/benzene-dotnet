using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.ClaimCheck;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.ClaimCheck;

// End-to-end: offload through a real MiddlewarePipelineBuilder<OutboundContext>, then hydrate
// through a real pipeline against a fake SqsMessageContext-style transport context, both wired via
// the real DI container (not hand-rolled fakes) with InMemoryClaimCheckStore as the shared store -
// proving the body an IClaimCheckStore.PutAsync actually stored is exactly what the hydrated
// transport context ends up with.
public class ClaimCheckRoundTripTest
{
    private class BigRequest
    {
        public string Payload { get; set; } = new string('x', 300 * 1024);
    }

    // A stand-in for a Lambda-event-backed transport context (e.g. SqsMessageContext): headers and a
    // mutable raw body, exactly the shape IMessageHeadersGetter<TContext>/IMessageBodySetter<TContext>
    // exist to abstract over.
    private class FakeTransportContext
    {
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
        public string? Body { get; set; }
    }

    private class FakeTransportHeadersGetter : IMessageHeadersGetter<FakeTransportContext>
    {
        public IDictionary<string, string> GetHeaders(FakeTransportContext context) => context.Headers;
    }

    private class FakeTransportBodySetter : IMessageBodySetter<FakeTransportContext>
    {
        public Task SetBody(FakeTransportContext context, string body)
        {
            context.Body = body;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task OffloadThenHydrate_BodyIsIdenticalEndToEnd()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddInMemoryClaimCheckStore();
        container.AddScoped<IMessageHeadersGetter<FakeTransportContext>, FakeTransportHeadersGetter>();
        container.AddScoped<IMessageBodySetter<FakeTransportContext>, FakeTransportBodySetter>();

        var outboundBuilder = new MiddlewarePipelineBuilder<OutboundContext>(container);
        outboundBuilder.UseClaimCheck();
        var outboundPipeline = outboundBuilder.Build();

        var inboundBuilder = new MiddlewarePipelineBuilder<FakeTransportContext>(container);
        inboundBuilder.UseClaimCheck<FakeTransportContext>();
        var inboundPipeline = inboundBuilder.Build();

        using var factory = new MicrosoftServiceResolverFactory(services);

        var request = new BigRequest();
        var expectedBody = new JsonSerializer().Serialize(request);

        var outboundContext = new OutboundContext("orders:big-payload", request);
        using (var outboundResolver = factory.CreateScope())
        {
            await outboundPipeline.HandleAsync(outboundContext, outboundResolver);
        }

        // The request was actually offloaded: a reference header, and a placeholder request.
        Assert.True(outboundContext.Headers.ContainsKey(ClaimCheckHeaders.ClaimCheck));
        var reference = outboundContext.Headers[ClaimCheckHeaders.ClaimCheck];
        Assert.IsType<ClaimCheckPlaceholder>(outboundContext.Request);

        var inboundContext = new FakeTransportContext();
        inboundContext.Headers[ClaimCheckHeaders.ClaimCheck] = reference;

        using (var inboundResolver = factory.CreateScope())
        {
            await inboundPipeline.HandleAsync(inboundContext, inboundResolver);
        }

        Assert.Equal(expectedBody, inboundContext.Body);
    }
}
