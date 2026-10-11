using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.Azure.EventHub;

/// <summary>
/// Extracts headers from an event's string-typed properties.
/// </summary>
public class EventHubConsumerMessageHeadersGetter : IMessageHeadersGetter<EventHubConsumerContext>
{
    private readonly string _topicPropertyKey;

    /// <summary>
    /// Initializes a new instance that leaves out the default topic event property
    /// (<see cref="EventHubConsumerMessageTopicGetter.DefaultTopicProperty"/>, <c>topic</c>) - the key <see cref="EventHubConsumerMessageTopicGetter"/> consumes.
    /// </summary>
    public EventHubConsumerMessageHeadersGetter()
        : this(EventHubConsumerMessageTopicGetter.DefaultTopicProperty)
    { }

    /// <summary>
    /// Initializes a new instance that leaves out <paramref name="topicPropertyKey"/>: the topic is routing
    /// metadata, consumed by the topic getter, so it does not reappear in the header dictionary
    /// (transport-metadata-cases.json <c>headersExclude</c>). Pass the same key the topic getter reads.
    /// </summary>
    /// <param name="topicPropertyKey">The event property the topic is carried on (matched case-insensitively).</param>
    public EventHubConsumerMessageHeadersGetter(string topicPropertyKey)
    {
        _topicPropertyKey = topicPropertyKey;
    }

    /// <summary>
    /// Gets the headers for the event from its string-typed properties.
    /// </summary>
    /// <param name="context">The Event Hub consumer context to extract headers from.</param>
    /// <returns>The message headers.</returns>
    public IDictionary<string, string> GetHeaders(EventHubConsumerContext context)
    {
        // OrdinalIgnoreCase, matching every other built-in getter's headers dictionary (#165) -
        // ToDictionary with no comparer built a plain-ordinal (case-sensitive) dictionary here.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in context.EventData.Properties)
        {
            if (MetadataKeys.Matches(property.Key, _topicPropertyKey))
            {
                continue;
            }

            if (property.Value is string value)
            {
                headers[property.Key] = value;
            }
        }

        return headers;
    }
}
