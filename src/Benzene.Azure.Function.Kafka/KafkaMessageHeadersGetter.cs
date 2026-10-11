using System.Linq;
using System.Text;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.Azure.Function.Kafka;

/// <summary>
/// Extracts headers from a Kafka event's <c>KafkaRecord.Headers</c>, UTF-8 decoding each value.
/// </summary>
public class KafkaMessageHeadersGetter : IMessageHeadersGetter<KafkaContext>
{
    private readonly string _topicHeaderKey;

    /// <summary>
    /// Initializes a new instance that leaves out the default topic header (<c>topic</c>) - the key
    /// <see cref="KafkaMessageTopicGetter"/> consumes.
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

    /// <summary>
    /// Gets the decoded headers from the Kafka record.
    /// </summary>
    /// <param name="context">The Kafka context to extract headers from.</param>
    /// <returns>A dictionary of header names to UTF-8 decoded values. Empty when the record has no headers.</returns>
    public IDictionary<string, string> GetHeaders(KafkaContext context)
    {
        var headers = context.KafkaEvent.Headers;

        if (headers == null || headers.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Last-wins over the header array (Kafka permits duplicate keys); ToDictionary would throw on a
        // duplicate, aborting the record. Matches the AWS Lambda Kafka and Kafka.Core getters.
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (MetadataKeys.Matches(header.Key, _topicHeaderKey))
            {
                continue;
            }

            dictionary[header.Key] = Encoding.UTF8.GetString(header.Value ?? System.Array.Empty<byte>());
        }

        return dictionary;
    }
}
