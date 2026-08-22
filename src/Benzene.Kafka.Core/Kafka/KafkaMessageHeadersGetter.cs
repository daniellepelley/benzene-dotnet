using System.Text;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>Reads the outbound message headers from a <see cref="KafkaSendMessageContext"/>.</summary>
public class KafkaSendMessageHeadersGetter : IMessageHeadersGetter<KafkaSendMessageContext>
{
    /// <inheritdoc />
    public IDictionary<string, string> GetHeaders(KafkaSendMessageContext context)
    {
        // Last-wins over the ordered header list (Kafka permits duplicate keys); ToDictionary would
        // throw on a duplicate. Matches the inbound getter and the RabbitMq/gRPC getters.
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in context.Message.Headers)
        {
            dictionary[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes() ?? System.Array.Empty<byte>());
        }

        return dictionary;
    }
}