using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Benzene.Abstractions.DI;
using Benzene.Clients.Aws.Sqs;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Aws.Sqs;

/// <summary>
/// #268/#225-family: <see cref="SqsClientMiddleware"/> resolves the ambient
/// <see cref="ICancellationTokenAccessor"/> and threads its token into the SQS <c>SendMessageAsync</c>
/// call, on both the DI-resolved and given-instance <c>UseSqsClient</c> paths. Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class SqsClientMiddlewareCancellationTest
{
    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = token });
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    [Fact]
    public async Task UseSqsClient_GivenInstance_ForwardsTheAmbientTokenToSendMessageAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), cts.Token))
            .ReturnsAsync(new SendMessageResponse());

        var pipeline = new MiddlewarePipelineBuilder<SqsSendMessageContext>(new NullBenzeneServiceContainer())
            .UseSqsClient(mockSqsClient.Object)
            .Build();

        var context = new SqsSendMessageContext(new SendMessageRequest());
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        mockSqsClient.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseSqsClient_DiResolved_ForwardsTheAmbientTokenToSendMessageAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), cts.Token))
            .ReturnsAsync(new SendMessageResponse());

        var services = new ServiceCollection();
        services.AddSingleton(mockSqsClient.Object);
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var container = new MicrosoftBenzeneServiceContainer(services);

        var pipeline = new MiddlewarePipelineBuilder<SqsSendMessageContext>(container)
            .UseSqsClient()
            .Build();

        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();
        var context = new SqsSendMessageContext(new SendMessageRequest());
        await pipeline.HandleAsync(context, resolver);

        mockSqsClient.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseSqsClient_GivenInstance_WithNoAccessorRegistered_SendsWithNoneToken()
    {
        var mockSqsClient = new Mock<IAmazonSQS>();
        mockSqsClient
            .Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), CancellationToken.None))
            .ReturnsAsync(new SendMessageResponse());

        var pipeline = new MiddlewarePipelineBuilder<SqsSendMessageContext>(new NullBenzeneServiceContainer())
            .UseSqsClient(mockSqsClient.Object)
            .Build();

        var context = new SqsSendMessageContext(new SendMessageRequest());
        await pipeline.HandleAsync(context, new NullServiceResolver());

        mockSqsClient.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), CancellationToken.None), Times.Once);
    }
}
