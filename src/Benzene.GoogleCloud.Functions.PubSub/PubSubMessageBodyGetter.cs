using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.GoogleCloud.Functions.PubSub;

/// <summary>
/// Extracts the message body from a Pub/Sub message's data payload.
/// </summary>
public class PubSubMessageBodyGetter : IMessageBodyGetter<PubSubContext>
{
    /// <summary>
    /// Gets the Pub/Sub message's data payload, UTF-8 decoded, as the message body.
    /// </summary>
    /// <param name="context">The Pub/Sub context to extract the body from.</param>
    /// <returns>
    /// The message body, or <c>null</c> if the CloudEvent carried no Pub/Sub message at all (a
    /// malformed/synthetic delivery) - matching the SNS/SQS getters' hardening rather than throwing.
    /// </returns>
    public string? GetBody(PubSubContext context)
    {
        return context.Message?.TextData;
    }
}
