using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Benzene.RabbitMq.RabbitMqSendMessage;

/// <summary>
/// Makes <c>mandatory: true</c> publishing on <see cref="RabbitMqClientMiddleware"/> real: correlates a
/// broker <c>Basic.Return</c> (unroutable) or <c>Basic.Ack</c>/<c>Basic.Nack</c> (routed / rejected) back
/// to the specific publish that caused it, so the caller gets an accurate outcome instead of the
/// unconditional "Accepted" the middleware used to report for every publish regardless of what the
/// broker actually did with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as a separate, channel-keyed object.</b> <see cref="RabbitMqClientMiddleware"/> is
/// resolved fresh from the middleware pipeline on every single publish (see
/// <c>Benzene.Core.Middleware.MiddlewarePipeline{TContext}</c> - middleware is deliberately not cached
/// across calls, so a Scoped/Transient DI registration still gets a new instance per request). RabbitMQ.Client's
/// <c>BasicReturnAsync</c>/<c>BasicAcksAsync</c>/<c>BasicNacksAsync</c> events, in contrast, live on the
/// <see cref="IChannel"/> itself, not on any one publish - subscribing a fresh handler from inside the
/// middleware's constructor would pile on a new handler for every message ever published on that channel.
/// This type is looked up (or created) once per <see cref="IChannel"/> via a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>, so the channel-level events are subscribed exactly
/// once no matter how many mandatory publishes - and how many <see cref="RabbitMqClientMiddleware"/>
/// instances - run against that channel. The table does not extend the channel's lifetime: once nothing
/// outside this table references the channel any more, both it and its coordinator become collectible.
/// </para>
/// <para>
/// <b>Why a publisher-confirms-enabled channel is required.</b> AMQP's <c>Basic.Return</c> carries the
/// returned message's exchange/routing key/properties/body back, but - unlike <c>Basic.Ack</c>/
/// <c>Basic.Nack</c> - no delivery tag, so correlating it to a specific publish needs an identifier
/// embedded in the message itself: <see cref="BasicProperties.MessageId"/> (stamped by
/// <see cref="RabbitMqClientMiddleware"/> if the caller didn't set one). Knowing when a publish did
/// <em>not</em> get returned - the "routed" half of the outcome - has no such trick available: RabbitMQ.Client
/// 7.0.0's <c>BasicPublishAsync</c> does not hand the assigned delivery tag back to the caller, so the
/// only race-free way to know it is to read <see cref="IChannel.GetNextPublishSequenceNumberAsync"/>
/// immediately before publishing, with nothing else able to publish on the channel in between - which
/// requires confirms to be selected (RabbitMQ.Client only assigns sequence numbers, and only fires
/// <c>Basic.Ack</c>/<c>Basic.Nack</c> at all, once <c>Confirm.Select</c> has run on the channel).
/// </para>
/// <para>
/// <b>Verifying confirms are enabled.</b> <see cref="IChannel"/> exposes no direct "are confirms enabled"
/// property in RabbitMQ.Client 7.0.0. <see cref="IChannel.GetNextPublishSequenceNumberAsync"/> is used as
/// a reliable proxy instead: verified against the 7.0.0 source
/// (<c>Channel.PublisherConfirms.cs</c>), it always returns the channel's internal
/// <c>_nextPublishSeqNo</c> counter, which is set to 1 the moment <c>Confirm.Select</c> runs during
/// <c>IConnection.CreateChannelAsync</c> (when the channel was opened with
/// <c>CreateChannelOptions.PublisherConfirmationsEnabled = true</c>) and otherwise never leaves 0. A
/// result of 0 on a freshly-opened channel therefore reliably means confirms are not enabled.
/// </para>
/// </remarks>
internal sealed class RabbitMqMandatoryPublishCoordinator
{
    private static readonly ConditionalWeakTable<IChannel, RabbitMqMandatoryPublishCoordinator> Coordinators = new();

    private readonly IChannel _channel;

    // Guards "learn the delivery tag this publish is about to be assigned, then actually publish" as one
    // atomic step across every mandatory publish sharing this coordinator - see the "why a
    // publisher-confirms-enabled channel is required" remark above for why that pairing has to be atomic.
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private readonly ConcurrentDictionary<ulong, PendingPublish> _byTag = new();
    private readonly ConcurrentDictionary<string, PendingPublish> _byMessageId = new();

    private sealed record PendingPublish(ulong Tag, string MessageId, TaskCompletionSource<bool> Tcs);

    /// <summary>Gets the coordinator for <paramref name="channel"/>, creating (and validating) it on first use.</summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="channel"/> does not have publisher confirmations enabled.
    /// </exception>
    public static RabbitMqMandatoryPublishCoordinator GetOrCreate(IChannel channel)
        => Coordinators.GetValue(channel, static ch => new RabbitMqMandatoryPublishCoordinator(ch));

    private RabbitMqMandatoryPublishCoordinator(IChannel channel)
    {
        _channel = channel;

        // Fail fast (P6 - no inert options): see the "verifying confirms are enabled" remark above. This
        // call does no I/O (it only touches an in-process semaphore internally), so blocking on it here -
        // once, the first time a channel is used for a mandatory publish - is safe and avoids forcing
        // every call site of GetOrCreate to become async.
        ulong nextSequenceNumber = channel.GetNextPublishSequenceNumberAsync().AsTask().GetAwaiter().GetResult();
        if (nextSequenceNumber == 0)
        {
            throw new InvalidOperationException(
                "RabbitMqClientMiddleware/UseRabbitMqClient was configured with mandatory: true, but the " +
                "IChannel it was given does not have publisher confirmations enabled. An unroutable " +
                "message can only be reliably correlated back to the publish that sent it when confirms " +
                "sequence the channel's publishes, so mandatory: true refuses to run on a channel it " +
                "cannot verify this on, rather than silently behaving as if mandatory: false. Open the " +
                "channel with confirms enabled - e.g. connection.CreateChannelAsync(new CreateChannelOptions(" +
                "publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: false)) - before " +
                "passing it to mandatory: true publishing.");
        }

        channel.BasicReturnAsync += OnBasicReturnAsync;
        channel.BasicAcksAsync += OnBasicAcksAsync;
        channel.BasicNacksAsync += OnBasicNacksAsync;
        channel.ChannelShutdownAsync += OnChannelShutdownAsync;
    }

    /// <summary>
    /// Publishes <paramref name="properties"/> - which must already carry a non-empty
    /// <see cref="BasicProperties.MessageId"/> - with <c>mandatory: true</c>, and resolves once the
    /// outcome is known.
    /// </summary>
    /// <returns><c>true</c> once the broker acks the publish; <c>false</c> if it instead returns the message as unroutable.</returns>
    public async Task<bool> PublishMandatoryAsync(string exchange, string routingKey, BasicProperties properties,
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        string messageId = properties.MessageId!;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ulong tag = 0;
        try
        {
            // Atomic with the publish immediately below (nothing else can run BasicPublishAsync on this
            // channel while we hold the gate), so this is guaranteed to be the tag RabbitMQ.Client is
            // about to assign to it.
            tag = await _channel.GetNextPublishSequenceNumberAsync(cancellationToken).ConfigureAwait(false);
            var pending = new PendingPublish(tag, messageId, tcs);
            _byTag[tag] = pending;
            _byMessageId[messageId] = pending;

            await _channel.BasicPublishAsync(exchange, routingKey, true, properties, body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Forget(tag, messageId);
            throw;
        }
        finally
        {
            _publishGate.Release();
        }

        // Released above already - only the "assign tag, write the frame" step needs to be serialized;
        // multiple mandatory publishes can have their outcomes pending concurrently, each correctly
        // settled by its own tag/MessageId.
        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Forget(ulong tag, string messageId)
    {
        if (tag != 0)
        {
            _byTag.TryRemove(tag, out _);
        }

        _byMessageId.TryRemove(messageId, out _);
    }

    private Task OnBasicReturnAsync(object sender, BasicReturnEventArgs @event)
    {
        string? messageId = @event.BasicProperties.MessageId;
        if (!string.IsNullOrEmpty(messageId) && _byMessageId.TryRemove(messageId, out PendingPublish? pending))
        {
            _byTag.TryRemove(pending.Tag, out _);

            // Unroutable. A mandatory-but-returned publish is still typically acked by the broker
            // afterwards (it accepted responsibility for the message before finding no queue to route it
            // to), so the return - not a possible later ack for the same tag - has to be what decides the
            // outcome. TrySetResult is safe even if an ack for the same tag arrives later anyway (the
            // dictionary entries are already gone by then, so Settle below is a no-op for it); only the
            // first settlement of a given publish ever takes effect.
            pending.Tcs.TrySetResult(false);
        }

        return Task.CompletedTask;
    }

    private Task OnBasicAcksAsync(object sender, BasicAckEventArgs @event)
    {
        Settle(@event.DeliveryTag, @event.Multiple, routed: true);
        return Task.CompletedTask;
    }

    private Task OnBasicNacksAsync(object sender, BasicNackEventArgs @event)
    {
        // A broker-level nack unrelated to routing (e.g. an internal broker error) is still not the
        // "Accepted" a mandatory: true caller was promised.
        Settle(@event.DeliveryTag, @event.Multiple, routed: false);
        return Task.CompletedTask;
    }

    private void Settle(ulong deliveryTag, bool multiple, bool routed)
    {
        if (multiple)
        {
            // A "multiple" ack/nack covers every outstanding tag up to and including deliveryTag.
            foreach (ulong tag in _byTag.Keys)
            {
                if (tag <= deliveryTag && _byTag.TryRemove(tag, out PendingPublish? pending))
                {
                    _byMessageId.TryRemove(pending.MessageId, out _);
                    pending.Tcs.TrySetResult(routed);
                }
            }
        }
        else if (_byTag.TryRemove(deliveryTag, out PendingPublish? pending))
        {
            _byMessageId.TryRemove(pending.MessageId, out _);
            pending.Tcs.TrySetResult(routed);
        }
    }

    private Task OnChannelShutdownAsync(object sender, ShutdownEventArgs @event)
    {
        // The channel closed with mandatory publishes still outstanding (e.g. the connection dropped) -
        // fault them rather than leaving their callers awaiting forever.
        foreach (PendingPublish pending in _byTag.Values)
        {
            pending.Tcs.TrySetException(new InvalidOperationException(
                $"The RabbitMQ channel closed before the mandatory publish with MessageId '{pending.MessageId}' " +
                $"was confirmed or returned: {@event.ReplyText}"));
        }

        _byTag.Clear();
        _byMessageId.Clear();
        return Task.CompletedTask;
    }
}
