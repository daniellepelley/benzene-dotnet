using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Benzene.Abstractions.DI;
using Benzene.Clients.Azure.QueueStorage;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Azure.QueueStorage;

/// <summary>
/// #268/#225-family: <see cref="QueueStorageClientMiddleware"/> resolves the ambient
/// <see cref="ICancellationTokenAccessor"/> and threads its token into the <c>SendMessageAsync</c>
/// call, on both the DI-resolved and given-instance <c>UseQueueStorageClient</c> paths. Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class QueueStorageClientMiddlewareCancellationTest
{
    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = token });
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    [Fact]
    public async Task UseQueueStorageClient_GivenInstance_ForwardsTheAmbientTokenToSendMessageAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient
            .Setup(x => x.SendMessageAsync("hello", cts.Token))
            .ReturnsAsync((global::Azure.Response<SendReceipt>)null);

        var pipeline = new MiddlewarePipelineBuilder<QueueStorageSendMessageContext>(new NullBenzeneServiceContainer())
            .UseQueueStorageClient(mockQueueClient.Object)
            .Build();

        var context = new QueueStorageSendMessageContext("hello");
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        mockQueueClient.Verify(x => x.SendMessageAsync("hello", cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseQueueStorageClient_DiResolved_ForwardsTheAmbientTokenToSendMessageAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient
            .Setup(x => x.SendMessageAsync("hello", cts.Token))
            .ReturnsAsync((global::Azure.Response<SendReceipt>)null);

        var services = new ServiceCollection();
        services.AddSingleton(mockQueueClient.Object);
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var container = new MicrosoftBenzeneServiceContainer(services);

        var pipeline = new MiddlewarePipelineBuilder<QueueStorageSendMessageContext>(container)
            .UseQueueStorageClient()
            .Build();

        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();
        var context = new QueueStorageSendMessageContext("hello");
        await pipeline.HandleAsync(context, resolver);

        mockQueueClient.Verify(x => x.SendMessageAsync("hello", cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseQueueStorageClient_GivenInstance_WithNoAccessorRegistered_SendsWithNoneToken()
    {
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient
            .Setup(x => x.SendMessageAsync("hello", CancellationToken.None))
            .ReturnsAsync((global::Azure.Response<SendReceipt>)null);

        var pipeline = new MiddlewarePipelineBuilder<QueueStorageSendMessageContext>(new NullBenzeneServiceContainer())
            .UseQueueStorageClient(mockQueueClient.Object)
            .Build();

        var context = new QueueStorageSendMessageContext("hello");
        await pipeline.HandleAsync(context, new NullServiceResolver());

        mockQueueClient.Verify(x => x.SendMessageAsync("hello", CancellationToken.None), Times.Once);
    }
}
