using System;
using System.Collections.Generic;
using System.Text;
using Amazon.SQS.Model;
using Azure.Messaging.EventHubs;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Azure.EventHub;
using Benzene.Azure.ServiceBus;
using Benzene.Aws.Sqs;
using Benzene.Aws.Sqs.Consumer;
using Benzene.RabbitMq;
using Benzene.RabbitMq.RabbitMqMessage;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace Benzene.Test.Customization;

// Regression coverage for making IBenzeneWireNames a real DI seam (see
// work/customization-robustness-review.md): previously nothing resolved it, so registering a
// replacement had no effect - this proves a replacement now changes the topic key each transport's
// consumer-side topic getter reads, for every transport whose Add*Consumer left topicAttributeKey /
// topicPropertyKey / topicHeaderKey at that transport's own default.
public class BenzeneWireNamesOverrideTest
{
    private const string CustomTopicKey = "x-custom-topic";

    [Fact]
    public void SqsConsumer_DefaultKeyLeftAsIs_HonorsRegisteredWireNames()
    {
        var services = ServiceResolverMother.CreateServiceCollection(x => x.AddSqsConsumer());
        services.AddSingleton<IBenzeneWireNames>(new BenzeneWireNames(CustomTopicKey));

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var topicGetter = scope.ServiceProvider.GetRequiredService<IMessageTopicGetter<SqsConsumerMessageContext>>();

        var message = new Message
        {
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [CustomTopicKey] = new MessageAttributeValue { StringValue = "order:create" }
            }
        };
        var topic = topicGetter.GetTopic(SqsConsumerMessageContext.CreateInstance(message));

        Assert.Equal("order:create", topic.Id);
    }

    [Fact]
    public void SqsConsumer_ExplicitKeyPassed_WireNamesDoesNotOverrideIt()
    {
        var services = ServiceResolverMother.CreateServiceCollection(x => x.AddSqsConsumer("explicit-key"));
        services.AddSingleton<IBenzeneWireNames>(new BenzeneWireNames(CustomTopicKey));

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var topicGetter = scope.ServiceProvider.GetRequiredService<IMessageTopicGetter<SqsConsumerMessageContext>>();

        var message = new Message
        {
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["explicit-key"] = new MessageAttributeValue { StringValue = "order:create" }
            }
        };
        var topic = topicGetter.GetTopic(SqsConsumerMessageContext.CreateInstance(message));

        Assert.Equal("order:create", topic.Id);
    }

    [Fact]
    public void ServiceBusConsumer_DefaultKeyLeftAsIs_HonorsRegisteredWireNames()
    {
        var services = ServiceResolverMother.CreateServiceCollection(x => x.AddServiceBusConsumer());
        services.AddSingleton<IBenzeneWireNames>(new BenzeneWireNames(CustomTopicKey));

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var topicGetter = scope.ServiceProvider.GetRequiredService<IMessageTopicGetter<ServiceBusConsumerContext>>();

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            properties: new Dictionary<string, object> { [CustomTopicKey] = "order:create" });
        var topic = topicGetter.GetTopic(ServiceBusConsumerContext.CreateInstance(message));

        Assert.Equal("order:create", topic.Id);
    }

    [Fact]
    public void EventHubConsumer_DefaultKeyLeftAsIs_HonorsRegisteredWireNames()
    {
        var services = ServiceResolverMother.CreateServiceCollection(x => x.AddEventHubConsumer());
        services.AddSingleton<IBenzeneWireNames>(new BenzeneWireNames(CustomTopicKey));

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var topicGetter = scope.ServiceProvider.GetRequiredService<IMessageTopicGetter<EventHubConsumerContext>>();

        var eventData = EventHubsModelFactory.EventData(
            eventBody: new BinaryData("{}"),
            properties: new Dictionary<string, object> { [CustomTopicKey] = "order:create" });
        var topic = topicGetter.GetTopic(EventHubConsumerContext.CreateInstance(eventData));

        Assert.Equal("order:create", topic.Id);
    }

    [Fact]
    public void RabbitMq_DefaultKeyLeftAsIs_HonorsRegisteredWireNames()
    {
        var services = ServiceResolverMother.CreateServiceCollection(x => x.AddRabbitMq());
        services.AddSingleton<IBenzeneWireNames>(new BenzeneWireNames(CustomTopicKey));

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var topicGetter = scope.ServiceProvider.GetRequiredService<IMessageTopicGetter<RabbitMqContext>>();

        var headers = new Dictionary<string, object?> { [CustomTopicKey] = Encoding.UTF8.GetBytes("order:create") };
        var properties = new BasicProperties { Headers = headers };
        var args = new BasicDeliverEventArgs(
            consumerTag: "tag",
            deliveryTag: 1,
            redelivered: false,
            exchange: "exchange",
            routingKey: "routing.key",
            properties: properties,
            body: Encoding.UTF8.GetBytes("{}"));
        var context = RabbitMqContext.CreateInstance(args);
        var topic = topicGetter.GetTopic(context);

        Assert.Equal("order:create", topic.Id);
    }
}
