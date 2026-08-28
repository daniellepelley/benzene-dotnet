using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.RabbitMq.RabbitMqSendMessage;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace Benzene.Test.RabbitMq;

/// <summary>
/// #236: <see cref="RabbitMqClientMiddleware"/> resolves the ambient <see cref="ICancellationTokenAccessor"/>
/// and threads its token into <see cref="RabbitMqMandatoryPublishCoordinator.PublishMandatoryAsync"/> -
/// observable at <see cref="IChannel.GetNextPublishSequenceNumberAsync"/>, which the coordinator calls
/// with the same token it was given. Mirrors <c>PubSubCancellationTest</c>'s "assert the actual token"
/// model - never <c>It.IsAny&lt;CancellationToken&gt;()</c> for the token under test.
/// </summary>
public class RabbitMqClientMiddlewareCancellationTest
{
    private static Mock<IChannel> AckingChannel(ulong nextSequenceNumber = 1)
    {
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(x => x.GetNextPublishSequenceNumberAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(nextSequenceNumber);
        mockChannel
            .Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback(() => mockChannel.Raise(x => x.BasicAcksAsync += null, mockChannel.Object, new BasicAckEventArgs(1UL, false)))
            .Returns(ValueTask.CompletedTask);
        return mockChannel;
    }

    private static RabbitMqSendMessageContext SampleContext() =>
        new("", "routing-key", ReadOnlyMemory<byte>.Empty, new Dictionary<string, object?>());

    [Fact]
    public async Task HandleAsync_MandatoryTrue_ConstructorAccessor_ForwardsTheAmbientTokenToTheCoordinator()
    {
        using var cts = new CancellationTokenSource();
        var mockChannel = AckingChannel();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };

        var middleware = new RabbitMqClientMiddleware(mockChannel.Object, mandatory: true, cancellation: accessor);

        await middleware.HandleAsync(SampleContext(), () => Task.CompletedTask);

        mockChannel.Verify(x => x.GetNextPublishSequenceNumberAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseRabbitMqClient_ResolvesTheAccessorFromThePipeline_ForwardsTheAmbientTokenToTheCoordinator()
    {
        // RabbitMq has only the one UseRabbitMqClient(channel, ...) overload (no DI-resolved zero-arg
        // sibling like the other transports) - this proves it resolves ICancellationTokenAccessor from
        // the pipeline's own service resolver at execution time, not just via constructor injection.
        using var cts = new CancellationTokenSource();
        var mockChannel = AckingChannel();

        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();

        var pipeline = new MiddlewarePipelineBuilder<RabbitMqSendMessageContext>(new NullBenzeneServiceContainer())
            .UseRabbitMqClient(mockChannel.Object, mandatory: true)
            .Build();

        await pipeline.HandleAsync(SampleContext(), resolver);

        mockChannel.Verify(x => x.GetNextPublishSequenceNumberAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_MandatoryTrue_NoAccessor_UsesNoneToken()
    {
        var mockChannel = AckingChannel();

        var middleware = new RabbitMqClientMiddleware(mockChannel.Object, mandatory: true);

        await middleware.HandleAsync(SampleContext(), () => Task.CompletedTask);

        mockChannel.Verify(x => x.GetNextPublishSequenceNumberAsync(CancellationToken.None), Times.Once);
    }
}
