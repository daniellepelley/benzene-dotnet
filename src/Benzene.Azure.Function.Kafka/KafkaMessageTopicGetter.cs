using System.Text;
using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages;
using Benzene.Core.Messages;

namespace Benzene.Azure.Function.Kafka;

/// <summary>
/// Resolves the inbound topic of a Kafka record: the record's <c>topic</c> header when present (so one
/// Kafka topic can carry several Benzene topics), falling back to the Kafka topic the record was consumed
/// from (so a producer that writes no header - a non-Benzene one, or one that names its Kafka topics after
/// Benzene topics - still routes). The header key is matched case-insensitively and, when repeated, the
/// last value wins (transport-bindings, Kafka; transport-metadata-cases.json topicSources).
/// </summary>
public class KafkaMessageTopicGetter : IMessageTopicGetter<KafkaContext>
{
    /// <summary>
    /// The default header the topic is read from (<c>topic</c>, wire-contracts §2). It wins when present;
    /// the Kafka topic the record was consumed from is the fallback (transport-bindings, Kafka).
    /// </summary>
    public const string DefaultTopicHeader = BenzeneWireNames.DefaultTopic;

    private readonly string _topicHeaderKey;

    /// <summary>Initializes a new instance that reads the default <c>topic</c> header.</summary>
    public KafkaMessageTopicGetter()
        : this(DefaultTopicHeader)
    { }

    /// <summary>Initializes a new instance that reads the topic from <paramref name="topicHeaderKey"/>.</summary>
    /// <param name="topicHeaderKey">The record header the topic is carried on (matched case-insensitively).</param>
    public KafkaMessageTopicGetter(string topicHeaderKey)
    {
        _topicHeaderKey = topicHeaderKey;
    }

    /// <summary>
    /// Gets the topic from the Kafka event's topic header, falling back to the event's Kafka topic.
    /// </summary>
    /// <param name="context">The Kafka context to extract the topic from.</param>
    /// <returns>The topic.</returns>
    public ITopic GetTopic(KafkaContext context)
    {
        return new Topic(GetTopicHeader(context) ?? context.KafkaEvent.Topic);
    }

    private string? GetTopicHeader(KafkaContext context)
    {
        string? value = null;
        var headers = context.KafkaEvent.Headers;
        if (headers == null)
        {
            return null;
        }

        foreach (var header in headers)
        {
            if (MetadataKeys.Matches(header.Key, _topicHeaderKey))
            {
                value = Encoding.UTF8.GetString(header.Value ?? System.Array.Empty<byte>());
            }
        }

        return value;
    }
}
