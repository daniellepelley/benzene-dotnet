using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.Azure.Function.EventHub.Function;

/// <summary>
/// Extracts headers from an event's string-typed properties.
/// </summary>
public class EventHubMessageHeadersGetter : IMessageHeadersGetter<EventHubContext>
{
    private readonly string _topicPropertyKey;

    /// <summary>
    /// Initializes a new instance that leaves out the default topic event property
    /// (<see cref="EventHubMessageTopicGetter.DefaultTopicProperty"/>, <c>topic</c>) - the key <see cref="EventHubMessageTopicGetter"/> consumes.
    /// </summary>
    public EventHubMessageHeadersGetter()
        : this(EventHubMessageTopicGetter.DefaultTopicProperty)
    { }

    /// <summary>
    /// Initializes a new instance that leaves out <paramref name="topicPropertyKey"/>: the topic is routing
    /// metadata, consumed by the topic getter, so it does not reappear in the header dictionary
    /// (transport-metadata-cases.json <c>headersExclude</c>). Pass the same key the topic getter reads.
    /// </summary>
    /// <param name="topicPropertyKey">The event property the topic is carried on (matched case-insensitively).</param>
    public EventHubMessageHeadersGetter(string topicPropertyKey)
    {
        _topicPropertyKey = topicPropertyKey;
    }

    /// <summary>
    /// Gets the headers for the event from its string-typed properties.
    /// </summary>
    /// <param name="context">The Event Hub context to extract headers from.</param>
    /// <returns>The message headers.</returns>
    public IDictionary<string, string> GetHeaders(EventHubContext context)
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
