using System.Text;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using JsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.RabbitMq.RabbitMqSendMessage;

/// <summary>
/// Converts between an outbound <see cref="OutboundContext"/> and a
/// <see cref="RabbitMqSendMessageContext"/>, so an outbound route (<c>OutboundRoutingBuilder.Route</c>)
/// can publish via RabbitMQ. The <see cref="OutboundContext"/> counterpart of
/// <see cref="RabbitMqContextConverter{T}"/>, and the same shape every other transport's outbound
/// converter follows (see <c>Benzene.Clients.Aws.Sqs.OutboundSqsContextConverter</c>).
/// </summary>
/// <remarks>
/// The context's <c>Topic</c> becomes the AMQP routing key and is also carried as a header, so a
/// <see cref="RabbitMqWorker"/> consuming the message routes by header (portable) with the routing key
/// as the idiomatic fallback. RabbitMQ has no request/response semantics beyond a publish
/// acknowledgement, so the response this converter produces is always <c>IBenzeneResult&lt;Void&gt;</c> -
/// a topic routed here must be sent via <c>IBenzeneMessageSender.SendAsync&lt;TRequest,Void&gt;</c>.
/// </remarks>
public class OutboundRabbitMqContextConverter : IContextConverter<OutboundContext, RabbitMqSendMessageContext>
{
    private readonly ISerializer _serializer;
    private readonly string _exchange;
    private readonly string _topicHeaderKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundRabbitMqContextConverter"/> class using a
    /// <see cref="JsonSerializer"/> to serialize the outgoing message.
    /// </summary>
    /// <param name="exchange">The exchange to publish to. Empty string (the default) uses the default exchange, where the routing key is the target queue name.</param>
    /// <param name="topicHeaderKey">The message-property header the topic is written to (defaults to <see cref="RabbitMqConstants.DefaultTopicHeader"/>).</param>
    public OutboundRabbitMqContextConverter(string exchange = "", string topicHeaderKey = RabbitMqConstants.DefaultTopicHeader)
        : this(new JsonSerializer(), exchange, topicHeaderKey)
    { }

    /// <summary>Initializes a new instance of the <see cref="OutboundRabbitMqContextConverter"/> class.</summary>
    /// <param name="serializer">The serializer used to encode the message body.</param>
    /// <param name="exchange">The exchange to publish to. Empty string uses the default exchange, where the routing key is the target queue name.</param>
    /// <param name="topicHeaderKey">
    /// The message-property header the topic is written to. Defaults to
    /// <see cref="RabbitMqConstants.DefaultTopicHeader"/> - pass a different key to publish for a
    /// non-Benzene consumer that routes on another header (keep it in sync with the consumer's
    /// <see cref="RabbitMqMessage.RabbitMqMessageTopicGetter"/> key).
    /// </param>
    public OutboundRabbitMqContextConverter(ISerializer serializer, string exchange, string topicHeaderKey = RabbitMqConstants.DefaultTopicHeader)
    {
        _serializer = serializer;
        _exchange = exchange;
        _topicHeaderKey = topicHeaderKey;
    }

    /// <summary>
    /// Builds a RabbitMQ publish, serializing the outgoing message as the body and forwarding the
    /// outbound headers (plus the topic) onto <c>BasicProperties.Headers</c>.
    /// </summary>
    /// <param name="contextIn">The outbound context to convert.</param>
    /// <returns>A task that resolves to the built <see cref="RabbitMqSendMessageContext"/>.</returns>
    public Task<RabbitMqSendMessageContext> CreateRequestAsync(OutboundContext contextIn)
    {
        var headers = new Dictionary<string, object?>();
        foreach (var header in contextIn.Headers)
        {
            // Null-coalesce like the Kafka converter does: a null header value is publishable as
            // empty rather than throwing ArgumentNullException.
            headers[header.Key] = Encoding.UTF8.GetBytes(header.Value ?? string.Empty);
        }

        // Carry the topic as a header too, so a Benzene RabbitMQ consumer's header-first topic getter
        // round-trips it regardless of the routing key the exchange binding uses.
        headers[_topicHeaderKey] = Encoding.UTF8.GetBytes(contextIn.Topic);

        var body = Encoding.UTF8.GetBytes(_serializer.Serialize(contextIn.Request));

        return Task.FromResult(new RabbitMqSendMessageContext(_exchange, contextIn.Topic, body, headers));
    }

    /// <summary>Maps the publish outcome back onto the outbound context as an <c>IBenzeneResult&lt;Void&gt;</c>.</summary>
    /// <param name="contextIn">The outbound context to set the response on.</param>
    /// <param name="contextOut">The completed <see cref="RabbitMqSendMessageContext"/>.</param>
    /// <returns>A completed task.</returns>
    public Task MapResponseAsync(OutboundContext contextIn, RabbitMqSendMessageContext contextOut)
    {
        contextIn.Response = contextOut.Published
            ? BenzeneResult.Accepted<Void>()
            : BenzeneResult.ServiceUnavailable<Void>("RabbitMQ message was not published");
        return Task.CompletedTask;
    }
}
