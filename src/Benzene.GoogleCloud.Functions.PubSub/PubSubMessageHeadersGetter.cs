using System;
using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.GoogleCloud.Functions.PubSub;

/// <summary>
/// Extracts headers from a Pub/Sub message's attributes.
/// </summary>
public class PubSubMessageHeadersGetter : IMessageHeadersGetter<PubSubContext>
{
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
                headers[attribute.Key] = attribute.Value;
            }
        }

        return headers;
    }
}
