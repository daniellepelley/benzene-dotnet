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
    /// <summary>
    /// The publish-confirm timeout applied when <see cref="PublishMandatoryAsync"/> is not given an
    /// explicit one (task board #45): without a bound, a stalled/unresponsive broker (confirms enabled
    /// but never firing <c>Basic.Ack</c>/<c>Basic.Nack</c>/<c>Basic.Return</c>) hangs the caller forever.
    /// </summary>
    public static readonly TimeSpan DefaultPublishConfirmTimeout = TimeSpan.FromSeconds(30);

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
    /// <param name="exchange">The exchange to publish to.</param>
    /// <param name="routingKey">The AMQP routing key.</param>
    /// <param name="properties">
    /// The message properties, carrying the <see cref="BasicProperties.MessageId"/> this publish is
    /// correlated by. Must be unique among this coordinator's currently in-flight mandatory publishes -
    /// see the <see cref="InvalidOperationException"/> below.
    /// </param>
    /// <param name="body">The message body.</param>
    /// <param name="cancellationToken">
    /// Cancelled by the caller (e.g. host shutdown). Cancelling while the broker's confirmation is still
    /// pending forgets the pending-publish entry before the <see cref="OperationCanceledException"/>
    /// propagates (task board #30) - it does not leak in <c>_byTag</c>/<c>_byMessageId</c>.
    /// </param>
    /// <param name="publishConfirmTimeout">
    /// The most this call will wait for the broker's <c>Basic.Ack</c>/<c>Basic.Nack</c>/<c>Basic.Return</c>
    /// once the publish has been written to the channel, before giving up with a
    /// <see cref="TimeoutException"/> (task board #45) - a stalled/unresponsive broker (confirms enabled
    /// but never firing) would otherwise hang the caller forever. Defaults to
    /// <see cref="DefaultPublishConfirmTimeout"/> when not given. Like a cancellation, a timeout also
    /// forgets the pending-publish entry before the exception propagates.
    /// </param>
    /// <returns><c>true</c> once the broker acks the publish; <c>false</c> if it instead returns the message as unroutable.</returns>
    /// <exception cref="InvalidOperationException">
    /// A mandatory publish with the same <see cref="BasicProperties.MessageId"/> is already in flight on
    /// this coordinator (task board #33) - accepting it would let a later <c>Basic.Return</c> be
    /// misattributed to whichever of the two publishes happened to still be registered under that id.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The broker did not confirm or return the publish within <paramref name="publishConfirmTimeout"/>.
    /// </exception>
    public async Task<bool> PublishMandatoryAsync(string exchange, string routingKey, BasicProperties properties,
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken, TimeSpan? publishConfirmTimeout = null)
    {
        string messageId = properties.MessageId!;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PendingPublish? pending = null;
        try
        {
            // Atomic with the publish immediately below (nothing else can run BasicPublishAsync on this
            // channel while we hold the gate), so this is guaranteed to be the tag RabbitMQ.Client is
            // about to assign to it.
            ulong tag = await _channel.GetNextPublishSequenceNumberAsync(cancellationToken).ConfigureAwait(false);
            pending = new PendingPublish(tag, messageId, tcs);
            _byTag[tag] = pending;

            // #33: TryAdd, not indexer-overwrite - a second publish sharing an already-in-flight
            // MessageId would otherwise silently steal the first publish's correlation entry, so a later
            // Basic.Return naming that MessageId gets misattributed to the wrong publish (and the first
            // publish's Tcs would never settle). Reject it here, before the message is even written to
            // the wire. Forget below is value-checked (see its remarks), so rejecting this duplicate
            // never disturbs the OTHER, still-legitimately-in-flight publish already registered under
            // this MessageId - only the _byTag entry this call itself just added is undone.
            if (!_byMessageId.TryAdd(messageId, pending))
            {
                throw new InvalidOperationException(
                    $"A mandatory RabbitMQ publish with MessageId '{messageId}' is already in flight on " +
                    "this channel. Each mandatory publish must use a MessageId that is not already " +
                    "pending on the same coordinator/channel, otherwise a later Basic.Return could not be " +
                    "reliably correlated back to the publish that caused it. Await (or cancel) the " +
                    "earlier publish before starting a new one with the same MessageId, or let " +
                    "RabbitMqClientMiddleware stamp a fresh one for you.");
            }

            await _channel.BasicPublishAsync(exchange, routingKey, true, properties, body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (pending is not null)
            {
                Forget(pending);
            }

            throw;
        }
        finally
        {
            _publishGate.Release();
        }

        // Released above already - only the "assign tag, write the frame" step needs to be serialized;
        // multiple mandatory publishes can have their outcomes pending concurrently, each correctly
        // settled by its own tag/MessageId.
        //
        // #30/#45: wait bounded by BOTH the caller's token and a publish-confirm timeout, via one linked
        // source - mirrors Benzene.Resilience.TimeoutMiddleware's "timer vs. host token" distinction.
        // Either way the pending-publish entry must be forgotten before the exception propagates, or it
        // leaks in _byTag/_byMessageId forever (nothing else will ever remove it once the caller has
        // stopped awaiting it).
        TimeSpan timeout = publishConfirmTimeout ?? DefaultPublishConfirmTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == timeoutCts.Token && !cancellationToken.IsCancellationRequested)
        {
            // The timer fired, not the caller: a publish-confirm timeout, not a genuine cancellation.
            Forget(pending);
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for the RabbitMQ broker to confirm or return the " +
                $"mandatory publish with MessageId '{messageId}'.", ex);
        }
        catch (OperationCanceledException)
        {
            // The caller's own token fired - a genuine cancellation, not a timeout.
            Forget(pending);
            throw;
        }
    }

    /// <summary>
    /// Removes <paramref name="pending"/> from <c>_byTag</c>/<c>_byMessageId</c> - but only the exact
    /// entries that still point at <em>this</em> instance. Value-checked (via the
    /// <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(KeyValuePair{TKey,TValue})"/> overload), not
    /// key-checked: a plain key-only <c>TryRemove(key, out _)</c> would delete whatever is currently
    /// registered under that key - including a DIFFERENT, still-legitimately-in-flight publish that
    /// happens to share this one's (rejected-as-a-duplicate) MessageId. Since <see cref="PendingPublish"/>
    /// is a record whose <see cref="TaskCompletionSource{TResult}"/> field is compared by reference, two
    /// distinct publishes are never equal even if they somehow shared a <c>Tag</c>/<c>MessageId</c>, so
    /// this can only ever remove the entry <paramref name="pending"/> itself put there.
    /// </summary>
    private void Forget(PendingPublish? pending)
    {
        if (pending is null)
        {
            return;
        }

        _byTag.TryRemove(new KeyValuePair<ulong, PendingPublish>(pending.Tag, pending));
        _byMessageId.TryRemove(new KeyValuePair<string, PendingPublish>(pending.MessageId, pending));
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
