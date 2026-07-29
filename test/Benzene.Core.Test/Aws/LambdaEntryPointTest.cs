using System;
using System.Text;
using System.Threading.Tasks;
using Amazon.Lambda.TestUtilities;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Core.Exceptions;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Aws;

public class LambdaEntryPointTest
{
    private static AwsLambdaEntryPoint BuildEntryPoint(
        Func<MiddlewarePipelineBuilder<AwsEventStreamContext>, IMiddlewarePipelineBuilder<AwsEventStreamContext>> configure)
    {
        var app = configure(new MiddlewarePipelineBuilder<AwsEventStreamContext>(
            new MicrosoftBenzeneServiceContainer(new ServiceCollection())));

        var mockServiceResolverFactory = new Mock<IServiceResolverFactory>();
        mockServiceResolverFactory.Setup(x => x.CreateScope())
            .Returns(ServiceResolverMother.CreateServiceResolver());

        return new AwsLambdaEntryPoint(app.Build(), mockServiceResolverFactory.Object);
    }

    [Fact]
    public async Task LambdaEntryPoint()
    {
        var lambdaEntryPoint = BuildEntryPoint(app => app
            .Use(null, async (x, next) =>
            {
                // Claiming the event without writing a body: nothing else can tell this apart from an
                // event nothing recognised, so the middleware has to say so.
                x.MarkHandled();
                await next();
            }));

        var request = new BenzeneMessageRequest();

        var result = await lambdaEntryPoint.FunctionHandlerAsync(AwsEventStreamContextBuilder.ObjectToStream(request), new TestLambdaContext());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task WritingAResponseBodyIsEnoughToClaimTheEvent()
    {
        // Custom middleware that writes bytes doesn't need to know MarkHandled exists.
        var lambdaEntryPoint = BuildEntryPoint(app => app
            .Use(null, async (x, next) =>
            {
                var bytes = Encoding.UTF8.GetBytes("{}");
                await x.Response.WriteAsync(bytes, 0, bytes.Length);
                await next();
            }));

        var result = await lambdaEntryPoint.FunctionHandlerAsync(
            AwsEventStreamContextBuilder.ObjectToStream(new BenzeneMessageRequest()), new TestLambdaContext());

        Assert.NotNull(result);
    }

    [Fact]
    public async Task AnEventNoMiddlewareClaimsIsAnError()
    {
        // The whole point of the flag: before it, this returned an empty 200 and the developer was
        // left looking for a handler that was never going to be reached.
        var lambdaEntryPoint = BuildEntryPoint(app => app.Use(null, (_, next) => next()));

        var exception = await Assert.ThrowsAsync<BenzeneException>(() =>
            lambdaEntryPoint.FunctionHandlerAsync(
                AwsEventStreamContextBuilder.ObjectToStream(new BenzeneMessageRequest()), new TestLambdaContext()));

        Assert.Contains("has not been recognized", exception.Message);
    }
}
