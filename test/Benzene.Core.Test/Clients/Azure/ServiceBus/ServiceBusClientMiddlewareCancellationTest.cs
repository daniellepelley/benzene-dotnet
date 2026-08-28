using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Benzene.Abstractions.DI;
using Benzene.Clients.Azure.ServiceBus;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Azure.ServiceBus;

/// <summary>
/// #268/#225-family: <see cref="ServiceBusClientMiddleware"/> resolves the ambient
/// <see cref="ICancellationTokenAccessor"/> and threads its token into the <c>SendMessageAsync</c>
/// call, on both the DI-resolved and given-instance <c>UseServiceBusClient</c> paths. Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class ServiceBusClientMiddlewareCancellationTest
{
    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = token });
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    [Fact]
    public async Task UseServiceBusClient_GivenInstance_ForwardsTheAmbientTokenToSendMessageAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockSender = new Mock<ServiceBusSender>();
        mockSender
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), cts.Token))
            .Returns(Task.CompletedTask);

        var pipeline = new MiddlewarePipelineBuilder<ServiceBusSendMessageContext>(new NullBenzeneServiceContainer())
            .UseServiceBusClient(mockSender.Object)
            .Build();

        var context = new ServiceBusSendMessageContext(new ServiceBusMessage());
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        mockSender.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseServiceBusClient_DiResolved_ForwardsTheAmbientTokenToSendMessageAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockSender = new Mock<ServiceBusSender>();
        mockSender
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), cts.Token))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(mockSender.Object);
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var container = new MicrosoftBenzeneServiceContainer(services);

        var pipeline = new MiddlewarePipelineBuilder<ServiceBusSendMessageContext>(container)
            .UseServiceBusClient()
            .Build();

        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();
        var context = new ServiceBusSendMessageContext(new ServiceBusMessage());
        await pipeline.HandleAsync(context, resolver);

        mockSender.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseServiceBusClient_GivenInstance_WithNoAccessorRegistered_SendsWithNoneToken()
    {
        var mockSender = new Mock<ServiceBusSender>();
        mockSender
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), CancellationToken.None))
            .Returns(Task.CompletedTask);

        var pipeline = new MiddlewarePipelineBuilder<ServiceBusSendMessageContext>(new NullBenzeneServiceContainer())
            .UseServiceBusClient(mockSender.Object)
            .Build();

        var context = new ServiceBusSendMessageContext(new ServiceBusMessage());
        await pipeline.HandleAsync(context, new NullServiceResolver());

        mockSender.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), CancellationToken.None), Times.Once);
    }
}
