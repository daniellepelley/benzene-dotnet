using System;
using System.IO;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.AwsEventStream;
using Benzene.Aws.Lambda.HttpBridge;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Aws.Lambda.Sqs.TestHelpers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Aws.HttpBridge;

/// <summary>
/// The bridge's whole job: claim HTTP-shaped events for an application Benzene does not own, and
/// leave every other event to Benzene's own bindings on the same function.
/// </summary>
public class HttpBridgeTest
{
    private static AwsLambdaEntryPoint BuildFunction(
        Func<APIGatewayHttpApiV2ProxyRequest, ILambdaContext, Task<APIGatewayHttpApiV2ProxyResponse>> bridge)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // ExampleMessageHandler takes IExampleService. Registering it makes the SQS path genuinely
        // work, so these tests assert what they are about — which binding claims an event — rather
        // than a handler failure that used to be quietly reported as a batch item failure.
        services.AddScoped(_ => Mock.Of<IExampleService>());
        services.UsingBenzene(x => x
            .AddBenzene()
            .AddMessageHandlers(typeof(Defaults).Assembly)
            .AddSqs());

        var container = new MicrosoftBenzeneServiceContainer(services);
        var pipeline = new MiddlewarePipelineBuilder<AwsEventStreamContext>(container);

        // The bridge takes HTTP; Benzene keeps everything else on the same function.
        pipeline.UseHttpBridgeV2(bridge)
                .UseSqs(sqs => sqs.UseMessageHandlers());

        return new AwsLambdaEntryPoint(pipeline.Build(), new MicrosoftServiceResolverFactory(services));
    }

    private static Stream Serialize<T>(T payload)
    {
        var stream = new MemoryStream();
        new DefaultLambdaJsonSerializer().Serialize(payload, stream);
        stream.Position = 0;
        return stream;
    }

    private static APIGatewayHttpApiV2ProxyRequest HttpRequest(string path) => new()
    {
        RawPath = path,
        RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
        {
            Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "GET", Path = path }
        }
    };

    [Fact]
    public async Task HttpEvent_GoesToTheBridge()
    {
        string seenPath = null;
        var function = BuildFunction((request, _) =>
        {
            seenPath = request.RawPath;
            return Task.FromResult(new APIGatewayHttpApiV2ProxyResponse { StatusCode = 200, Body = "from-the-bridge" });
        });

        var response = await function.FunctionHandlerAsync(Serialize(HttpRequest("/orders")), new FakeLambdaContext());

        Assert.Equal("/orders", seenPath);
        Assert.Contains("from-the-bridge", new StreamReader(response).ReadToEnd());
    }

    [Fact]
    public async Task SqsEvent_DoesNotGoToTheBridge()
    {
        var bridgeWasCalled = false;
        var function = BuildFunction((_, _) =>
        {
            bridgeWasCalled = true;
            return Task.FromResult(new APIGatewayHttpApiV2ProxyResponse { StatusCode = 200 });
        });

        var sqsEvent = MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsSqs();
        var response = await function.FunctionHandlerAsync(Serialize(sqsEvent), new FakeLambdaContext());

        Assert.False(bridgeWasCalled);   // the bridge must not see queue traffic
        // An SQS batch response means Benzene's SQS binding claimed the event on the same function.
        // Whether the handler's own dependencies resolve is not this package's business — the bridge
        // decides who serves an event, not what they do with it.
        Assert.Contains("batchItemFailures", new StreamReader(response).ReadToEnd());
    }

    [Fact]
    public async Task BothTransportsShareOneFunction()
    {
        var function = BuildFunction((_, _) =>
            Task.FromResult(new APIGatewayHttpApiV2ProxyResponse { StatusCode = 200, Body = "http" }));

        var http = await function.FunctionHandlerAsync(Serialize(HttpRequest("/x")), new FakeLambdaContext());
        var sqs = MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsSqs();
        var sqsResponse = await function.FunctionHandlerAsync(Serialize(sqs), new FakeLambdaContext());

        Assert.Contains("http", new StreamReader(http).ReadToEnd());              // HTTP -> the bridge
        Assert.Contains("batchItemFailures", new StreamReader(sqsResponse).ReadToEnd());  // SQS -> Benzene
    }

    private class FakeLambdaContext : ILambdaContext
    {
        public string AwsRequestId => "req";
        public IClientContext ClientContext => null;
        public string FunctionName => "test";
        public string FunctionVersion => "1";
        public ICognitoIdentity Identity => null;
        public string InvokedFunctionArn => "arn";
        public ILambdaLogger Logger => null;
        public string LogGroupName => "lg";
        public string LogStreamName => "ls";
        public int MemoryLimitInMB => 512;
        public TimeSpan RemainingTime => TimeSpan.FromMinutes(1);
    }
}
