using System;
using System.Collections.Generic;
using Azure.Messaging.ServiceBus;
using Benzene.Azure.Function.ServiceBus;
using Xunit;

namespace Benzene.Test.Azure.ServiceBus;

public class ServiceBusMessageHeadersGetterTest
{
    [Fact]
    public void GetHeaders_ReturnsOnlyStringTypedProperties()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("some-message"),
            properties: new Dictionary<string, object>
            {
                { "tenant", "acme" },
                { "count", 5 }
            });
        var context = new ServiceBusContext(message);

        var headers = new ServiceBusMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("acme", headers["tenant"]);
        Assert.False(headers.ContainsKey("count"));
    }

    [Fact]
    public void GetHeaders_IsCaseInsensitive()
    {
        // #165: ToDictionary with no comparer built a plain-ordinal (case-sensitive) dictionary here,
        // unlike every other built-in getter's headers dictionary.
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("some-message"),
            properties: new Dictionary<string, object>
            {
                { "Correlation-Id", "abc-123" }
            });
        var context = new ServiceBusContext(message);

        var headers = new ServiceBusMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("abc-123", headers["correlation-id"]);
    }

    [Fact]
    public void GetHeaders_LeavesOutTheConsumedTopicProperty()
    {
        // headersExclude (transport-metadata-cases.json): the topic is routing metadata, not a header.
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("some-message"),
            properties: new Dictionary<string, object>
            {
                { "topic", "some-topic" },
                { "tenant", "acme" }
            });
        var context = new ServiceBusContext(message);

        var headers = new ServiceBusMessageHeadersGetter().GetHeaders(context);

        Assert.False(headers.ContainsKey("topic"));
        Assert.Equal("acme", headers["tenant"]);
    }
}
