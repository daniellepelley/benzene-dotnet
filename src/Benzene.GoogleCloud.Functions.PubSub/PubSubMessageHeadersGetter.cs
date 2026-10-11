using System;
using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.GoogleCloud.Functions.PubSub;

/// <summary>
/// Extracts headers from a Pub/Sub message's attributes.
/// </summary>
public class PubSubMessageHeadersGetter : IMessageHeadersGetter<PubSubContext>
{
    private readonly string _topicAttributeKey;

    /// <summary>
    /// Initializes a new instance that leaves out the default topic attribute
    /// (<see cref="PubSubMessageTopicGetter.DefaultTopicAttribute"/>, <c>topic</c>) - the key <see cref="PubSubMessageTopicGetter"/> consumes.
    /// </summary>
    public PubSubMessageHeadersGetter()
        : this(PubSubMessageTopicGetter.DefaultTopicAttribute)
    { }

    /// <summary>
    /// Initializes a new instance that leaves out <paramref name="topicAttributeKey"/>: the topic is routing
    /// metadata, consumed by the topic getter, so it does not reappear in the header dictionary
    /// (transport-metadata-cases.json <c>headersExclude</c>). Pass the same key the topic getter reads.
    /// </summary>
    /// <param name="topicAttributeKey">The attribute the topic is carried on (matched case-insensitively).</param>
    public PubSubMessageHeadersGetter(string topicAttributeKey)
    {
        _topicAttributeKey = topicAttributeKey;
    }

    /// <summary>
    /// Gets the headers for the Pub/Sub message from its attributes.
    /// </summary>
    /// <param name="context">The Pub/Sub context to extract headers from.</param>
    /// <returns>
    /// The message headers, case-insensitively keyed. Empty (not throwing) if the CloudEvent
    /// carried no Pub/Sub message, or a message with no attributes at all - matching the SNS/SQS
    /// getters' hardening for the equivalent malformed-delivery case.
    /// </returns>
    public IDictionary<string, string> GetHeaders(PubSubContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (context.Message?.Attributes != null)
        {
            foreach (var attribute in context.Message.Attributes)
            {
                if (MetadataKeys.Matches(attribute.Key, _topicAttributeKey))
                {
                    continue;
                }

                headers[attribute.Key] = attribute.Value;
            }
        }

        return headers;
    }
}
