using System;
using System.IO;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.ApplicationLoadBalancerEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Benzene.Aws.Lambda.Core;
using Benzene.Abstractions.Middleware;
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
using Xunit;

namespace Benzene.Test.Aws.HttpBridge;

/// <summary>
/// The ALB bridge, and the one way it interacts badly with the REST bridge.
/// </summary>
/// <remarks>
/// ALB is a distinct payload pair rather than a flavour of API Gateway: the request carries
/// <c>requestContext.elb</c>, and the response requires <c>statusDescription</c>, which the API
/// Gateway response type has no field for — an ALB target answering in the API Gateway shape returns
/// 502. Benzene has no ALB binding of its own, so unlike the other two rules this one is derived from
/// the payload rather than inherited from an existing router.
/// </remarks>
public class HttpBridgeAlbTest
{
    private static AwsLambdaEntryPoint BuildFunction(Action<IMiddlewarePipelineBuilder<AwsEventStreamContext>> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.UsingBenzene(x => x
            .AddBenzene()
            .AddMessageHandlers(typeof(Defaults).Assembly)
            .AddSqs());

        var container = new MicrosoftBenzeneServiceContainer(services);
        var pipeline = new MiddlewarePipelineBuilder<AwsEventStreamContext>(container);
        configure(pipeline);

        return new AwsLambdaEntryPoint(pipeline.Build(), new MicrosoftServiceResolverFactory(services));
    }

    private static Stream Serialize<T>(T payload)
    {
        var stream = new MemoryStream();
        new DefaultLambdaJsonSerializer().Serialize(payload, stream);
        stream.Position = 0;
        return stream;
    }

    private static ApplicationLoadBalancerRequest AlbRequest(string path) => new()
    {
        Path = path,
        HttpMethod = "GET",
        RequestContext = new ApplicationLoadBalancerRequest.ALBRequestContext
        {
            Elb = new ApplicationLoadBalancerRequest.ElbInfo
            {
                TargetGroupArn = "arn:aws:elasticloadbalancing:eu-west-1:123456789012:targetgroup/benzene/abc"
            }
        }
    };

    private static ApplicationLoadBalancerResponse AlbResponse(string body) => new()
    {
        StatusCode = 200,
        StatusDescription = "200 OK",
        Body = body
    };

    [Fact]
    public async Task AlbEvent_GoesToTheBridge()
    {
        string seenPath = null;
        string seenTargetGroup = null;

        var function = BuildFunction(pipeline => pipeline
            .UseHttpBridgeAlb((request, _) =>
            {
                seenPath = request.Path;
                seenTargetGroup = request.RequestContext.Elb.TargetGroupArn;
                return Task.FromResult(AlbResponse("from-the-alb-bridge"));
            })
            .UseSqs(sqs => sqs.UseMessageHandlers()));

        var response = await function.FunctionHandlerAsync(Serialize(AlbRequest("/orders")), new FakeLambdaContext());
        var body = await new StreamReader(response).ReadToEndAsync();

        Assert.Equal("/orders", seenPath);
        Assert.Contains("benzene", seenTargetGroup);
        Assert.Contains("from-the-alb-bridge", body);
        // ALB rejects a response without statusDescription with a 502, so it has to survive the round trip.
        Assert.Contains("statusDescription", body);
    }

    [Fact]
    public async Task SqsEvent_DoesNotGoToTheAlbBridge()
    {
        var bridgeWasCalled = false;

        var function = BuildFunction(pipeline => pipeline
            .UseHttpBridgeAlb((_, _) =>
            {
                bridgeWasCalled = true;
                return Task.FromResult(AlbResponse("http"));
            })
            .UseSqs(sqs => sqs.UseMessageHandlers()));

        var sqsEvent = MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsSqs();
        var response = await function.FunctionHandlerAsync(Serialize(sqsEvent), new FakeLambdaContext());

        Assert.False(bridgeWasCalled);
        Assert.Contains("batchItemFailures", await new StreamReader(response).ReadToEndAsync());
    }

    [Fact]
    public async Task AnApiGatewayEvent_DoesNotGoToTheAlbBridge()
    {
        // The ALB rule is requestContext.elb, so an API Gateway REST payload — which has HttpMethod
        // but no elb — falls through to whatever comes next.
        var albWasCalled = false;
        var restWasCalled = false;

        var function = BuildFunction(pipeline => pipeline
            .UseHttpBridgeAlb((_, _) =>
            {
                albWasCalled = true;
                return Task.FromResult(AlbResponse("alb"));
            })
            .UseHttpBridge((_, _) =>
            {
                restWasCalled = true;
                return Task.FromResult(new APIGatewayProxyResponse { StatusCode = 200, Body = "rest" });
            }));

        var response = await function.FunctionHandlerAsync(
            Serialize(new APIGatewayProxyRequest { HttpMethod = "GET", Path = "/orders" }), new FakeLambdaContext());

        Assert.False(albWasCalled);
        Assert.True(restWasCalled);
        Assert.Contains("rest", await new StreamReader(response).ReadToEndAsync());
    }

    [Fact]
    public async Task RegisteredFirst_TheAlbBridgeClaimsAlbTraffic()
    {
        // The ordering rule the docs state, pinned. The REST rule is HttpMethod != null — inherited
        // unchanged from Benzene's own API Gateway router — and an ALB payload deserializes into an
        // APIGatewayProxyRequest with HttpMethod set, so the two overlap and registration order
        // decides. ALB first is correct; the next test shows what the other order costs.
        var function = BuildFunction(pipeline => pipeline
            .UseHttpBridgeAlb((_, _) => Task.FromResult(AlbResponse("alb")))
            .UseHttpBridge((_, _) => Task.FromResult(new APIGatewayProxyResponse { StatusCode = 200, Body = "rest" })));

        var response = await function.FunctionHandlerAsync(Serialize(AlbRequest("/orders")), new FakeLambdaContext());

        Assert.Contains("alb", await new StreamReader(response).ReadToEndAsync());
    }

    [Fact]
    public async Task RegisteredSecond_TheAlbBridgeNeverSeesItsOwnTraffic()
    {
        var function = BuildFunction(pipeline => pipeline
            .UseHttpBridge((_, _) => Task.FromResult(new APIGatewayProxyResponse { StatusCode = 200, Body = "rest" }))
            .UseHttpBridgeAlb((_, _) => Task.FromResult(AlbResponse("alb"))));

        var response = await function.FunctionHandlerAsync(Serialize(AlbRequest("/orders")), new FakeLambdaContext());
        var body = await new StreamReader(response).ReadToEndAsync();

        // The REST bridge claims it, and the response goes back without statusDescription — which is
        // a 502 from a real ALB. This is why the ordering is documented rather than left to chance.
        Assert.Contains("rest", body);
        Assert.DoesNotContain("statusDescription", body);
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
