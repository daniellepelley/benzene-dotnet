using System;
using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.Aws.Sqs.Consumer;

/// <summary>
/// Extracts headers from an SQS message's string-typed message attributes.
/// </summary>
public class SqsConsumerMessageHeadersGetter : IMessageHeadersGetter<SqsConsumerMessageContext>
{
    private readonly string _topicAttributeKey;

    /// <summary>
    /// Initializes a new instance that leaves out the default topic message attribute
    /// (<see cref="SqsConsumerMessageTopicGetter.DefaultTopicAttribute"/>, <c>topic</c>) - the key <see cref="SqsConsumerMessageTopicGetter"/> consumes.
    /// </summary>
    public SqsConsumerMessageHeadersGetter()
        : this(SqsConsumerMessageTopicGetter.DefaultTopicAttribute)
    { }

    /// <summary>
    /// Initializes a new instance that leaves out <paramref name="topicAttributeKey"/>: the topic is routing
    /// metadata, consumed by the topic getter, so it does not reappear in the header dictionary
    /// (transport-metadata-cases.json <c>headersExclude</c>). Pass the same key the topic getter reads.
    /// </summary>
    /// <param name="topicAttributeKey">The message attribute the topic is carried on (matched case-insensitively).</param>
    public SqsConsumerMessageHeadersGetter(string topicAttributeKey)
    {
        _topicAttributeKey = topicAttributeKey;
    }

    /// <summary>
    /// Gets the string-typed message attributes as headers.
    /// </summary>
    /// <param name="context">The SQS consumer message context to extract headers from.</param>
    /// <returns>A dictionary of header names to values, limited to attributes with a <c>String</c> data type.</returns>
    public IDictionary<string, string> GetHeaders(SqsConsumerMessageContext context)
    {
        // OrdinalIgnoreCase on both branches - previously only the null-attributes fallback used it,
        // so the comparer (and therefore header-name case-sensitivity) silently depended on whether
        // the message happened to carry any attributes (#165).
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Null-guard the attribute map (a message deserialized from a payload with no attributes can
        // yield null), matching the SNS getter's hardening rather than NRE-ing out of the invocation.
        if (context.Message.MessageAttributes != null)
        {
            foreach (var attribute in context.Message.MessageAttributes)
            {
                if (MetadataKeys.Matches(attribute.Key, _topicAttributeKey))
                {
                    continue;
                }

                if (attribute.Value.DataType == "String")
                {
                    headers[attribute.Key] = attribute.Value.StringValue;
                }
            }
        }

        return headers;
    }
}
