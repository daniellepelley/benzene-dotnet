using System;
using System.Collections.Generic;
using System.Text;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.Aws.Lambda.Kafka;

/// <summary>
/// Extracts every header from a Kafka record, UTF-8 decoding each value.
/// </summary>
/// <remarks>
/// The topic header is <em>routing</em> metadata, consumed by <see cref="KafkaMessageTopicGetter"/> (which
/// falls back to the record's own Kafka topic) - it is deliberately NOT surfaced as a header. Forwarding a
/// handled message's headers onto an outbound send therefore never carries its inbound topic along, which -
/// if that send targets a topic the same service consumes - would be an infinite-loop trap. An outbound
/// topic must be set explicitly, never inherited from the message being handled.
/// </remarks>
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
        var headerBatches = context.KafkaEventRecord.Headers;

        if (headerBatches == null || headerBatches.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // The AWS Kafka wire format emits each record header as a separate single-entry element in the
        // Headers list (preserving Kafka's ordered, duplicate-key-capable headers). Flatten every
        // element rather than taking only the first (which dropped all headers after the first), and use
        // a last-wins indexer so a duplicate key doesn't throw (Kafka permits repeated keys).
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in headerBatches)
        {
            if (batch == null)
            {
                continue;
            }

            foreach (var header in batch)
            {
                if (MetadataKeys.Matches(header.Key, _topicHeaderKey))
                {
                    continue;
                }

                dictionary[header.Key] = Encoding.UTF8.GetString(header.Value ?? Array.Empty<byte>());
            }
        }

        return dictionary;
    }
}
