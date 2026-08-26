using System.Text;
using Benzene.Kafka.Core.TestHelpers;
using Benzene.Testing;
using Confluent.Kafka;
using Xunit;

namespace Benzene.TestHelpers.SmokeTest;

// #81: Benzene.Kafka.Core.TestHelpers had zero coverage from Benzene.sln - this puts a basic
// scenario into the standard test baseline.
public class KafkaSmokeTest
{
    [Fact]
    public void AsKafkaBenzeneMessage_TopicIsRecordTopic_ValueIsRawPayload()
    {
        var record = MessageBuilder.Create("hello_world", new SmokeMessage { Name = "World" })
            .WithHeader("x-trace-id", "abc123")
            .AsKafkaBenzeneMessage();

        Assert.Equal("hello_world", record.Topic);
        Assert.Contains("World", record.Message.Value);
        Assert.True(record.Message.Headers.TryGetLastBytes("x-trace-id", out var headerBytes));
        Assert.Equal("abc123", Encoding.UTF8.GetString(headerBytes));
    }
}
