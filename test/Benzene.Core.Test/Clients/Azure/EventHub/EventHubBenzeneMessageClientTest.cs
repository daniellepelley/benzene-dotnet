using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Benzene.Clients;
using Benzene.Clients.Azure.EventHub;
using Benzene.Core.Middleware;
using Benzene.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Azure.EventHub;

/// <summary>
/// WP-I coverage debt: <see cref="EventHubBenzeneMessageClient"/> had zero direct unit tests before
/// this (see <c>work/bug-fix-rulings-round14-15-2026-08.md</c> WP-I). Mirrors the conventions in
/// <c>SnsBenzeneMessageClientTest</c> - success path, transport-throws-and-is-mapped-correctly, and
/// null-logger-doesn't-throw (the #266 fix).
/// </summary>
public class EventHubBenzeneMessageClientTest
{
    private static EventDataBatch CapacityBatch(int capacity, CreateBatchOptions options)
    {
        var store = new List<EventData>();
        return EventHubsModelFactory.EventDataBatch(256 * 1024, store, options, _ => store.Count < capacity);
    }

    private static Mock<EventHubProducerClient> MockProducer(Exception? sendThrows = null)
    {
        var mockProducer = new Mock<EventHubProducerClient>();
        mockProducer
            .Setup(x => x.CreateBatchAsync(It.IsAny<CreateBatchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateBatchOptions o, CancellationToken _) => CapacityBatch(10, o));

        var sendSetup = mockProducer.Setup(x => x.SendAsync(It.IsAny<EventDataBatch>(), It.IsAny<CancellationToken>()));
        if (sendThrows is not null)
        {
            sendSetup.ThrowsAsync(sendThrows);
        }
        else
        {
            sendSetup.Returns(Task.CompletedTask);
        }

        return mockProducer;
    }

    [Fact]
    public async Task SendMessageAsync_SendSucceeds_ReturnsAccepted()
    {
        var client = new EventHubBenzeneMessageClient(MockProducer().Object,
            NullLogger<EventHubBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_ThrowingProducer_ReturnsServiceUnavailable()
    {
        var client = new EventHubBenzeneMessageClient(MockProducer(new InvalidOperationException("boom")).Object,
            NullLogger<EventHubBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Contains(result.Errors, e => e.Message.Contains("boom"));
    }

    // #266: a null logger must not make the catch block's own LogError throw and mask the real
    // send failure.
    [Fact]
    public async Task SendMessageAsync_NullLogger_DoesNotThrow_AndStillReturnsServiceUnavailable()
    {
        var client = new EventHubBenzeneMessageClient(MockProducer(new InvalidOperationException("boom")).Object,
            logger: null!, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_PrebuiltPipeline_SendSucceeds_ReturnsAccepted()
    {
        var pipeline = new MiddlewarePipelineBuilder<EventHubSendMessageContext>(new NullBenzeneServiceContainer())
            .UseEventHubClient(MockProducer().Object)
            .Build();

        var client = new EventHubBenzeneMessageClient(pipeline,
            NullLogger<EventHubBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }
}
