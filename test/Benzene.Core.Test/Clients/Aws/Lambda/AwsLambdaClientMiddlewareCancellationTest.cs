using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Benzene.Abstractions.DI;
using Benzene.Clients.Aws.Lambda;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Clients.Aws.Lambda;

/// <summary>
/// #268/#225-family: <see cref="AwsLambdaClientMiddleware"/> resolves the ambient
/// <see cref="ICancellationTokenAccessor"/> and threads its token into the <c>InvokeAsync</c> call, on
/// both the DI-resolved and given-instance <c>UseAwsLambdaClient</c> paths. Mirrors
/// <c>PubSubCancellationTest</c>'s "assert the actual token" model - never <c>It.IsAny&lt;CancellationToken&gt;()</c>.
/// </summary>
public class AwsLambdaClientMiddlewareCancellationTest
{
    private static IServiceResolver CreateResolver(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = token });
        return new MicrosoftServiceResolverFactory(services).CreateScope();
    }

    [Fact]
    public async Task UseAwsLambdaClient_GivenInstance_ForwardsTheAmbientTokenToInvokeAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockLambdaClient = new Mock<IAmazonLambda>();
        mockLambdaClient
            .Setup(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), cts.Token))
            .ReturnsAsync(new InvokeResponse());

        var pipeline = new MiddlewarePipelineBuilder<LambdaSendMessageContext>(new NullBenzeneServiceContainer())
            .UseAwsLambdaClient(mockLambdaClient.Object)
            .Build();

        var context = new LambdaSendMessageContext(new InvokeRequest());
        await pipeline.HandleAsync(context, CreateResolver(cts.Token));

        mockLambdaClient.Verify(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseAwsLambdaClient_DiResolved_ForwardsTheAmbientTokenToInvokeAsync()
    {
        using var cts = new CancellationTokenSource();
        var mockLambdaClient = new Mock<IAmazonLambda>();
        mockLambdaClient
            .Setup(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), cts.Token))
            .ReturnsAsync(new InvokeResponse());

        var services = new ServiceCollection();
        services.AddSingleton(mockLambdaClient.Object);
        services.AddSingleton<ICancellationTokenAccessor>(new CancellationTokenAccessor { CancellationToken = cts.Token });
        var container = new MicrosoftBenzeneServiceContainer(services);

        var pipeline = new MiddlewarePipelineBuilder<LambdaSendMessageContext>(container)
            .UseAwsLambdaClient()
            .Build();

        var resolver = new MicrosoftServiceResolverFactory(services).CreateScope();
        var context = new LambdaSendMessageContext(new InvokeRequest());
        await pipeline.HandleAsync(context, resolver);

        mockLambdaClient.Verify(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UseAwsLambdaClient_GivenInstance_WithNoAccessorRegistered_InvokesWithNoneToken()
    {
        var mockLambdaClient = new Mock<IAmazonLambda>();
        mockLambdaClient
            .Setup(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), CancellationToken.None))
            .ReturnsAsync(new InvokeResponse());

        var pipeline = new MiddlewarePipelineBuilder<LambdaSendMessageContext>(new NullBenzeneServiceContainer())
            .UseAwsLambdaClient(mockLambdaClient.Object)
            .Build();

        var context = new LambdaSendMessageContext(new InvokeRequest());
        await pipeline.HandleAsync(context, new NullServiceResolver());

        mockLambdaClient.Verify(x => x.InvokeAsync(It.IsAny<InvokeRequest>(), CancellationToken.None), Times.Once);
    }
}
