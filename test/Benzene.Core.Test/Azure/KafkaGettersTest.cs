using System;
using System.Text;
using Benzene.Azure.Function.Kafka;
using Microsoft.Azure.Functions.Worker;
using Xunit;

namespace Benzene.Test.Azure;

public class KafkaGettersTest
{
    [Fact]
    public void KafkaMessageBodyGetter_ReturnsTheUtf8DecodedValue()
    {
        var context = new KafkaContext(new KafkaRecord { Value = Encoding.UTF8.GetBytes("hello") });

        Assert.Equal("hello", new KafkaMessageBodyGetter().GetBody(context));
    }

    [Fact]
    public void KafkaMessageBodyGetter_NullValue_ReturnsNull()
    {
        var context = new KafkaContext(new KafkaRecord { Value = null });

        Assert.Null(new KafkaMessageBodyGetter().GetBody(context));
    }

    // Round 14-15 #235 coverage gap: essentially no getter across the Azure Functions transports had
    // a malformed-input test. Closing it for Kafka's body getter - an invalid UTF-8 byte sequence in
    // the record value (a poison payload, not merely absent data like the null case above).
    // Confirms current behavior rather than fixing anything: Encoding.UTF8.GetString uses replacement
    // fallback (U+FFFD), not a throwing decoder, so this does NOT throw - no latent bug found here.
    [Fact]
    public void KafkaMessageBodyGetter_InvalidUtf8Bytes_DoesNotThrow_ReturnsReplacementCharacters()
    {
        // 0xC3 alone is a lead byte for a 2-byte UTF-8 sequence with no continuation byte - invalid.
        var context = new KafkaContext(new KafkaRecord { Value = new byte[] { 0xC3, 0x28 } });

        var body = new KafkaMessageBodyGetter().GetBody(context);

        Assert.NotNull(body);
        Assert.Contains('\uFFFD', body);
    }

    [Fact]
    public void KafkaMessageTopicGetter_ReturnsTheRecordTopic()
    {
        var context = new KafkaContext(new KafkaRecord { Topic = "my-topic" });

        Assert.Equal("my-topic", new KafkaMessageTopicGetter().GetTopic(context).Id);
    }

    [Fact]
    public void KafkaMessageHeadersGetter_NullHeaders_ReturnsAnEmptyDictionary()
    {
        var context = new KafkaContext(new KafkaRecord());

        Assert.Empty(new KafkaMessageHeadersGetter().GetHeaders(context));
    }

    [Fact]
    public void KafkaMessageHeadersGetter_EmptyHeaders_ReturnsAnEmptyDictionary()
    {
        var context = new KafkaContext(new KafkaRecord { Headers = Array.Empty<KafkaHeader>() });

        Assert.Empty(new KafkaMessageHeadersGetter().GetHeaders(context));
    }

    [Fact]
    public void KafkaMessageHeadersGetter_ReturnsTheUtf8DecodedHeaderValues()
    {
        var context = new KafkaContext(new KafkaRecord
        {
            Headers = new[]
            {
                new KafkaHeader { Key = "traceparent", Value = Encoding.UTF8.GetBytes("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01") },
                new KafkaHeader { Key = "correlation-id", Value = Encoding.UTF8.GetBytes("abc-123") }
            }
        });

        var headers = new KafkaMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", headers["traceparent"]);
        Assert.Equal("abc-123", headers["correlation-id"]);
    }

    [Fact]
    public void KafkaMessageHeadersGetter_DuplicateHeaderKeys_TakesLastValue_DoesNotThrow()
    {
        // Kafka permits repeated header keys; ToDictionary threw on the duplicate, aborting the record.
        var context = new KafkaContext(new KafkaRecord
        {
            Headers = new[]
            {
                new KafkaHeader { Key = "trace", Value = Encoding.UTF8.GetBytes("a") },
                new KafkaHeader { Key = "trace", Value = Encoding.UTF8.GetBytes("b") }
            }
        });

        var headers = new KafkaMessageHeadersGetter().GetHeaders(context);

        Assert.Equal("b", headers["trace"]);
    }
}
