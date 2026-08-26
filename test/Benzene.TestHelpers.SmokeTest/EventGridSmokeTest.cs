using Benzene.Azure.Function.EventGrid.TestHelpers;
using Benzene.Testing;
using Xunit;

namespace Benzene.TestHelpers.SmokeTest;

// #81: Benzene.Azure.Function.EventGrid.TestHelpers had zero coverage from Benzene.sln - this puts
// a basic scenario into the standard test baseline.
public class EventGridSmokeTest
{
    [Fact]
    public void AsEventGridBenzeneMessage_TopicBecomesEventType_DataIsRawPayload()
    {
        var triggerEvent = MessageBuilder.Create("hello.world", new SmokeMessage { Name = "World" })
            .AsEventGridBenzeneMessage();

        Assert.Equal("hello.world", triggerEvent.EventType);
        Assert.True(triggerEvent.Data.HasValue);
        Assert.Equal("World", triggerEvent.Data!.Value.GetProperty("name").GetString());
    }
}
