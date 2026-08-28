using System.Threading;
using System.Threading.Tasks;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Benzene.Abstractions.DI;
using Benzene.Clients.Aws.EventBridge;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Aws.EventBridge;

/// <summary>
/// #268/#225-family: <see cref="EventBridgeClientMiddleware"/> resolves the ambient
/// <see cref="ICancellationTokenAccessor"/> and threads its token into the <c>PutEventsAsync</c> call,
/// on both the DI-resolved and given-instance <c>UseEventBridgeClient</c> paths. Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class EventBridgeClientMiddlewareCancellationTest
{
    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = token });
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    [Fact]
    public async Task UseEventBridgeClient_GivenInstance_ForwardsTheAmbientTokenToPutEventsAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockEventBridgeClient = new Mock<IAmazonEventBridge>();
        mockEventBridgeClient
            .Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), cts.Token))
            .ReturnsAsync(new PutEventsResponse());

        var pipeline = new MiddlewarePipelineBuilder<EventBridgeSendMessageContext>(new NullBenzeneServiceContainer())
            .UseEventBridgeClient(mockEventBridgeClient.Object)
            .Build();

        var context = new EventBridgeSendMessageContext(new PutEventsRequest());
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        mockEventBridgeClient.Verify(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseEventBridgeClient_DiResolved_ForwardsTheAmbientTokenToPutEventsAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockEventBridgeClient = new Mock<IAmazonEventBridge>();
        mockEventBridgeClient
            .Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), cts.Token))
            .ReturnsAsync(new PutEventsResponse());

        var services = new ServiceCollection();
        services.AddSingleton(mockEventBridgeClient.Object);
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var container = new MicrosoftBenzeneServiceContainer(services);

        var pipeline = new MiddlewarePipelineBuilder<EventBridgeSendMessageContext>(container)
            .UseEventBridgeClient()
            .Build();

        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();
        var context = new EventBridgeSendMessageContext(new PutEventsRequest());
        await pipeline.HandleAsync(context, resolver);

        mockEventBridgeClient.Verify(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseEventBridgeClient_GivenInstance_WithNoAccessorRegistered_PublishesWithNoneToken()
    {
        var mockEventBridgeClient = new Mock<IAmazonEventBridge>();
        mockEventBridgeClient
            .Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), CancellationToken.None))
            .ReturnsAsync(new PutEventsResponse());

        var pipeline = new MiddlewarePipelineBuilder<EventBridgeSendMessageContext>(new NullBenzeneServiceContainer())
            .UseEventBridgeClient(mockEventBridgeClient.Object)
            .Build();

        var context = new EventBridgeSendMessageContext(new PutEventsRequest());
        await pipeline.HandleAsync(context, new NullServiceResolver());

        mockEventBridgeClient.Verify(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), CancellationToken.None), Times.Once);
    }
}
