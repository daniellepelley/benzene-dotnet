using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Clients.Aws.Sns;
using Benzene.Core.Middleware;
using Benzene.Results;
using Benzene.Test.Logging.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Aws.Client.Sns;

public class SnsBenzeneMessageClientTest
{
    [Fact]
    public void Constructor_NullLogger_ThrowsImmediately()
    {
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();

        Assert.Throws<ArgumentNullException>(() =>
            new SnsBenzeneMessageClient("some-topic-arn", mockSnsClient.Object, null!, new NullServiceResolver()));
    }

    [Fact]
    public void Constructor_PrebuiltPipelineOverload_NullLogger_ThrowsImmediately()
    {
        var pipeline = new MiddlewarePipelineBuilder<SnsSendMessageContext>(new NullBenzeneServiceContainer()).Build();

        Assert.Throws<ArgumentNullException>(() =>
            new SnsBenzeneMessageClient("some-topic-arn", pipeline, null!, new NullServiceResolver()));
    }

    [Fact]
    public async Task SendMessageAsync_ThrowingClient_LogsThroughTheErrorPathWithoutThrowing()
    {
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var collector = new FakeLogCollector();
        var logger = new FakeLogger<SnsBenzeneMessageClient>(collector);

        var client = new SnsBenzeneMessageClient("some-topic-arn", mockSnsClient.Object, logger, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        Assert.Contains(collector.Entries, e => e.Exception?.Message == "boom");
    }

    [Fact]
    public async Task SendMessageAsync_OkResponse_ReturnsAccepted()
    {
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { HttpStatusCode = HttpStatusCode.OK });

        var client = new SnsBenzeneMessageClient("some-topic-arn", mockSnsClient.Object, NullLogger<SnsBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_NonOkResponse_ReturnsMappedResult()
    {
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { HttpStatusCode = HttpStatusCode.BadRequest });

        var client = new SnsBenzeneMessageClient("some-topic-arn", mockSnsClient.Object, NullLogger<SnsBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.NotEqual(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_ThrowingClient_ReturnsServiceUnavailable()
    {
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var client = new SnsBenzeneMessageClient("some-topic-arn", mockSnsClient.Object, NullLogger<SnsBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_PrebuiltPipeline_OkResponse_ReturnsAccepted()
    {
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { HttpStatusCode = HttpStatusCode.OK });

        var pipeline = new MiddlewarePipelineBuilder<SnsSendMessageContext>(new NullBenzeneServiceContainer())
            .UseSnsClient(mockSnsClient.Object)
            .Build();

        var client = new SnsBenzeneMessageClient("some-topic-arn", pipeline, NullLogger<SnsBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }
}
