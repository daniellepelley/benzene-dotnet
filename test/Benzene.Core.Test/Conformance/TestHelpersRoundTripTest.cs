using Benzene.Abstractions;
using Benzene.Aws.Lambda.Sns;
using Benzene.Aws.Lambda.Sns.TestHelpers;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Aws.Lambda.Sqs.TestHelpers;
using Benzene.Azure.Function.ServiceBus;
using Benzene.Azure.Function.ServiceBus.TestHelpers;
using Benzene.GoogleCloud.Functions.PubSub;
using Benzene.GoogleCloud.Functions.PubSub.TestHelpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Xunit;

namespace Benzene.Test.Conformance;

/// <summary>
/// Each transport's test-helper builder produces something that transport's own getter can read.
/// </summary>
/// <remarks>
/// <para>
/// A test double that spells a wire key itself is free to drift from the decoder it stands in for,
/// and when it does, every test using it passes while the real transport cannot route a single
/// message. That is not hypothetical: the Azure test double wrote <c>props["topic"]</c> as a literal
/// and drifted from its own decoder the moment the constant changed.
/// </para>
/// <para>
/// The builders now reference <c>BenzeneWireNames.DefaultTopic</c> rather than spelling the key, and
/// these assertions hold whether they do or not — they drive the builder's output through the real
/// getter, so agreement is checked rather than assumed. Convention alone is exactly what failed.
/// </para>
/// </remarks>
public class TestHelpersRoundTripTest
{
    private static IMessageBuilder<ExampleRequestPayload> Message() =>
        MessageBuilder.Create(Defaults.Topic, new ExampleRequestPayload());

    [Fact]
    public void Sqs()
    {
        var sqsEvent = Message().AsSqs();
        var context = SqsMessageContext.CreateInstance(sqsEvent, sqsEvent.Records[0]);

        Assert.Equal(Defaults.Topic, new SqsMessageTopicGetter().GetTopic(context).Id);
    }

    [Fact]
    public void Sns()
    {
        var snsEvent = Message().AsSns();
        var context = SnsRecordContext.CreateInstance(snsEvent, snsEvent.Records[0]);

        Assert.Equal(Defaults.Topic, new SnsMessageTopicGetter().GetTopic(context).Id);
    }

    [Fact]
    public void ServiceBus()
    {
        var context = new ServiceBusContext(Message().AsAzureServiceBusMessage());

        Assert.Equal(Defaults.Topic, new ServiceBusMessageTopicGetter().GetTopic(context).Id);
    }

    [Fact]
    public void PubSub()
    {
        var context = new PubSubContext(Message().AsPubSubEvent());

        Assert.Equal(Defaults.Topic, new PubSubMessageTopicGetter().GetTopic(context).Id);
    }

    [Fact]
    public void TheBuildersUseTheSpecsDefaultKey()
    {
        // Not a substitute for the round trips above — a builder and a getter can agree on the wrong
        // key and still round-trip. This pins that key to the one the conformance fixture names, which
        // is what lets a Benzene service exchange a message with another port.
        Assert.Equal("topic", BenzeneWireNames.DefaultTopic);
    }
}
