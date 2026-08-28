using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Benzene.Abstractions.DI;
using Benzene.Clients.Azure.EventHub;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Azure.EventHub;

/// <summary>
/// #268/#225-family: <see cref="EventHubClientMiddleware"/> resolves the ambient
/// <see cref="ICancellationTokenAccessor"/> and threads its token into the batch-create/send calls, on
/// both the DI-resolved and given-instance <c>UseEventHubClient</c> paths. Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class EventHubClientMiddlewareCancellationTest
{
    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = token });
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    private static EventDataBatch CapacityBatch(CreateBatchOptions options)
    {
        var store = new List<EventData>();
        return EventHubsModelFactory.EventDataBatch(256 * 1024, store, options, _ => store.Count < 10);
    }

    private static void SetUpProducer(Mock<EventHubProducerClient> mock, CancellationToken expectedToken)
    {
        mock.Setup(x => x.CreateBatchAsync(It.IsAny<CreateBatchOptions>(), expectedToken))
            .ReturnsAsync((CreateBatchOptions o, CancellationToken _) => CapacityBatch(o));
        mock.Setup(x => x.SendAsync(It.IsAny<EventDataBatch>(), expectedToken))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task UseEventHubClient_GivenInstance_ForwardsTheAmbientTokenToCreateBatchAndSend()
    {
        using var cts = new CancellationTokenSource();
        var mockProducer = new Mock<EventHubProducerClient>();
        SetUpProducer(mockProducer, cts.Token);

        var pipeline = new MiddlewarePipelineBuilder<EventHubSendMessageContext>(new NullBenzeneServiceContainer())
            .UseEventHubClient(mockProducer.Object)
            .Build();

        var context = new EventHubSendMessageContext(new EventData("payload"u8.ToArray()));
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        mockProducer.Verify(x => x.CreateBatchAsync(It.IsAny<CreateBatchOptions>(), cts.Token), Times.Once);
        mockProducer.Verify(x => x.SendAsync(It.IsAny<EventDataBatch>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseEventHubClient_DiResolved_ForwardsTheAmbientTokenToCreateBatchAndSend()
    {
        using var cts = new CancellationTokenSource();
        var mockProducer = new Mock<EventHubProducerClient>();
        SetUpProducer(mockProducer, cts.Token);

        var services = new ServiceCollection();
        services.AddSingleton(mockProducer.Object);
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var container = new MicrosoftBenzeneServiceContainer(services);

        var pipeline = new MiddlewarePipelineBuilder<EventHubSendMessageContext>(container)
            .UseEventHubClient()
            .Build();

        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();
        var context = new EventHubSendMessageContext(new EventData("payload"u8.ToArray()));
        await pipeline.HandleAsync(context, resolver);

        mockProducer.Verify(x => x.CreateBatchAsync(It.IsAny<CreateBatchOptions>(), cts.Token), Times.Once);
        mockProducer.Verify(x => x.SendAsync(It.IsAny<EventDataBatch>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseEventHubClient_GivenInstance_WithNoAccessorRegistered_SendsWithNoneToken()
    {
        var mockProducer = new Mock<EventHubProducerClient>();
        SetUpProducer(mockProducer, CancellationToken.None);

        var pipeline = new MiddlewarePipelineBuilder<EventHubSendMessageContext>(new NullBenzeneServiceContainer())
            .UseEventHubClient(mockProducer.Object)
            .Build();

        var context = new EventHubSendMessageContext(new EventData("payload"u8.ToArray()));
        await pipeline.HandleAsync(context, new NullServiceResolver());

        mockProducer.Verify(x => x.CreateBatchAsync(It.IsAny<CreateBatchOptions>(), CancellationToken.None), Times.Once);
        mockProducer.Verify(x => x.SendAsync(It.IsAny<EventDataBatch>(), CancellationToken.None), Times.Once);
    }
}
