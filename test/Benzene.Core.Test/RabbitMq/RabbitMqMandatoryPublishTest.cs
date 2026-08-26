using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.RabbitMq.RabbitMqSendMessage;
using Benzene.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace Benzene.Test.RabbitMq;

/// <summary>
/// WP-8 (task board #24): <c>mandatory: true</c> used to unconditionally set
/// <c>RabbitMqSendMessageContext.Published = true</c> without ever subscribing to
/// <c>IChannel.BasicReturnAsync</c>, so an unroutable message reported "Accepted" identically to a routed
/// one. These tests cover both the internal correlation tracker directly
/// (<see cref="RabbitMqMandatoryPublishCoordinator"/>) and the observable behavior through the public
/// <see cref="RabbitMqBenzeneMessageClient"/>/<see cref="RabbitMqClientMiddleware"/> surface.
/// </summary>
public class RabbitMqMandatoryPublishTest
{
    private static Mock<IChannel> ConfirmsEnabledChannel(ulong nextSequenceNumber = 1)
    {
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(x => x.GetNextPublishSequenceNumberAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(nextSequenceNumber);
        return mockChannel;
    }

    // -------------------------------------------------------------------------------------------
    // RabbitMqMandatoryPublishCoordinator - the correlation tracker, tested directly.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void GetOrCreate_ChannelWithoutConfirmsEnabled_Throws()
    {
        // GetNextPublishSequenceNumberAsync stays 0 forever unless Confirm.Select ran when the channel
        // was opened - see RabbitMqMandatoryPublishCoordinator's remarks for why that's the reliable
        // public-API proxy for "are confirms enabled" in RabbitMQ.Client 7.0.0.
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 0);

        var ex = Assert.Throws<InvalidOperationException>(() => RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object));
        Assert.Contains("publisher confirmations", ex.Message);
    }

    [Fact]
    public void GetOrCreate_ConfirmsEnabledChannel_DoesNotThrow()
    {
        var mockChannel = ConfirmsEnabledChannel();

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);

        Assert.NotNull(coordinator);
    }

    [Fact]
    public void GetOrCreate_SameChannel_ReturnsTheSameInstance()
    {
        // The whole point: RabbitMQ.Client's BasicReturnAsync/BasicAcksAsync/BasicNacksAsync events are
        // channel-scoped, not per-publish, so every RabbitMqClientMiddleware instance sharing a channel
        // (a fresh one is constructed per publish - see MiddlewarePipeline<TContext>) must share ONE
        // subscription, not pile on a new one per message.
        var mockChannel = ConfirmsEnabledChannel();

        var first = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);
        var second = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrCreate_DifferentChannels_ReturnDifferentInstances()
    {
        var first = RabbitMqMandatoryPublishCoordinator.GetOrCreate(ConfirmsEnabledChannel().Object);
        var second = RabbitMqMandatoryPublishCoordinator.GetOrCreate(ConfirmsEnabledChannel().Object);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task PublishMandatoryAsync_BrokerAcks_ResolvesTrue()
    {
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback(() => mockChannel.Raise(x => x.BasicAcksAsync += null, mockChannel.Object, new BasicAckEventArgs(1UL, false)))
            .Returns(ValueTask.CompletedTask);

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);
        var properties = new BasicProperties { MessageId = "msg-1" };

        bool routed = await coordinator.PublishMandatoryAsync("exchange", "routing-key", properties,
            ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.True(routed);
    }

    [Fact]
    public async Task PublishMandatoryAsync_BrokerReturnsAsUnroutable_ResolvesFalse()
    {
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                var returnedProperties = new BasicProperties { MessageId = "msg-1" };
                var returnArgs = new BasicReturnEventArgs(312, "NO_ROUTE", "exchange", "routing-key",
                    returnedProperties, ReadOnlyMemory<byte>.Empty);
                mockChannel.Raise(x => x.BasicReturnAsync += null, mockChannel.Object, returnArgs);

                // A mandatory-but-returned publish is still typically acked afterwards - the return, not
                // this later ack, must be what decides the outcome.
                mockChannel.Raise(x => x.BasicAcksAsync += null, mockChannel.Object, new BasicAckEventArgs(1UL, false));
            })
            .Returns(ValueTask.CompletedTask);

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);
        var properties = new BasicProperties { MessageId = "msg-1" };

        bool routed = await coordinator.PublishMandatoryAsync("exchange", "routing-key", properties,
            ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.False(routed);
    }

    [Fact]
    public async Task PublishMandatoryAsync_ReturnForADifferentMessageId_DoesNotAffectThisPublish()
    {
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                var unrelatedProperties = new BasicProperties { MessageId = "some-other-message" };
                var returnArgs = new BasicReturnEventArgs(312, "NO_ROUTE", "exchange", "routing-key",
                    unrelatedProperties, ReadOnlyMemory<byte>.Empty);
                mockChannel.Raise(x => x.BasicReturnAsync += null, mockChannel.Object, returnArgs);
                mockChannel.Raise(x => x.BasicAcksAsync += null, mockChannel.Object, new BasicAckEventArgs(1UL, false));
            })
            .Returns(ValueTask.CompletedTask);

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);
        var properties = new BasicProperties { MessageId = "msg-1" };

        bool routed = await coordinator.PublishMandatoryAsync("exchange", "routing-key", properties,
            ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.True(routed);
    }

    [Fact]
    public async Task PublishMandatoryAsync_MultipleAckCoversEarlierTag_ResolvesTrue()
    {
        ulong nextTag = 1;
        // RabbitMqMandatoryPublishCoordinator.GetOrCreate itself calls GetNextPublishSequenceNumberAsync
        // once (the confirms-enabled check), so the first tag actually assigned to a publish is not
        // necessarily 1 - record what's actually handed out instead of assuming.
        var assignedTags = new List<ulong>();
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(x => x.GetNextPublishSequenceNumberAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                ulong tag = nextTag++;
                assignedTags.Add(tag);
                return tag;
            });
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);
        assignedTags.Clear(); // discard the tag consumed by the confirms-enabled check above

        var firstPublish = coordinator.PublishMandatoryAsync("exchange", "routing-key",
            new BasicProperties { MessageId = "msg-1" }, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        var secondPublish = coordinator.PublishMandatoryAsync("exchange", "routing-key",
            new BasicProperties { MessageId = "msg-2" }, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(2, assignedTags.Count);

        // One "multiple" ack for the higher of the two tags covers both outstanding publishes.
        mockChannel.Raise(x => x.BasicAcksAsync += null, mockChannel.Object, new BasicAckEventArgs(assignedTags.Max(), true));

        Assert.True(await firstPublish);
        Assert.True(await secondPublish);
    }

    [Fact]
    public async Task PublishMandatoryAsync_ChannelShutsDownWhilePending_FaultsTheWait()
    {
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback(() => mockChannel.Raise(x => x.ChannelShutdownAsync += null, mockChannel.Object,
                new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "CONNECTION_FORCED")))
            .Returns(ValueTask.CompletedTask);

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);
        var properties = new BasicProperties { MessageId = "msg-1" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PublishMandatoryAsync(
            "exchange", "routing-key", properties, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    // -------------------------------------------------------------------------------------------
    // Tracked findings round 7-10, WP-A (task board #30, #33, #45) - RabbitMqMandatoryPublishCoordinator
    // hardening. Ruled in work/bug-fix-designs-round7-10-2026-08.md, "WP-A - RabbitMQ mandatory-publish
    // coordinator hardening".
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the coordinator's private <c>_byTag</c>/<c>_byMessageId</c> dictionaries via reflection (the
    /// round-7 leak-probe technique) so a test can assert a pending-publish entry did not leak past
    /// <c>Forget</c>, without exposing internal state on the type's public surface just for testing.
    /// </summary>
    private static (int ByTagCount, int ByMessageIdCount) GetPendingCounts(RabbitMqMandatoryPublishCoordinator coordinator)
    {
        FieldInfo byTagField = typeof(RabbitMqMandatoryPublishCoordinator)
            .GetField("_byTag", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo byMessageIdField = typeof(RabbitMqMandatoryPublishCoordinator)
            .GetField("_byMessageId", BindingFlags.NonPublic | BindingFlags.Instance)!;

        dynamic byTag = byTagField.GetValue(coordinator)!;
        dynamic byMessageId = byMessageIdField.GetValue(coordinator)!;
        return ((int)byTag.Count, (int)byMessageId.Count);
    }

    [Fact]
    public async Task PublishMandatoryAsync_CancelledWhileAwaitingBrokerOutcome_ForgetsThePendingPublish()
    {
        // Task board #30: a broker that never fires Basic.Ack/Basic.Nack/Basic.Return, combined with the
        // caller's own token firing while the final await is outstanding - before this fix, that leaked
        // the pending-publish entry in _byTag/_byMessageId forever, because Forget(tag, messageId) was
        // only ever called from the earlier try/catch around the publish itself, not around the final
        // await.
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask); // never raises an ack/nack/return

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);
        var properties = new BasicProperties { MessageId = "msg-leak-probe" };
        using var cts = new CancellationTokenSource();

        Task<bool> publishTask = coordinator.PublishMandatoryAsync("exchange", "routing-key", properties,
            ReadOnlyMemory<byte>.Empty, cts.Token);

        // By this point the publish has already run to completion synchronously (the mocked channel
        // completes every call inline) and is now suspended purely on the broker's outcome - exactly the
        // "mid-wait" state the leak needs.
        (int byTagBefore, int byMessageIdBefore) = GetPendingCounts(coordinator);
        Assert.Equal(1, byTagBefore);
        Assert.Equal(1, byMessageIdBefore);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishTask);

        (int byTagAfter, int byMessageIdAfter) = GetPendingCounts(coordinator);
        Assert.Equal(0, byTagAfter);
        Assert.Equal(0, byMessageIdAfter);
    }

    [Fact]
    public async Task PublishMandatoryAsync_BrokerNeverConfirms_TimesOutAndForgetsThePendingPublish()
    {
        // Task board #45: nothing used to bound how long a caller waits for the broker's confirm - a
        // stalled/unresponsive broker (confirms enabled but never firing) hung the caller forever.
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask); // never raises an ack/nack/return

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);
        var properties = new BasicProperties { MessageId = "msg-timeout-probe" };

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => coordinator.PublishMandatoryAsync(
            "exchange", "routing-key", properties, ReadOnlyMemory<byte>.Empty, CancellationToken.None,
            TimeSpan.FromMilliseconds(50)));

        Assert.Contains("msg-timeout-probe", ex.Message);

        (int byTagAfter, int byMessageIdAfter) = GetPendingCounts(coordinator);
        Assert.Equal(0, byTagAfter);
        Assert.Equal(0, byMessageIdAfter);
    }

    [Fact]
    public async Task PublishMandatoryAsync_DefaultTimeout_Is30Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RabbitMqMandatoryPublishCoordinator.DefaultPublishConfirmTimeout);
    }

    [Fact]
    public async Task PublishMandatoryAsync_DuplicateMessageIdAlreadyInFlight_ThrowsClearly()
    {
        // Task board #33: _byMessageId[messageId] = pending used indexer-overwrite, so a second publish
        // sharing an already-in-flight MessageId silently stole the first publish's correlation entry -
        // a later Basic.Return naming that MessageId would then be misattributed to the wrong publish
        // (and the first publish's Tcs would never settle from a real broker return). It must instead be
        // rejected up front, clearly, at publish time.
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask); // never settles - keeps the first publish "in flight"

        var coordinator = RabbitMqMandatoryPublishCoordinator.GetOrCreate(mockChannel.Object);

        Task<bool> firstPublish = coordinator.PublishMandatoryAsync("exchange", "routing-key",
            new BasicProperties { MessageId = "dup-id" }, ReadOnlyMemory<byte>.Empty, CancellationToken.None,
            TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PublishMandatoryAsync(
            "exchange", "routing-key", new BasicProperties { MessageId = "dup-id" }, ReadOnlyMemory<byte>.Empty,
            CancellationToken.None));

        Assert.Contains("dup-id", ex.Message);
        Assert.Contains("already in flight", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The first (genuinely earlier) publish is untouched by the rejected duplicate - it is still the
        // one and only entry tracked for "dup-id", and settles independently (here, by its own timeout).
        (int byTagAfterDuplicateRejected, int byMessageIdAfterDuplicateRejected) = GetPendingCounts(coordinator);
        Assert.Equal(1, byTagAfterDuplicateRejected);
        Assert.Equal(1, byMessageIdAfterDuplicateRejected);

        await Assert.ThrowsAsync<TimeoutException>(() => firstPublish);
    }

    // -------------------------------------------------------------------------------------------
    // Through the public surface (RabbitMqBenzeneMessageClient / RabbitMqClientMiddleware).
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_MandatoryTrue_ChannelWithoutConfirms_ThrowsImmediately()
    {
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 0);

        // Fails at wiring time (client construction), not on the first send.
        Assert.Throws<InvalidOperationException>(() =>
            new RabbitMqBenzeneMessageClient(mockChannel.Object, NullLogger<RabbitMqBenzeneMessageClient>.Instance,
                new NullServiceResolver(), mandatory: true));
    }

    [Fact]
    public async Task SendMessageAsync_MandatoryTrue_RoutedMessage_ReturnsAccepted()
    {
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback(() => mockChannel.Raise(x => x.BasicAcksAsync += null, mockChannel.Object, new BasicAckEventArgs(1UL, false)))
            .Returns(ValueTask.CompletedTask);

        var client = new RabbitMqBenzeneMessageClient(mockChannel.Object,
            NullLogger<RabbitMqBenzeneMessageClient>.Instance, new NullServiceResolver(), mandatory: true);

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_MandatoryTrue_UnroutableMessage_DoesNotReportAccepted()
    {
        BasicProperties? capturedProperties = null;
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, bool _, BasicProperties props, ReadOnlyMemory<byte> _, CancellationToken _) =>
            {
                capturedProperties = props;
                // Echo the same MessageId back, exactly like a real broker return would.
                var returnedProperties = new BasicProperties { MessageId = props.MessageId };
                var returnArgs = new BasicReturnEventArgs(312, "NO_ROUTE", "exchange", "some-topic",
                    returnedProperties, ReadOnlyMemory<byte>.Empty);
                mockChannel.Raise(x => x.BasicReturnAsync += null, mockChannel.Object, returnArgs);
            })
            .Returns(ValueTask.CompletedTask);

        var client = new RabbitMqBenzeneMessageClient(mockChannel.Object,
            NullLogger<RabbitMqBenzeneMessageClient>.Instance, new NullServiceResolver(), mandatory: true);

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        // This is the bug this WP fixes: mandatory: true used to report Accepted unconditionally here.
        Assert.NotEqual(BenzeneResultStatus.Accepted, result.Status);
        Assert.NotNull(capturedProperties);
        Assert.False(string.IsNullOrEmpty(capturedProperties!.MessageId));
    }

    [Fact]
    public async Task SendMessageAsync_MandatoryTrue_StampsAFreshMessageIdPerSend()
    {
        var capturedMessageIds = new List<string?>();
        var mockChannel = ConfirmsEnabledChannel(nextSequenceNumber: 1);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, bool _, BasicProperties props, ReadOnlyMemory<byte> _, CancellationToken _) =>
            {
                capturedMessageIds.Add(props.MessageId);
                mockChannel.Raise(x => x.BasicAcksAsync += null, mockChannel.Object, new BasicAckEventArgs(1UL, false));
            })
            .Returns(ValueTask.CompletedTask);

        var middleware = new RabbitMqClientMiddleware(mockChannel.Object, mandatory: true);

        // RabbitMqSendMessageContext carries no MessageId slot of its own (properties are built fresh
        // from Headers on every publish), so every mandatory publish takes the "not already set" branch -
        // each send must still get its own freshly-stamped, non-empty, distinct MessageId.
        for (int i = 0; i < 2; i++)
        {
            var context = new RabbitMqSendMessageContext("", "routing-key", ReadOnlyMemory<byte>.Empty,
                new Dictionary<string, object?>());
            await middleware.HandleAsync(context, () => Task.CompletedTask);
        }

        Assert.Equal(2, capturedMessageIds.Count);
        Assert.All(capturedMessageIds, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.NotEqual(capturedMessageIds[0], capturedMessageIds[1]);
    }

    [Fact]
    public async Task SendMessageAsync_MandatoryFalse_UnaffectedByCoordinator_StillFireAndForget()
    {
        // mandatory: false must keep behaving exactly as before: no confirms requirement, no waiting.
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), false,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var client = new RabbitMqBenzeneMessageClient(mockChannel.Object,
            NullLogger<RabbitMqBenzeneMessageClient>.Instance, new NullServiceResolver());

        var result = await client.SendMessageAsync<string, string>("some-topic", "some-message");

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
        mockChannel.Verify(x => x.GetNextPublishSequenceNumberAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
