using System;
using System.Collections.Generic;
using System.Linq;
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
