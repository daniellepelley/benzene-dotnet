using System.Text;
using Benzene.RabbitMq;
using Benzene.RabbitMq.TestHelpers;
using Benzene.Testing;
using Xunit;

namespace Benzene.TestHelpers.SmokeTest;

// #81: Benzene.RabbitMq.TestHelpers had zero coverage from Benzene.sln - this puts a basic scenario
// into the standard test baseline.
public class RabbitMqSmokeTest
{
    [Fact]
    public void AsRabbitMqBenzeneMessage_TopicRidesAsHeaderAndRoutingKey_BodyIsRawPayload()
    {
        var delivery = MessageBuilder.Create("hello:world", new SmokeMessage { Name = "World" })
            .WithHeader("x-trace-id", "abc123")
            .AsRabbitMqBenzeneMessage();

        Assert.Equal("hello:world", delivery.RoutingKey);

        var topicHeaderBytes = Assert.IsType<byte[]>(delivery.BasicProperties.Headers![RabbitMqConstants.DefaultTopicHeader]);
        Assert.Equal("hello:world", Encoding.UTF8.GetString(topicHeaderBytes));

        var traceHeaderBytes = Assert.IsType<byte[]>(delivery.BasicProperties.Headers!["x-trace-id"]);
        Assert.Equal("abc123", Encoding.UTF8.GetString(traceHeaderBytes));

        Assert.Contains("World", Encoding.UTF8.GetString(delivery.Body.ToArray()));
    }
}
