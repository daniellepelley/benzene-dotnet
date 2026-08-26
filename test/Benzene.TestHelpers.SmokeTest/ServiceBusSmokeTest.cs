using Benzene.Abstractions;
using Benzene.Azure.ServiceBus.TestHelpers;
using Benzene.Testing;
using Xunit;

namespace Benzene.TestHelpers.SmokeTest;

// #81: Benzene.Azure.ServiceBus.TestHelpers had zero coverage from Benzene.sln - this puts a basic
// scenario into the standard test baseline.
public class ServiceBusSmokeTest
{
    [Fact]
    public void AsAzureServiceBusMessage_TopicRidesAsApplicationProperty_BodyIsRawPayload()
    {
        var message = MessageBuilder.Create("hello:world", new SmokeMessage { Name = "World" })
            .WithHeader("x-trace-id", "abc123")
            .AsAzureServiceBusMessage();

        Assert.Equal("hello:world", message.ApplicationProperties[BenzeneWireNames.DefaultTopic]);
        Assert.Equal("abc123", message.ApplicationProperties["x-trace-id"]);
        Assert.Contains("World", message.Body.ToString());
    }
}
