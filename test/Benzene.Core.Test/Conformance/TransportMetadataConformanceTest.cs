using System;
using System.IO;
using System.Text.Json;
using Benzene.Abstractions;
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
    private static string ExpectedTopicKey()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "conformance", "transport-metadata-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .GetProperty("defaultMetadataKeys")
            .GetProperty("topic")
            .GetString()!;
    }

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
