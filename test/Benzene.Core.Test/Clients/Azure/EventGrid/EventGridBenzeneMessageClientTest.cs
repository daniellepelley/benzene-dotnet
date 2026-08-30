using System;
using System.Threading.Tasks;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Clients.Azure.EventGrid;
using Benzene.Core.Middleware;
using Benzene.Results;
using Benzene.Test.Logging.Helpers;
using Xunit;

namespace Benzene.Test.Clients.Azure.EventGrid;

public class EventGridBenzeneMessageClientTest
{
    [Fact]
    public void Constructor_PrebuiltPipelineOverload_NullLogger_ThrowsImmediately()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EventGridBenzeneMessageClient(
                "some-source",
                (IMiddlewarePipeline<EventGridSendMessageContext>)null!,
                null!,
                new NullServiceResolver()));
    }

    [Fact]
    public async Task SendMessageAsync_FailingSend_LogsThroughTheErrorPathWithoutThrowing()
    {
        var pipeline = new MiddlewarePipelineBuilder<EventGridSendMessageContext>(new NullBenzeneServiceContainer())
            .Use((context, next) => throw new Exception("boom"))
            .Build();

        var collector = new FakeLogCollector();
        var logger = new FakeLogger<EventGridBenzeneMessageClient>(collector);

        var client = new EventGridBenzeneMessageClient("some-source", pipeline, logger, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Contains(collector.Entries, e => e.Exception?.Message == "boom");
    }
}
