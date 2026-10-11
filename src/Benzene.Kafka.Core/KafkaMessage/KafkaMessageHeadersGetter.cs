using System.Text;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.Kafka.Core.KafkaMessage;

/// <summary>Reads the inbound message headers from a <see cref="KafkaRecordContext{TKey,TValue}"/>.</summary>
public class KafkaMessageHeadersGetter<TKey, TValue> : IMessageHeadersGetter<KafkaRecordContext<TKey, TValue>>
{
    private readonly string _topicHeaderKey;

    /// <summary>
    /// Initializes a new instance that leaves out the default topic header (<c>topic</c>) - the key
    /// <see cref="KafkaMessageTopicGetter{TKey,TValue}"/> consumes.
    /// </summary>
    public KafkaMessageHeadersGetter()
        : this(Benzene.Abstractions.BenzeneWireNames.DefaultTopic)
    { }

    /// <summary>
    /// Initializes a new instance that leaves out <paramref name="topicHeaderKey"/>: the topic is routing
    /// metadata, consumed by the topic getter, so it does not reappear in the header dictionary
    /// (transport-metadata-cases.json <c>headersExclude</c>). Pass the same key the topic getter reads.
    /// </summary>
    /// <param name="topicHeaderKey">The record header the topic is carried on (matched case-insensitively).</param>
    public KafkaMessageHeadersGetter(string topicHeaderKey)
    {
        _topicHeaderKey = topicHeaderKey;
    }

    /// <inheritdoc />
    public IDictionary<string, string> GetHeaders(KafkaRecordContext<TKey, TValue> context)
    {
        // Kafka headers are an ordered list that legitimately permits repeated keys, so build the
        // dictionary with a last-wins indexer rather than ToDictionary (which throws on a duplicate
        // key, making a valid record unprocessable). Matches the RabbitMq/gRPC header getters.
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in context.ConsumeResult.Message.Headers)
        {
            if (MetadataKeys.Matches(header.Key, _topicHeaderKey))
            {
                continue;
            }

            dictionary[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes() ?? System.Array.Empty<byte>());
        }

        return dictionary;
    }
}