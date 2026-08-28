using Benzene.Azure.Function.QueueStorage;
using Xunit;

namespace Benzene.Test.Azure;

// No dedicated getter test file existed for this transport before round 14-15's #235 pass flagged
// the broad malformed-input coverage gap across test/Benzene.Core.Test/Azure/ (see
// work/bug-fix-designs-round15-2026-08.md §3) - mirrors KafkaGettersTest.cs's shape for the transport
// that had none.
public class QueueStorageGettersTest
{
    [Fact]
    public void QueueStorageMessageBodyGetter_ReturnsTheMessageText()
    {
        var context = new QueueStorageContext(new QueueStorageMessage("hello"));

        Assert.Equal("hello", new QueueStorageMessageBodyGetter().GetBody(context));
    }

    // Malformed-input coverage gap, closed: QueueStorageMessageBodyGetter does no parsing of its own
    // (unlike EventGrid's Parse) - it passes the raw message text straight through untouched. This
    // confirms that rather than assuming it: malformed/truncated JSON in the message text is not
    // rejected or altered here - any envelope validation happens downstream (the request mapper, or
    // BenzeneMessageQueueStorageHandler's envelope deserialization on the UseBenzeneMessage path), not
    // in this getter. No latent bug found - this getter cannot itself be broken by a malformed body
    // because it does not interpret the body at all.
    [Fact]
    public void QueueStorageMessageBodyGetter_MalformedJsonText_ReturnsRawTextUnparsedAndUnrejected()
    {
        const string malformed = "{ \"topic\": \"orders\", \"body\": { unterminated";
        var context = new QueueStorageContext(new QueueStorageMessage(malformed));

        Assert.Equal(malformed, new QueueStorageMessageBodyGetter().GetBody(context));
    }

    [Fact]
    public void QueueStorageMessageTopicGetter_AlwaysReturnsNull()
    {
        var context = new QueueStorageContext(new QueueStorageMessage("{ malformed"));

        Assert.Null(new QueueStorageMessageTopicGetter().GetTopic(context));
    }

    [Fact]
    public void QueueStorageMessageHeadersGetter_AlwaysReturnsAnEmptyDictionary()
    {
        var context = new QueueStorageContext(new QueueStorageMessage("{ malformed"));

        Assert.Empty(new QueueStorageMessageHeadersGetter().GetHeaders(context));
    }
}
