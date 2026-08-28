using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Benzene.Clients;
using Benzene.Clients.Azure.EventGrid;
using Benzene.Core.Middleware;
using Benzene.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Azure.EventGrid;

/// <summary>
/// WP-I coverage debt: <see cref="EventGridBenzeneMessageClient"/> had zero direct unit tests before
/// this (see <c>work/bug-fix-rulings-round14-15-2026-08.md</c> WP-I). Mirrors the conventions in
/// <c>SnsBenzeneMessageClientTest</c> - success path, transport-throws-and-is-mapped-correctly, and
/// null-logger-doesn't-throw (the #266 fix).
/// </summary>
public class EventGridBenzeneMessageClientTest
{
    private const string Source = "my-service";

    [Fact]
    public async Task SendMessageAsync_PublishSucceeds_ReturnsAccepted()
    {
        var mockPublisher = new Mock<EventGridPublisherClient>();
        mockPublisher
            .Setup(x => x.SendEventAsync(It.IsAny<CloudEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response)null!);

        var client = new EventGridBenzeneMessageClient(Source, mockPublisher.Object,
            NullLogger<EventGridBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_ThrowingPublisher_ReturnsServiceUnavailable()
    {
        var mockPublisher = new Mock<EventGridPublisherClient>();
        mockPublisher
            .Setup(x => x.SendEventAsync(It.IsAny<CloudEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var client = new EventGridBenzeneMessageClient(Source, mockPublisher.Object,
            NullLogger<EventGridBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Contains(result.Errors, e => e.Message.Contains("boom"));
    }

    // #266: a null logger must not make the catch block's own LogError throw and mask the real
    // publish failure.
    [Fact]
    public async Task SendMessageAsync_NullLogger_DoesNotThrow_AndStillReturnsServiceUnavailable()
    {
        var mockPublisher = new Mock<EventGridPublisherClient>();
        mockPublisher
            .Setup(x => x.SendEventAsync(It.IsAny<CloudEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var client = new EventGridBenzeneMessageClient(Source, mockPublisher.Object,
            logger: null!, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_PrebuiltPipeline_PublishSucceeds_ReturnsAccepted()
    {
        var mockPublisher = new Mock<EventGridPublisherClient>();
        mockPublisher
            .Setup(x => x.SendEventAsync(It.IsAny<CloudEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response)null!);

        var pipeline = new MiddlewarePipelineBuilder<EventGridSendMessageContext>(new NullBenzeneServiceContainer())
            .UseEventGridClient(mockPublisher.Object)
            .Build();

        var client = new EventGridBenzeneMessageClient(Source, pipeline,
            NullLogger<EventGridBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }
}
