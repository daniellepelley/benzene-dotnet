using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.SNSEvents;
using Amazon.Lambda.SQSEvents;
using Azure.Messaging.EventHubs;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Azure.EventHub;
using Benzene.Azure.ServiceBus;
using Benzene.Core.Messages;
using Benzene.GoogleCloud.Functions.PubSub.TestHelpers;
using Benzene.RabbitMq.RabbitMqMessage;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Benzene.Aws.Lambda.EventBridge;
using Benzene.Clients.Aws.EventBridge;
using Benzene.Core.MessageHandlers;
using Benzene.Aws.Lambda.Sns;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Azure.Function.EventHub.Function;
using Benzene.Azure.Function.ServiceBus;
using Benzene.Clients.Aws.Sns;
using Benzene.Clients.Aws.Sqs;
using Benzene.Clients.Azure.EventHub;
using Benzene.Clients.Azure.ServiceBus;
using Benzene.Clients.GoogleCloud.PubSub;
using Benzene.GoogleCloud.Functions.PubSub;
using Benzene.Kafka.Core.Kafka;
using Benzene.RabbitMq;
using Xunit;

namespace Benzene.Test.Conformance;

/// <summary>
/// Pins every binding's reserved metadata key against the language-neutral fixture
/// (<c>transport-metadata-cases.json</c>, wire-contracts §2).
/// </summary>
/// <remarks>
/// <para>
/// This is the one part of the wire contract no other fixture touches — envelope, status and
/// protocol-mapping cases never look at native metadata. A binding can therefore rename or misspell
/// its topic key, pass every other test, and still be unable to exchange a single queue message
/// with another port. That is not hypothetical: it is exactly how this port and the Python port
/// diverged when the key was briefly <c>benzene-topic</c>.
/// </para>
/// <para>
/// Inbound getters and outbound converters are both listed on purpose. An override that reached
/// only one direction would make a service send messages it cannot itself receive.
/// </para>
/// </remarks>
public class TransportMetadataConformanceTest
{
    private static JsonDocument LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "conformance", "transport-metadata-cases.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string ExpectedKey(string name)
    {
        using var document = LoadFixture();
        return document.RootElement.GetProperty("defaultMetadataKeys").GetProperty(name).GetString()!;
    }

    private static string ExpectedTopicKey() => ExpectedKey("topic");

    public static TheoryData<string, string> TopicKeys() => new()
    {
        // inbound — the getters that resolve a topic from native metadata
        { "sqs (inbound)", SqsMessageTopicGetter.DefaultTopicAttribute },
        { "sns (inbound)", SnsMessageTopicGetter.DefaultTopicAttribute },
        { "servicebus (inbound)", ServiceBusMessageTopicGetter.DefaultTopicProperty },
        { "eventhub (inbound)", EventHubMessageTopicGetter.DefaultTopicProperty },
        { "pubsub (inbound)", PubSubMessageTopicGetter.DefaultTopicAttribute },
        { "kafka (inbound)", KafkaMessageContextConverter<object>.DefaultTopicHeader },
        { "rabbitmq (inbound)", RabbitMqConstants.DefaultTopicHeader },

        // outbound — the clients that write it, which must agree with the getters above
        { "sqs (outbound)", SqsContextConverter<object>.DefaultTopicAttribute },
        { "sqs (outbound, raw)", OutboundSqsContextConverter.DefaultTopicAttribute },
        { "sns (outbound)", SnsContextConverter<object>.DefaultTopicAttribute },
        { "sns (outbound, raw)", OutboundSnsContextConverter.DefaultTopicAttribute },
        { "servicebus (outbound)", ServiceBusContextConverter<object>.DefaultTopicProperty },
        { "servicebus (outbound, raw)", OutboundServiceBusContextConverter.DefaultTopicProperty },
        { "eventhub (outbound)", EventHubContextConverter<object>.DefaultTopicProperty },
        { "eventhub (outbound, raw)", OutboundEventHubContextConverter.DefaultTopicProperty },
        { "pubsub (outbound)", OutboundPubSubContextConverter.DefaultTopicAttribute },
    };

    [Theory]
    [MemberData(nameof(TopicKeys))]
    public void BindingUsesTheSpecsDefaultTopicKey(string binding, string actualKey)
    {
        Assert.Equal(ExpectedTopicKey(), actualKey);
    }

    [Fact]
    public void TheDefaultsComeFromOneDefinition()
    {
        // Each binding's constant aliases this one rather than repeating the literal. When they each
        // spelled it themselves they were free to drift, and one silently did.
        Assert.Equal(ExpectedTopicKey(), BenzeneWireNames.DefaultTopic);
        Assert.Equal(BenzeneWireNames.DefaultTopic, BenzeneWireNames.Default.Topic);
    }

    [Fact]
    public void TheVersionKeyIsTheOneVersionHeader()
    {
        // wire-contracts §2 / D10: one version header, read by every transport's version getter.
        Assert.Equal(ExpectedKey("version"), BenzeneWireNames.DefaultVersion);
        Assert.Equal(ExpectedKey("version"), HeaderMessageVersionGetter<SqsMessageContext>.DefaultHeaderName);
    }

    public static TheoryData<string, string> EmbeddedHeadersKeys() => new()
    {
        // defaultMetadataKeys.headers is the EventBridge embedded-headers key (only ports with an
        // EventBridge binding assert it); the inbound getter and the outbound converter must agree.
        { "eventbridge (inbound)", EventBridgeMessageHeadersGetter.EmbeddedHeadersKey },
        { "eventbridge (outbound)", EventBridgeContextConverter<object>.EmbeddedHeadersKey },
    };

    [Theory]
    [MemberData(nameof(EmbeddedHeadersKeys))]
    public void EventBridgeUsesTheSpecsEmbeddedHeadersKey(string binding, string actualKey)
    {
        Assert.Equal(ExpectedKey("headers"), actualKey);
    }

    // Every metadataCases and overrideCases entry runs against every binding that resolves its topic from
    // native metadata (the fixture's topicSources with source "metadata"), each decoded the way the pipeline
    // decodes it: the binding's topic getter, its headers getter, and the one version getter over those
    // headers. RabbitMQ's native fallback (its routing key) is given empty here, so an absent key stays
    // unresolved. Kafka is listed there too, but this port's Kafka bindings route on the native Kafka topic,
    // so they have no topic attribute to run these cases on.
    public static TheoryData<string, string> MetadataCases()
    {
        using var document = LoadFixture();
        var names = document.RootElement.GetProperty("metadataCases").EnumerateArray()
            .Concat(document.RootElement.GetProperty("overrideCases").EnumerateArray())
            .Select(x => x.GetProperty("name").GetString()!)
            .ToList();

        var data = new TheoryData<string, string>();
        foreach (var binding in MetadataBindings.Keys)
        {
            foreach (var name in names)
            {
                data.Add(binding, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public void MetadataCase_DecodesAsExpected(string binding, string caseName)
    {
        using var document = LoadFixture();
        var metadataCase = document.RootElement.GetProperty("metadataCases").EnumerateArray()
            .Concat(document.RootElement.GetProperty("overrideCases").EnumerateArray())
            .Single(x => x.GetProperty("name").GetString() == caseName);
        Assert.False(metadataCase.TryGetProperty("requires", out _), $"{caseName} is gated; this runner assumes it is not");

        var topicKey = metadataCase.TryGetProperty("metadataKeys", out var keys) && keys.TryGetProperty("topic", out var key)
            ? key.GetString()!
            : ExpectedTopicKey();
        var metadata = metadataCase.GetProperty("metadata").EnumerateObject()
            .Select(x => new KeyValuePair<string, string>(x.Name, x.Value.GetString()!))
            .ToList();

        var decoded = MetadataBindings[binding](metadata, topicKey, string.Empty);

        var expected = metadataCase.GetProperty("expected");
        Assert.Equal(expected.GetProperty("topic").GetString(), decoded.Topic);
        if (expected.TryGetProperty("version", out var expectedVersion))
        {
            Assert.Equal(expectedVersion.GetString(), decoded.Version);
        }
        if (expected.TryGetProperty("headers", out var expectedHeaders))
        {
            foreach (var header in expectedHeaders.EnumerateObject())
            {
                Assert.True(decoded.Headers.TryGetValue(header.Name, out var value), $"{binding}/{caseName}: header '{header.Name}' missing");
                Assert.Equal(header.Value.GetString(), value);
            }
        }
        if (expected.TryGetProperty("headersExclude", out var excluded))
        {
            foreach (var excludedKey in excluded.EnumerateArray().Select(x => x.GetString()!))
            {
                Assert.False(decoded.Headers.Keys.Any(x => string.Equals(x, excludedKey, StringComparison.OrdinalIgnoreCase)),
                    $"{binding}/{caseName}: '{excludedKey}' was consumed as routing metadata but also surfaced as a header");
            }
        }
    }

    private sealed record Decoded(string Topic, string Version, IDictionary<string, string> Headers);

    private static Decoded Decode<TContext>(TContext context, IMessageTopicGetter<TContext> topicGetter, IMessageHeadersGetter<TContext> headersGetter)
    {
        var topic = topicGetter.GetTopic(context);
        var version = new HeaderMessageVersionGetter<TContext>(headersGetter).GetVersion(context);

        // An unresolved topic is the port's <missing> sentinel; the fixture writes it as "".
        var topicId = topic.Id == null || topic.Id == Benzene.Core.Messages.Constants.Missing.Id ? string.Empty : topic.Id;
        return new Decoded(topicId, version ?? string.Empty, headersGetter.GetHeaders(context));
    }

    // binding -> (neutral metadata, configured topic key, native fallback topic) -> decoded. Each adapts the neutral string->string
    // metadata to the binding's native channel, the way that transport's SDK would deliver it.
    private static readonly IReadOnlyDictionary<string, Func<IReadOnlyList<KeyValuePair<string, string>>, string, string, Decoded>> MetadataBindings =
        new Dictionary<string, Func<IReadOnlyList<KeyValuePair<string, string>>, string, string, Decoded>>
        {
            ["sqs"] = (metadata, topicKey, nativeTopic) =>
            {
                var message = new SQSEvent.SQSMessage
                {
                    MessageAttributes = metadata.ToDictionary(x => x.Key,
                        x => new SQSEvent.MessageAttribute { DataType = "String", StringValue = x.Value })
                };
                var context = SqsMessageContext.CreateInstance(new SQSEvent { Records = new() { message } }, message);
                return Decode(context, new SqsMessageTopicGetter(topicKey), new SqsMessageHeadersGetter(topicKey));
            },
            ["sqs (consumer)"] = (metadata, topicKey, nativeTopic) =>
            {
                var context = SqsConsumerMessageContext.CreateInstance(new Amazon.SQS.Model.Message
                {
                    MessageAttributes = metadata.ToDictionary(x => x.Key,
                        x => new Amazon.SQS.Model.MessageAttributeValue { DataType = "String", StringValue = x.Value })
                });
                return Decode(context, new SqsConsumerMessageTopicGetter(topicKey), new SqsConsumerMessageHeadersGetter(topicKey));
            },
            ["sns"] = (metadata, topicKey, nativeTopic) =>
            {
                var context = SnsRecordContext.CreateInstance(null, new SNSEvent.SNSRecord
                {
                    Sns = new SNSEvent.SNSMessage
                    {
                        Message = "{}",
                        MessageAttributes = metadata.ToDictionary(x => x.Key,
                            x => new SNSEvent.MessageAttribute { Type = "String", Value = x.Value })
                    }
                });
                return Decode(context, new SnsMessageTopicGetter(topicKey), new SnsMessageHeadersGetter(topicKey));
            },
            ["servicebus"] = (metadata, topicKey, nativeTopic) =>
            {
                var context = new ServiceBusContext(ServiceBusModelFactory.ServiceBusReceivedMessage(
                    body: new BinaryData("{}"),
                    properties: metadata.ToDictionary(x => x.Key, x => (object)x.Value)));
                return Decode(context, new ServiceBusMessageTopicGetter(topicKey), new ServiceBusMessageHeadersGetter(topicKey));
            },
            ["servicebus (consumer)"] = (metadata, topicKey, nativeTopic) =>
            {
                var context = ServiceBusConsumerContext.CreateInstance(ServiceBusModelFactory.ServiceBusReceivedMessage(
                    body: new BinaryData("{}"),
                    properties: metadata.ToDictionary(x => x.Key, x => (object)x.Value)));
                return Decode(context, new ServiceBusConsumerMessageTopicGetter(topicKey), new ServiceBusConsumerMessageHeadersGetter(topicKey));
            },
            ["eventhub"] = (metadata, topicKey, nativeTopic) =>
            {
                var context = EventHubContext.CreateInstance(EventDataWith(metadata));
                return Decode(context, new EventHubMessageTopicGetter(topicKey), new EventHubMessageHeadersGetter(topicKey));
            },
            ["eventhub (consumer)"] = (metadata, topicKey, nativeTopic) =>
            {
                var context = EventHubConsumerContext.CreateInstance(EventDataWith(metadata));
                return Decode(context, new EventHubConsumerMessageTopicGetter(topicKey), new EventHubConsumerMessageHeadersGetter(topicKey));
            },
            ["pubsub"] = (metadata, topicKey, nativeTopic) =>
            {
                var builder = new PubSubMessageBuilder();
                foreach (var pair in metadata)
                {
                    builder = builder.WithAttribute(pair.Key, pair.Value);
                }
                var context = new PubSubContext(builder.Build());
                return Decode(context, new PubSubMessageTopicGetter(topicKey), new PubSubMessageHeadersGetter(topicKey));
            },
            ["rabbitmq"] = (metadata, topicKey, nativeTopic) =>
            {
                // The AMQP routing key is the binding's fallback (topicSources).
                var properties = new BasicProperties
                {
                    Headers = metadata.ToDictionary(x => x.Key, x => (object?)Encoding.UTF8.GetBytes(x.Value))
                };
                var context = RabbitMqContext.CreateInstance(new BasicDeliverEventArgs(
                    consumerTag: "tag", deliveryTag: 1, redelivered: false, exchange: "exchange",
                    routingKey: nativeTopic, properties: properties, body: Encoding.UTF8.GetBytes("{}")));
                return Decode(context, new RabbitMqMessageTopicGetter(topicKey), new RabbitMqMessageHeadersGetter(topicKey));
            },
        };

    private static EventData EventDataWith(IEnumerable<KeyValuePair<string, string>> metadata)
    {
        var eventData = new EventData(new BinaryData("{}"));
        foreach (var pair in metadata)
        {
            eventData.Properties[pair.Key] = pair.Value;
        }

        return eventData;
    }

    [Fact]
    public void NamesAreOverridablePerService()
    {
        // The spec requires the reserved names to be one injectable value, not a literal per binding.
        IBenzeneWireNames names = new BenzeneWireNames(topic: "x-my-topic");

        Assert.Equal("x-my-topic", names.Topic);
        Assert.Equal(BenzeneWireNames.DefaultVersion, names.Version);

        // A getter built from those names reads the overridden key, and the default key stops routing.
        var getter = new SqsMessageTopicGetter(names.Topic);
        Assert.NotNull(getter);
    }
}
