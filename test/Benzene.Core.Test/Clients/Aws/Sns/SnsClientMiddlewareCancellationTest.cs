using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Benzene.Abstractions.DI;
using Benzene.Clients.Aws.Sns;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Aws.Sns;

/// <summary>
/// #268/#225-family: <see cref="SnsClientMiddleware"/> resolves the ambient
/// <see cref="ICancellationTokenAccessor"/> and threads its token into the SNS <c>PublishAsync</c>
/// call, on both the DI-resolved and given-instance <c>UseSnsClient</c> paths. Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class SnsClientMiddlewareCancellationTest
{
    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        var accessor = new CancellationTokenAccessor { CancellationToken = token };
        services.AddSingleton<ICancellationTokenAccessor>(accessor);
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    [Fact]
    public async Task UseSnsClient_GivenInstance_ForwardsTheAmbientTokenToPublishAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), cts.Token))
            .ReturnsAsync(new PublishResponse());

        var pipeline = new MiddlewarePipelineBuilder<SnsSendMessageContext>(new NullBenzeneServiceContainer())
            .UseSnsClient(mockSnsClient.Object)
            .Build();

        var context = new SnsSendMessageContext(new PublishRequest());
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        mockSnsClient.Verify(x => x.PublishAsync(It.IsAny<PublishRequest>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseSnsClient_DiResolved_ForwardsTheAmbientTokenToPublishAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), cts.Token))
            .ReturnsAsync(new PublishResponse());

        var services = new ServiceCollection();
        services.AddSingleton(mockSnsClient.Object);
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var container = new MicrosoftBenzeneServiceContainer(services);

        var pipeline = new MiddlewarePipelineBuilder<SnsSendMessageContext>(container)
            .UseSnsClient()
            .Build();

        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();
        var context = new SnsSendMessageContext(new PublishRequest());
        await pipeline.HandleAsync(context, resolver);

        mockSnsClient.Verify(x => x.PublishAsync(It.IsAny<PublishRequest>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseSnsClient_GivenInstance_WithNoAccessorRegistered_PublishesWithNoneToken()
    {
        var mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), CancellationToken.None))
            .ReturnsAsync(new PublishResponse());

        var pipeline = new MiddlewarePipelineBuilder<SnsSendMessageContext>(new NullBenzeneServiceContainer())
            .UseSnsClient(mockSnsClient.Object)
            .Build();

        var context = new SnsSendMessageContext(new PublishRequest());
        await pipeline.HandleAsync(context, new NullServiceResolver());

        mockSnsClient.Verify(x => x.PublishAsync(It.IsAny<PublishRequest>(), CancellationToken.None), Times.Once);
    }
}
