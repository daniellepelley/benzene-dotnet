using System.Collections.Generic;
using Amazon.SQS.Model;
using Benzene.Aws.Sqs.Consumer;
using Xunit;

namespace Benzene.Test.Aws.Sqs;

public class SqsConsumerMessageHeadersGetterTest
{
    [Fact]
    public void GetHeaders_ReturnsOnlyStringTypedAttributes()
    {
        var context = SqsConsumerMessageContext.CreateInstance(new Message
        {
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                { "tenant", new MessageAttributeValue { DataType = "String", StringValue = "acme" } },
                { "count", new MessageAttributeValue { DataType = "Number", StringValue = "5" } }
            }
        });

        var headers = new SqsConsumerMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("acme", headers["tenant"]);
        Assert.False(headers.ContainsKey("count"));
    }

    [Fact]
    public void GetHeaders_IsCaseInsensitive_RegardlessOfWhetherAttributesArePresent()
    {
        // #165: previously only the null-attributes fallback used OrdinalIgnoreCase, so the
        // comparer (and therefore header-name case-sensitivity) silently depended on whether the
        // message happened to carry any attributes.
        var context = SqsConsumerMessageContext.CreateInstance(new Message
        {
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                { "Correlation-Id", new MessageAttributeValue { DataType = "String", StringValue = "abc-123" } }
            }
        });

        var headers = new SqsConsumerMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("abc-123", headers["correlation-id"]);
    }

    [Fact]
    public void GetHeaders_LeavesOutTheConsumedTopicAttribute_UnderAnyCasing()
    {
        // The topic is routing metadata consumed by the topic getter; it must not reappear as a header
        // (transport-metadata-cases.json headersExclude), and keys are case-insensitive on read.
        var context = SqsConsumerMessageContext.CreateInstance(new Message
        {
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                { "Topic", new MessageAttributeValue { DataType = "String", StringValue = "some-topic" } },
                { "tenant", new MessageAttributeValue { DataType = "String", StringValue = "acme" } }
            }
        });

        var headers = new SqsConsumerMessageHeadersGetter().GetHeaders(context);

        Assert.False(headers.ContainsKey("topic"));
        Assert.Equal("acme", headers["tenant"]);
        Assert.Equal("some-topic", new SqsConsumerMessageTopicGetter().GetTopic(context).Id);
    }
}
