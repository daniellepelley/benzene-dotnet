using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;

namespace Benzene.Azure.ServiceBus;

/// <summary>
/// Extracts headers from a Service Bus message's string-typed application properties.
/// </summary>
public class ServiceBusConsumerMessageHeadersGetter : IMessageHeadersGetter<ServiceBusConsumerContext>
{
    private readonly string _topicPropertyKey;

    /// <summary>
    /// Initializes a new instance that leaves out the default topic application property
    /// (<see cref="ServiceBusConsumerMessageTopicGetter.DefaultTopicProperty"/>, <c>topic</c>) - the key <see cref="ServiceBusConsumerMessageTopicGetter"/> consumes.
    /// </summary>
    public ServiceBusConsumerMessageHeadersGetter()
        : this(ServiceBusConsumerMessageTopicGetter.DefaultTopicProperty)
    { }

    /// <summary>
    /// Initializes a new instance that leaves out <paramref name="topicPropertyKey"/>: the topic is routing
    /// metadata, consumed by the topic getter, so it does not reappear in the header dictionary
    /// (transport-metadata-cases.json <c>headersExclude</c>). Pass the same key the topic getter reads.
    /// </summary>
    /// <param name="topicPropertyKey">The application property the topic is carried on (matched case-insensitively).</param>
    public ServiceBusConsumerMessageHeadersGetter(string topicPropertyKey)
    {
        _topicPropertyKey = topicPropertyKey;
    }

    /// <summary>
    /// Gets the headers for the Service Bus message from its string-typed application properties.
    /// </summary>
    /// <param name="context">The Service Bus consumer context to extract headers from.</param>
    /// <returns>The message headers.</returns>
    public IDictionary<string, string> GetHeaders(ServiceBusConsumerContext context)
    {
        // OrdinalIgnoreCase, matching every other built-in getter's headers dictionary (#165) -
        // ToDictionary with no comparer built a plain-ordinal (case-sensitive) dictionary here.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in context.Message.ApplicationProperties)
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
