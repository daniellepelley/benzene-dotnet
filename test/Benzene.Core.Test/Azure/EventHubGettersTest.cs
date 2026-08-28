using System;
using Azure.Messaging.EventHubs;
using Benzene.Azure.Function.EventHub.Function;
using Xunit;

namespace Benzene.Test.Azure;

public class EventHubGettersTest
{
    private static EventHubContext CreateContext(string body = "some-message", params (string Key, object Value)[] properties)
    {
        var eventData = new EventData(new BinaryData(body));
        foreach (var (key, value) in properties)
        {
            eventData.Properties[key] = value;
        }
        return EventHubContext.CreateInstance(eventData);
    }

    [Fact]
    public void GetHeaders_ReturnsOnlyStringTypedProperties()
    {
        var context = CreateContext(properties: new[] { ("some-header", (object)"some-value"), ("some-number", (object)42) });

        var headers = new EventHubMessageHeadersGetter().GetHeaders(context);

        Assert.Single(headers);
        Assert.Equal("some-value", headers["some-header"]);
    }

    [Fact]
    public void GetHeaders_TraceparentProperty_RoundTrips()
    {
        const string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var context = CreateContext(properties: ("traceparent", (object)traceparent));

        var headers = new EventHubMessageHeadersGetter().GetHeaders(context);

        Assert.Equal(traceparent, headers["traceparent"]);
    }

    [Fact]
    public void GetHeaders_NoProperties_ReturnsEmptyDictionary()
    {
        var context = CreateContext();

        var headers = new EventHubMessageHeadersGetter().GetHeaders(context);

        Assert.Empty(headers);
    }

    [Fact]
    public void GetHeaders_IsCaseInsensitive()
    {
        // #165: ToDictionary with no comparer built a plain-ordinal (case-sensitive) dictionary here,
        // unlike every other built-in getter's headers dictionary.
        var context = CreateContext(properties: ("Correlation-Id", (object)"abc-123"));

        var headers = new EventHubMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("abc-123", headers["correlation-id"]);
    }

    // No coverage existed for EventHubMessageBodyGetter at all before round 14-15's #235 pass flagged
    // the broad malformed-input coverage gap across test/Benzene.Core.Test/Azure/ (see
    // work/bug-fix-designs-round15-2026-08.md §3).
    [Fact]
    public void GetBody_ReturnsTheEventBodyAsAString()
    {
        var context = CreateContext("hello");

        Assert.Equal("hello", new EventHubMessageBodyGetter().GetBody(context));
    }

    // Malformed-input coverage gap, closed: an invalid UTF-8 byte sequence in the event body (a
    // poison payload, not merely absent data). Confirms current behavior rather than fixing anything -
    // BinaryData.ToString() decodes as UTF-8 with replacement-character fallback, not a throwing
    // decoder (matching Kafka's and Service Bus's body getters - see their equivalent cases), so this
    // does NOT throw. No latent bug found here.
    [Fact]
    public void GetBody_InvalidUtf8Bytes_DoesNotThrow_ReturnsReplacementCharacters()
    {
        // 0xC3 alone is a lead byte for a 2-byte UTF-8 sequence with no continuation byte - invalid.
        var eventData = new EventData(new BinaryData(new byte[] { 0xC3, 0x28 }));
        var context = EventHubContext.CreateInstance(eventData);

        var body = new EventHubMessageBodyGetter().GetBody(context);

        Assert.NotNull(body);
        Assert.Contains('\uFFFD', body);
    }
}
