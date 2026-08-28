using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Benzene.Abstractions.DI;
using Benzene.Clients.Azure.EventGrid;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Azure.EventGrid;

/// <summary>
/// #268/#225-family: <see cref="EventGridClientMiddleware"/> resolves the ambient
/// <see cref="ICancellationTokenAccessor"/> and threads its token into the <c>SendEventAsync</c> call,
/// on both the DI-resolved and given-instance <c>UseEventGridClient</c> paths. Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class EventGridClientMiddlewareCancellationTest
{
    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = token });
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    private static CloudEvent SampleEvent() =>
        new("source", "type", BinaryData.FromString("\"data\""), "application/json", CloudEventDataFormat.Json);

    [Fact]
    public async Task UseEventGridClient_GivenInstance_ForwardsTheAmbientTokenToSendEventAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockPublisher = new Mock<EventGridPublisherClient>();
        mockPublisher
            .Setup(x => x.SendEventAsync(It.IsAny<CloudEvent>(), cts.Token))
            .ReturnsAsync((global::Azure.Response)null);

        var pipeline = new MiddlewarePipelineBuilder<EventGridSendMessageContext>(new NullBenzeneServiceContainer())
            .UseEventGridClient(mockPublisher.Object)
            .Build();

        var context = new EventGridSendMessageContext(SampleEvent());
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        mockPublisher.Verify(x => x.SendEventAsync(It.IsAny<CloudEvent>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseEventGridClient_DiResolved_ForwardsTheAmbientTokenToSendEventAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockPublisher = new Mock<EventGridPublisherClient>();
        mockPublisher
            .Setup(x => x.SendEventAsync(It.IsAny<CloudEvent>(), cts.Token))
            .ReturnsAsync((global::Azure.Response)null);

        var services = new ServiceCollection();
        services.AddSingleton(mockPublisher.Object);
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var container = new MicrosoftBenzeneServiceContainer(services);

        var pipeline = new MiddlewarePipelineBuilder<EventGridSendMessageContext>(container)
            .UseEventGridClient()
            .Build();

        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();
        var context = new EventGridSendMessageContext(SampleEvent());
        await pipeline.HandleAsync(context, resolver);

        mockPublisher.Verify(x => x.SendEventAsync(It.IsAny<CloudEvent>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseEventGridClient_GivenInstance_WithNoAccessorRegistered_SendsWithNoneToken()
    {
        var mockPublisher = new Mock<EventGridPublisherClient>();
        mockPublisher
            .Setup(x => x.SendEventAsync(It.IsAny<CloudEvent>(), CancellationToken.None))
            .ReturnsAsync((global::Azure.Response)null);

        var pipeline = new MiddlewarePipelineBuilder<EventGridSendMessageContext>(new NullBenzeneServiceContainer())
            .UseEventGridClient(mockPublisher.Object)
            .Build();

        var context = new EventGridSendMessageContext(SampleEvent());
        await pipeline.HandleAsync(context, new NullServiceResolver());

        mockPublisher.Verify(x => x.SendEventAsync(It.IsAny<CloudEvent>(), CancellationToken.None), Times.Once);
    }
}
