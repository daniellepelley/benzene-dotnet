using Benzene.Azure.Function.QueueStorage.TestHelpers;
using Benzene.Testing;
using Benzene.Xml;
using Xunit;
using JsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;

namespace Benzene.TestHelpers.SmokeTest;

// #80 (worth-fixing): AsQueueStorageBenzeneMessage(serializer) used to serialize the WHOLE envelope
// (topic + headers + body) with the caller-supplied serializer, which crashed for a non-JSON
// serializer because Headers is an IDictionary<string,string> (an interface, unserializable by
// System.Xml.Serialization.XmlSerializer). The fix: the envelope always goes through fixed JSON,
// matching the sibling AsEventHubBenzeneMessage - only the envelope's Body uses the passed
// serializer. This is the round-9 repro, now permanent (see work/outstanding-bugs.md).
public class QueueStorageSmokeTest
{
    [Fact]
    public void AsQueueStorageBenzeneMessage_DefaultSerializer_EnvelopeIsJson()
    {
        var message = MessageBuilder.Create("hello:world", new SmokeMessage { Name = "World" })
            .WithHeader("x-trace-id", "abc123")
            .AsQueueStorageBenzeneMessage();

        var envelope = new JsonSerializer().Deserialize<EnvelopeShape>(message.MessageText);

        Assert.Equal("hello:world", envelope!.Topic);
        Assert.Equal("abc123", envelope.Headers["x-trace-id"]);

        var body = new JsonSerializer().Deserialize<SmokeMessage>(envelope.Body);
        Assert.Equal("World", body!.Name);
    }

    [Fact]
    public void AsQueueStorageBenzeneMessage_XmlSerializer_DoesNotThrow_AndMatchesEventHubShape()
    {
        // Before the fix: this threw (attempting to XML-serialize the envelope, whose Headers is an
        // IDictionary<string,string> interface XmlSerializer cannot handle).
        var message = MessageBuilder.Create("hello:world", new SmokeMessage { Name = "World" })
            .AsQueueStorageBenzeneMessage(new XmlSerializer());

        // The envelope itself must still be JSON (fixed, independent of the passed serializer) -
        // matching AsEventHubBenzeneMessage's contract.
        var envelope = new JsonSerializer().Deserialize<EnvelopeShape>(message.MessageText);
        Assert.Equal("hello:world", envelope!.Topic);

        // Only the Body is XML.
        var body = new XmlSerializer().Deserialize<SmokeMessage>(envelope.Body);
        Assert.Equal("World", body!.Name);
    }

    // Mirrors BenzeneMessageRequest's wire shape without depending on it directly, so this test
    // stays a black-box check of what actually landed in the queue message text.
    private class EnvelopeShape
    {
        public string Topic { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new();
        public string Body { get; set; } = string.Empty;
    }
}
