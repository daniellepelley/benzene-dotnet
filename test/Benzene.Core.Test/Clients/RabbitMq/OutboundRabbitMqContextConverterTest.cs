using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Clients;
using Benzene.Microsoft.Dependencies;
using Benzene.RabbitMq.RabbitMqSendMessage;
using Benzene.Results;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json;
using RabbitMQ.Client;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients.RabbitMq;

/// <summary>
/// Covers the <c>OutboundContext</c> overloads of <c>UseRabbitMq</c> - the ones that let an outbound
/// route (<c>AddOutboundRouting(...).Route(...)</c>) terminate in a RabbitMQ publish without a
/// hand-written terminal middleware.
/// </summary>
public class OutboundRabbitMqContextConverterTest
{
    private static Mock<IChannel> PublishingChannel()
    {
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(x => x.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        return mockChannel;
    }

    private static IBenzeneMessageSender SenderFor(Action<OutboundRoutingBuilder> configure)
    {
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(configure);
        return new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()).GetService<IBenzeneMessageSender>();
    }

    [Fact]
    public async Task SendAsync_RoutedThroughUseRabbitMq_PublishesAndReturnsAcceptedVoidResult()
    {
        var mockChannel = PublishingChannel();
        var sender = SenderFor(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseRabbitMq(mockChannel.Object)));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        mockChannel.Verify(x => x.BasicPublishAsync(
            "",
            Defaults.Topic,
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.Is<ReadOnlyMemory<byte>>(body =>
                JsonConvert.DeserializeObject<ExampleRequestPayload>(Encoding.UTF8.GetString(body.ToArray())).Name == "foo"),
            It.IsAny<CancellationToken>()));

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendAsync_ForwardsHeadersAndTheTopicOntoBasicProperties()
    {
        BasicProperties captured = null;
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(x => x.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, bool _, BasicProperties props, ReadOnlyMemory<byte> _, CancellationToken _) => captured = props)
            .Returns(ValueTask.CompletedTask);

        var sender = SenderFor(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseRabbitMq(mockChannel.Object, "some-exchange")));

        await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" },
            new Dictionary<string, string> { { "tenantId", "tenant-1" } });

        Assert.NotNull(captured);
        Assert.Equal("tenant-1", Encoding.UTF8.GetString((byte[])captured.Headers!["tenantId"]!));
        Assert.Equal(Defaults.Topic, Encoding.UTF8.GetString((byte[])captured.Headers!["topic"]!));
    }

    [Fact]
    public async Task SendAsync_WithCustomTopicHeaderKey_WritesTheTopicToThatHeader()
    {
        BasicProperties captured = null;
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(x => x.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, bool _, BasicProperties props, ReadOnlyMemory<byte> _, CancellationToken _) => captured = props)
            .Returns(ValueTask.CompletedTask);

        var sender = SenderFor(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseRabbitMq(mockChannel.Object, topicHeaderKey: "x-my-topic")));

        await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.NotNull(captured);
        Assert.Equal(Defaults.Topic, Encoding.UTF8.GetString((byte[])captured.Headers!["x-my-topic"]!));
        Assert.False(captured.Headers!.ContainsKey("topic"));
    }

    [Fact]
    public async Task SendAsync_ThroughTheExplicitInnerPipelineOverload_PublishesToTheGivenExchange()
    {
        // The rung below the channel shorthand: the caller configures the inner publish pipeline, so
        // publish flags (mandatory/persistent) and extra middleware are available. mandatory: true
        // requires a publisher-confirms-enabled channel (see RabbitMqMandatoryPublishCoordinator) and
        // waits for the broker's ack/return, so the mock has to supply both.
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(x => x.GetNextPublishSequenceNumberAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1UL);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback(() => mockChannel.Raise(x => x.BasicAcksAsync += null, mockChannel.Object,
                new RabbitMQ.Client.Events.BasicAckEventArgs(1UL, false)))
            .Returns(ValueTask.CompletedTask);

        var sender = SenderFor(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UseRabbitMq("some-exchange",
                builder => builder.UseRabbitMqClient(mockChannel.Object, mandatory: true))));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        mockChannel.Verify(x => x.BasicPublishAsync(
            "some-exchange", Defaults.Topic, true,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()));
        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }
}
