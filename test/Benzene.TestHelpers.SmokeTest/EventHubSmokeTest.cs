using Benzene.Azure.EventHub;
using Benzene.Azure.EventHub.TestHelpers;
using Benzene.Testing;
using Xunit;

namespace Benzene.TestHelpers.SmokeTest;

// #81: Benzene.Azure.EventHub.TestHelpers (the self-hosted Event Hub consumer's test helpers) had
// zero coverage from Benzene.sln - this puts a basic scenario into the standard test baseline.
public class EventHubSmokeTest
{
    [Fact]
    public void AsEventHubBenzeneMessage_TopicRidesAsProperty_BodyIsRawPayload()
    {
        var eventData = MessageBuilder.Create("hello:world", new SmokeMessage { Name = "World" })
            .WithHeader("x-trace-id", "abc123")
            .AsEventHubBenzeneMessage();

        Assert.Equal("hello:world", eventData.Properties[EventHubConsumerMessageTopicGetter.DefaultTopicProperty]);
        Assert.Equal("abc123", eventData.Properties["x-trace-id"]);
        Assert.Contains("World", eventData.EventBody.ToString());
    }
}
