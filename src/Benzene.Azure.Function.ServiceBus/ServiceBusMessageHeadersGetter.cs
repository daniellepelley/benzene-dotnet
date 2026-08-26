using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Azure.Function.ServiceBus;

/// <summary>
/// Extracts headers from a Service Bus message's string-typed application properties.
/// </summary>
public class ServiceBusMessageHeadersGetter : IMessageHeadersGetter<ServiceBusContext>
{
    /// <summary>
    /// Gets the headers for the Service Bus message from its string-typed application properties.
    /// </summary>
    /// <param name="context">The Service Bus context to extract headers from.</param>
    /// <returns>The message headers.</returns>
    public IDictionary<string, string> GetHeaders(ServiceBusContext context)
    {
        // OrdinalIgnoreCase, matching every other built-in getter's headers dictionary (#165) -
        // ToDictionary with no comparer built a plain-ordinal (case-sensitive) dictionary here.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in context.Message.ApplicationProperties)
        {
            if (property.Value is string value)
            {
                headers[property.Key] = value;
            }
        }

        return headers;
    }
}
