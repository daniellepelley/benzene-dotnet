using System;
using Azure.Messaging.ServiceBus;
using Benzene.Azure.Function.ServiceBus;
using Xunit;

namespace Benzene.Test.Azure.ServiceBus;

// No dedicated test file existed for ServiceBusMessageBodyGetter before round 14-15's #235 pass
// flagged the broad malformed-input coverage gap across test/Benzene.Core.Test/Azure/ (see
// work/bug-fix-designs-round15-2026-08.md §3) - this file closes it, alongside the sibling
// TopicGetter/HeadersGetter test files already in this directory.
public class ServiceBusMessageBodyGetterTest
{
    [Fact]
    public void GetBody_ReturnsTheMessageBodyAsAString()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData("hello"));
        var context = new ServiceBusContext(message);

        Assert.Equal("hello", new ServiceBusMessageBodyGetter().GetBody(context));
    }

    // Malformed-input coverage gap, closed: an invalid UTF-8 byte sequence in the body (a poison
    // payload, not merely absent data). Confirms current behavior rather than fixing anything -
    // BinaryData.ToString() decodes as UTF-8 with replacement-character fallback, not a throwing
    // decoder (matching Kafka's body getter - see KafkaGettersTest.cs's equivalent case), so this does
    // NOT throw. No latent bug found here.
    [Fact]
    public void GetBody_InvalidUtf8Bytes_DoesNotThrow_ReturnsReplacementCharacters()
    {
        // 0xC3 alone is a lead byte for a 2-byte UTF-8 sequence with no continuation byte - invalid.
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(new byte[] { 0xC3, 0x28 }));
        var context = new ServiceBusContext(message);

        var body = new ServiceBusMessageBodyGetter().GetBody(context);

        Assert.NotNull(body);
        Assert.Contains('\uFFFD', body);
    }
}
