using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Azure.EventHub;

/// <summary>
/// Extracts headers from an event's string-typed properties.
/// </summary>
public class EventHubConsumerMessageHeadersGetter : IMessageHeadersGetter<EventHubConsumerContext>
{
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
            if (property.Value is string value)
            {
                headers[property.Key] = value;
            }
        }

        return headers;
    }
}
