using System;
using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.Aws.Lambda.Sns;

/// <summary>
/// Extracts headers from an SNS record's message attributes.
/// </summary>
public class SnsMessageHeadersGetter : IMessageHeadersGetter<SnsRecordContext>
{
    private readonly string _topicAttributeKey;

    /// <summary>
    /// Initializes a new instance that leaves out the default topic message attribute
    /// (<see cref="SnsMessageTopicGetter.DefaultTopicAttribute"/>, <c>topic</c>) - the key <see cref="SnsMessageTopicGetter"/> consumes.
    /// </summary>
    public SnsMessageHeadersGetter()
        : this(SnsMessageTopicGetter.DefaultTopicAttribute)
    { }

    /// <summary>
    /// Initializes a new instance that leaves out <paramref name="topicAttributeKey"/>: the topic is routing
    /// metadata, consumed by the topic getter, so it does not reappear in the header dictionary
    /// (transport-metadata-cases.json <c>headersExclude</c>). Pass the same key the topic getter reads.
    /// </summary>
    /// <param name="topicAttributeKey">The message attribute the topic is carried on (matched case-insensitively).</param>
    public SnsMessageHeadersGetter(string topicAttributeKey)
    {
        _topicAttributeKey = topicAttributeKey;
    }

    /// <summary>
    /// Gets the SNS message attributes as headers.
    /// </summary>
    /// <param name="context">The SNS record context to extract headers from.</param>
    /// <returns>A dictionary of header names to values.</returns>
    public IDictionary<string, string> GetHeaders(SnsRecordContext context)
    {
        // Null-safe, matching SnsUtils.GetFromAttributes (used by the topic getter for the same
        // object): an SNS record deserialized from a payload with no MessageAttributes field has a
        // null MessageAttributes, and the topic path handles that gracefully - the headers path must
        // too, rather than NRE-ing out of the invocation on the same record.
        //
        // OrdinalIgnoreCase on both branches - previously only the empty-dictionary fallback used
        // it, so the comparer (and therefore header-name case-sensitivity) silently depended on
        // whether the record happened to carry any attributes.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (context.SnsRecord.Sns?.MessageAttributes != null)
        {
            foreach (var attribute in context.SnsRecord.Sns.MessageAttributes)
            {
                if (MetadataKeys.Matches(attribute.Key, _topicAttributeKey))
                {
                    continue;
                }

                headers[attribute.Key] = attribute.Value.Value;
            }
        }

        return headers;
    }
}
