using Benzene.Abstractions.Middleware;
using RabbitMQ.Client;

namespace Benzene.RabbitMq.RabbitMqSendMessage;

/// <summary>
/// The transport middleware at the bottom of an outbound RabbitMQ pipeline: publishes the
/// <see cref="RabbitMqSendMessageContext"/> to its exchange/routing key via the shared
/// <see cref="IChannel"/>, forwarding the Benzene headers onto <c>BasicProperties</c>.
/// </summary>
public class RabbitMqClientMiddleware : IMiddleware<RabbitMqSendMessageContext>, ITerminalMiddleware
{
    private readonly IChannel _channel;
    private readonly bool _mandatory;
    private readonly bool _persistent;
    private readonly TimeSpan? _publishConfirmTimeout;
    private readonly RabbitMqMandatoryPublishCoordinator? _coordinator;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqClientMiddleware"/> class.</summary>
    /// <param name="channel">The RabbitMQ channel to publish on.</param>
    /// <param name="mandatory">
    /// When <c>true</c>, an unroutable message (no queue bound for the routing key) is returned by the
    /// broker rather than silently dropped, and the returned message resolves <see cref="RabbitMqSendMessageContext.Published"/>
    /// to <c>false</c> - a real, awaited outcome, not just a documented promise: see
    /// <see cref="RabbitMqMandatoryPublishCoordinator"/>. <paramref name="channel"/> MUST have publisher
    /// confirmations enabled (<c>CreateChannelOptions.PublisherConfirmationsEnabled = true</c>) or
    /// construction throws - a returned message can only be reliably correlated back to its publish when
    /// confirms sequence the channel's publishes. Defaults to <c>false</c> (fire-and-forget; any
    /// <c>IChannel</c> is accepted).
    /// </param>
    /// <param name="persistent">
    /// When <c>true</c> (the default), the message is published with delivery mode 2 (persistent), so a
    /// message on a durable queue survives a broker restart. Set <c>false</c> for transient delivery
    /// (lower overhead, but the message is lost on restart even on a durable queue).
    /// </param>
    /// <param name="publishConfirmTimeout">
    /// Only applies when <paramref name="mandatory"/> is <c>true</c>. The most this middleware will wait
    /// for the broker's confirmation of a single publish before failing it with a
    /// <see cref="TimeoutException"/>, so a stalled/unresponsive broker cannot hang the caller forever
    /// (task board #45). Defaults to <see cref="RabbitMqMandatoryPublishCoordinator.DefaultPublishConfirmTimeout"/>
    /// (30 seconds) when not given.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="mandatory"/> is <c>true</c> and <paramref name="channel"/> does not have publisher
    /// confirmations enabled.
    /// </exception>
    public RabbitMqClientMiddleware(IChannel channel, bool mandatory = false, bool persistent = true,
        TimeSpan? publishConfirmTimeout = null)
    {
        _channel = channel;
        _mandatory = mandatory;
        _persistent = persistent;
        _publishConfirmTimeout = publishConfirmTimeout;

        // Fail fast here too (not only in Extensions.UseRabbitMqClient) so a caller constructing this
        // middleware directly - bypassing the extension method - gets the same guarantee. Memoized per
        // channel by RabbitMqMandatoryPublishCoordinator (this constructor runs on every publish - see
        // the pipeline's per-call middleware resolution - so this must stay cheap after the first time).
        _coordinator = _mandatory ? RabbitMqMandatoryPublishCoordinator.GetOrCreate(channel) : null;
    }

    /// <inheritdoc />
    public string Name => nameof(RabbitMqClientMiddleware);

    /// <inheritdoc />
    public async Task HandleAsync(RabbitMqSendMessageContext context, Func<Task> next)
    {
        var properties = new BasicProperties
        {
            Headers = context.Headers,
            Persistent = _persistent,
        };

        if (_mandatory)
        {
            // The correlation key a returned message is matched back to this publish by: AMQP's
            // Basic.Return carries the message's properties back but no delivery tag, so without a
            // stable identifier here there would be no way to tell which in-flight publish a given
            // return belongs to. Only stamped if the caller didn't already set one, so an existing
            // MessageId (e.g. one carrying business meaning) is preserved.
            properties.MessageId ??= Guid.NewGuid().ToString();

            context.Published = await _coordinator!
                .PublishMandatoryAsync(context.Exchange, context.RoutingKey, properties, context.Body,
                    CancellationToken.None, _publishConfirmTimeout)
                .ConfigureAwait(false);
            return;
        }

        await _channel.BasicPublishAsync(context.Exchange, context.RoutingKey, _mandatory, properties, context.Body);
        context.Published = true;
    }
}
