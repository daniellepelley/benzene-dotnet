using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Benzene.Clients;
using Benzene.Clients.GoogleCloud.PubSub;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json;
using Xunit;
using Encoding = System.Text.Encoding;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients.PubSub;

/// <summary>
/// Covers the <c>OutboundContext</c> overloads of <c>UsePubSub</c> - the ones that let an outbound
/// route (<c>AddOutboundRouting(...).Route(...)</c>) terminate in a Pub/Sub publish without a
/// hand-written terminal middleware.
/// </summary>
public class OutboundPubSubContextConverterTest
{
    private const string Topic = "projects/test-project/topics/" + Defaults.Topic;

    private static Mock<PublisherServiceApiClient> PublishingPublisher()
    {
        var mockPublisher = new Mock<PublisherServiceApiClient>();
        mockPublisher.Setup(x => x.PublishAsync(
                It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()))
            .ReturnsAsync(new PublishResponse { MessageIds = { "message-1" } });
        return mockPublisher;
    }

    private static IBenzeneMessageSender SenderFor(Action<OutboundRoutingBuilder> configure)
    {
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(configure);
        return new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()).GetService<IBenzeneMessageSender>();
    }

    [Fact]
    public async Task SendAsync_RoutedThroughUsePubSub_PublishesAndReturnsAcceptedVoidResult()
    {
        var mockPublisher = PublishingPublisher();
        var sender = SenderFor(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UsePubSub(Topic, action => action.UsePubSubClient(mockPublisher.Object))));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        mockPublisher.Verify(x => x.PublishAsync(
            TopicName.Parse(Topic),
            It.Is<IEnumerable<PubsubMessage>>(messages => JsonConvert.DeserializeObject<ExampleRequestPayload>(
                Encoding.UTF8.GetString(System.Linq.Enumerable.Single(messages).Data.ToByteArray())).Name == "foo"),
            It.IsAny<CallSettings>()));

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendAsync_ForwardsHeadersAndTheTopicOntoMessageAttributes()
    {
        PubsubMessage captured = null;
        var mockPublisher = new Mock<PublisherServiceApiClient>();
        mockPublisher.Setup(x => x.PublishAsync(
                It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()))
            .Callback((TopicName _, IEnumerable<PubsubMessage> messages, CallSettings _) => captured = System.Linq.Enumerable.Single(messages))
            .ReturnsAsync(new PublishResponse { MessageIds = { "message-1" } });

        var sender = SenderFor(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UsePubSub(Topic, action => action.UsePubSubClient(mockPublisher.Object))));

        await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" },
            new Dictionary<string, string> { { "tenantId", "tenant-1" } });

        Assert.NotNull(captured);
        Assert.Equal("tenant-1", captured.Attributes["tenantId"]);
        Assert.Equal(Defaults.Topic, captured.Attributes["topic"]);
    }

    [Fact]
    public async Task SendAsync_WithCustomTopicAttributeKey_WritesTheTopicToThatAttribute()
    {
        PubsubMessage captured = null;
        var mockPublisher = new Mock<PublisherServiceApiClient>();
        mockPublisher.Setup(x => x.PublishAsync(
                It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()))
            .Callback((TopicName _, IEnumerable<PubsubMessage> messages, CallSettings _) => captured = System.Linq.Enumerable.Single(messages))
            .ReturnsAsync(new PublishResponse { MessageIds = { "message-1" } });

        var sender = SenderFor(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UsePubSub(
                Topic, action => action.UsePubSubClient(mockPublisher.Object), topicAttributeKey: "x-my-topic")));

        await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        Assert.NotNull(captured);
        Assert.Equal(Defaults.Topic, captured.Attributes["x-my-topic"]);
        Assert.False(captured.Attributes.ContainsKey("topic"));
    }

    [Fact]
    public async Task SendAsync_ThroughTheExplicitInnerPipelineOverload_PublishesViaTheGivenPublisher()
    {
        // The rung below the client-registration shorthand: the caller configures the inner publish
        // pipeline directly rather than relying on the default UsePubSubClient() wiring.
        var mockPublisher = PublishingPublisher();
        var sender = SenderFor(routing => routing
            .Route(Defaults.Topic, pipeline => pipeline.UsePubSub(Topic,
                builder => builder.UsePubSubClient(mockPublisher.Object))));

        await sender.SendAsync<ExampleRequestPayload, Void>(
            Defaults.Topic, new ExampleRequestPayload { Id = 42, Name = "foo" });

        mockPublisher.Verify(x => x.PublishAsync(
            TopicName.Parse(Topic), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()));
    }

    [Fact]
    public async Task SendAsync_PublishSucceeds_RecordsTheServerAssignedMessageId()
    {
        var mockPublisher = new Mock<PublisherServiceApiClient>();
        mockPublisher.Setup(x => x.PublishAsync(
                It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()))
            .ReturnsAsync(new PublishResponse { MessageIds = { "message-7" } });

        PubSubSendMessageContext context = null;
        var middleware = new PubSubClientMiddleware(mockPublisher.Object);
        context = new PubSubSendMessageContext(TopicName.Parse(Topic), new PubsubMessage());

        await middleware.HandleAsync(context, () => Task.CompletedTask);

        Assert.Equal("message-7", context.MessageId);
    }
}
