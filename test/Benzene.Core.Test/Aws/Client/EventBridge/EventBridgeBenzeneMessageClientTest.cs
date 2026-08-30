using System;
using System.Threading.Tasks;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Clients.Aws.EventBridge;
using Benzene.Core.Middleware;
using Benzene.Results;
using Benzene.Test.Logging.Helpers;
using Xunit;

namespace Benzene.Test.Aws.Client.EventBridge;

public class EventBridgeBenzeneMessageClientTest
{
    [Fact]
    public void Constructor_PrebuiltPipelineOverload_NullLogger_ThrowsImmediately()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EventBridgeBenzeneMessageClient(
                "some-source",
                (IMiddlewarePipeline<EventBridgeSendMessageContext>)null!,
                null!,
                new NullServiceResolver()));
    }

    [Fact]
    public async Task SendMessageAsync_FailingSend_LogsThroughTheErrorPathWithoutThrowing()
    {
        var pipeline = new MiddlewarePipelineBuilder<EventBridgeSendMessageContext>(new NullBenzeneServiceContainer())
            .Use((context, next) => throw new Exception("boom"))
            .Build();

        var collector = new FakeLogCollector();
        var logger = new FakeLogger<EventBridgeBenzeneMessageClient>(collector);

        var client = new EventBridgeBenzeneMessageClient("some-source", pipeline, logger, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Contains(collector.Entries, e => e.Exception?.Message == "boom");
    }
}
