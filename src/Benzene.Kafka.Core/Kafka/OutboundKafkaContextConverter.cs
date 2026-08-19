using System.Text;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using JsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;
using Benzene.Results;
using Confluent.Kafka;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>
/// Converts between an outbound <see cref="OutboundContext"/> and a
/// <see cref="KafkaSendMessageContext"/>, so an outbound route (<c>OutboundRoutingBuilder.Route</c>)
/// can produce via Kafka. The <see cref="OutboundContext"/> counterpart of
/// <see cref="KafkaContextConverter{T}"/>, and the same shape every other transport's outbound
/// converter follows (see <c>Benzene.Clients.Aws.Sqs.OutboundSqsContextConverter</c>).
/// </summary>
/// <remarks>
/// The context's <c>Topic</c> is the Kafka topic produced to. Kafka has no request/response semantics
/// beyond a produce acknowledgement, so the response this converter produces is always
/// <see cref="IBenzeneResult{Void}"/> - a topic routed here must be sent via
/// <c>IBenzeneMessageSender.SendAsync&lt;TRequest,Void&gt;</c>.
/// </remarks>
public class OutboundKafkaContextConverter : IContextConverter<OutboundContext, KafkaSendMessageContext>
{
    private readonly ISerializer _serializer;
    private readonly string? _keyHeader;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundKafkaContextConverter"/> class using a
    /// <see cref="JsonSerializer"/> to serialize the outgoing message.
    /// </summary>
    /// <param name="keyHeader">
    /// The outbound header whose value becomes the Kafka message key (hash(key) → partition, so events
    /// sharing a key land on the same partition and are ordered there). <c>null</c> (the default) sends
    /// a keyless message (round-robin partitioning, no per-key ordering).
    /// </param>
    public OutboundKafkaContextConverter(string? keyHeader = null)
        : this(new JsonSerializer(), keyHeader)
    { }

    /// <summary>Initializes a new instance of the <see cref="OutboundKafkaContextConverter"/> class.</summary>
    /// <param name="serializer">The serializer used to serialize the outgoing message.</param>
    /// <param name="keyHeader">
    /// The outbound header whose value becomes the Kafka message key. <c>null</c> sends a keyless
    /// message.
    /// </param>
    public OutboundKafkaContextConverter(ISerializer serializer, string? keyHeader = null)
    {
        _serializer = serializer;
        _keyHeader = keyHeader;
    }

    /// <summary>
    /// Builds a Kafka produce request, serializing the outgoing message as the value and forwarding the
    /// outbound headers onto the record's headers.
    /// </summary>
    /// <param name="contextIn">The outbound context to convert.</param>
    /// <returns>A task that resolves to the built <see cref="KafkaSendMessageContext"/>.</returns>
    public Task<KafkaSendMessageContext> CreateRequestAsync(OutboundContext contextIn)
    {
        var headers = new Headers();
        foreach (var header in contextIn.Headers)
        {
            // A null header value is a valid dictionary state but Encoding.UTF8.GetBytes(null) throws,
            // which would hard-fail the whole produce. Coalesce to empty, matching the inbound getters'
            // null->empty convention.
            headers.Add(header.Key, Encoding.UTF8.GetBytes(header.Value ?? string.Empty));
        }

        string? key = null;
        if (_keyHeader != null)
        {
            contextIn.Headers.TryGetValue(_keyHeader, out key);
        }

        return Task.FromResult(new KafkaSendMessageContext(contextIn.Topic,
            new Message<string, string>
            {
                Key = key!,
                Value = _serializer.Serialize(contextIn.Request),
                Headers = headers
            }));
    }

    /// <summary>Maps the produce outcome back onto the outbound context as an <see cref="IBenzeneResult{Void}"/>.</summary>
    /// <param name="contextIn">The outbound context to set the response on.</param>
    /// <param name="contextOut">The completed <see cref="KafkaSendMessageContext"/>.</param>
    /// <returns>A completed task.</returns>
    public Task MapResponseAsync(OutboundContext contextIn, KafkaSendMessageContext contextOut)
    {
        contextIn.Response = contextOut.Response?.Status == PersistenceStatus.Persisted
            ? BenzeneResult.Accepted<Void>()
            : BenzeneResult.ServiceUnavailable<Void>("Kafka message was not persisted");
        return Task.CompletedTask;
    }
}
