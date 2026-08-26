using System.Collections.Generic;
using Amazon.Lambda.SQSEvents;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Core.MessageHandlers;
using Xunit;
using Constants = Benzene.Core.Constants;

namespace Benzene.Test.Aws.Sqs
{
    public class SqsMessageMapperTests
    {
        [Fact]
        public void SqsMessageMapperTest()
        {
            var sqsMessageContext = SqsMessageContext.CreateInstance(null, new SQSEvent.SQSMessage
            {
                Body = "some-message",
                MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>
                {
                    { "topic", new SQSEvent.MessageAttribute { StringValue = "some-topic" } }
                }
            });

            var mapper = new MessageGetter<SqsMessageContext>(new SqsMessageTopicGetter(), new SqsMessageBodyGetter(), new SqsMessageHeadersGetter());

            var topic = mapper.GetTopic(sqsMessageContext);
            var message = mapper.GetBody(sqsMessageContext);

            Assert.Equal("some-topic", topic.Id);
            Assert.Equal("some-message", message);
        }

        [Fact]
        public void SqsMessageMapperTest_NoTopic()
        {
            var sqsMessageContext = SqsMessageContext.CreateInstance(null, new SQSEvent.SQSMessage
            {
                Body = "some-message",
                MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
            });

            var mapper = new MessageGetter<SqsMessageContext>(new SqsMessageTopicGetter(), new SqsMessageBodyGetter(), new SqsMessageHeadersGetter());

            var topic = mapper.GetTopic(sqsMessageContext);
            var message = mapper.GetBody(sqsMessageContext);

            Assert.Equal(Constants.Missing, topic.Id);
            Assert.Equal("some-message", message);
        }

        [Fact]
        public void SqsMessageTopicGetter_ReadsCustomAttributeKey_WhenConfigured()
        {
            var sqsMessageContext = SqsMessageContext.CreateInstance(null, new SQSEvent.SQSMessage
            {
                Body = "some-message",
                MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>
                {
                    { "x-my-topic", new SQSEvent.MessageAttribute { StringValue = "some-topic" } }
                }
            });

            var topic = new SqsMessageTopicGetter("x-my-topic").GetTopic(sqsMessageContext);

            Assert.Equal("some-topic", topic.Id);
        }

        [Fact]
        public void SqsMessageHeadersGetter_IsCaseInsensitive_RegardlessOfWhetherAttributesArePresent()
        {
            // #165: previously only the null-attributes fallback used OrdinalIgnoreCase, so the
            // comparer (and therefore header-name case-sensitivity) silently depended on whether the
            // message happened to carry any attributes.
            var sqsMessageContext = SqsMessageContext.CreateInstance(null, new SQSEvent.SQSMessage
            {
                Body = "some-message",
                MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>
                {
                    { "Correlation-Id", new SQSEvent.MessageAttribute { DataType = "String", StringValue = "abc-123" } }
                }
            });

            var headers = new SqsMessageHeadersGetter().GetHeaders(sqsMessageContext);

            Assert.Equal("abc-123", headers["correlation-id"]);
        }

        [Fact]
        public async System.Threading.Tasks.Task SqsMessageBodySetter_ReplacesTheRawBody()
        {
            var sqsMessageContext = SqsMessageContext.CreateInstance(null, new SQSEvent.SQSMessage
            {
                Body = "{\"_benzeneClaimCheck\":\"memory://claim-check/abc\"}"
            });

            await new SqsMessageBodySetter().SetBody(sqsMessageContext, "{\"name\":\"some-name\"}");

            Assert.Equal("{\"name\":\"some-name\"}", sqsMessageContext.SqsMessage.Body);
        }
    }
}
