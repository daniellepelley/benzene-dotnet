using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.ApplicationLoadBalancerEvents;
using Amazon.Lambda.AspNetCoreServer.Internal;
using Amazon.Lambda.TestUtilities;
using Benzene.Aws.Lambda.AspNet;
using Benzene.Aws.Lambda.HttpBridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Benzene.Test.Aws.AspNet;

/// <summary>
/// Round-15's zero-coverage finding for <c>Benzene.Aws.Lambda.AspNet</c>'s three HTTP bridges
/// (<c>BenzeneAspNetBridge</c>, <c>BenzeneAspNetRestBridge</c>, <c>BenzeneAspNetAlbBridge</c>): the
/// only existing test in the package asserted DI registration, never that a bridge actually dispatches
/// into a real ASP.NET Core pipeline. These build a real <see cref="WebApplication"/> — the same
/// <c>Amazon.Lambda.AspNetCoreServer.Internal.LambdaServer</c> the package's own
/// <c>BenzeneLambdaServer</c> derives from, so a v1/v2/ALB-shaped request goes through exactly the
/// <c>_server.Application.ProcessRequestAsync</c> path each bridge's base class uses in production -
/// and assert the response minimal-API endpoint routing actually produced comes back correctly shaped
/// for each payload family.
/// </summary>
public class BenzeneAspNetBridgeTest
{
    private const string EndpointPath = "/hello";
    private const string EndpointResponseBody = "hello-from-aspnet";

    /// <summary>
    /// Builds and starts a real minimal-API <see cref="WebApplication"/> whose <c>IServer</c> is a bare
    /// AWS <c>LambdaServer</c> (the same base type <c>BenzeneLambdaServer</c> derives from) - so
    /// <c>StartAsync</c> captures the ASP.NET <c>IHttpApplication</c> exactly as it does inside a real
    /// Lambda function, without opening any socket or requiring a Lambda runtime environment.
    /// </summary>
    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseServer(new LambdaServer());

        var app = builder.Build();
        app.MapGet(EndpointPath, () => EndpointResponseBody);

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task V2Bridge_RoutesAV2ShapedRequest_ThroughTheRealAspNetPipeline()
    {
        await using var app = await StartHostAsync();

        var bridge = (IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>)
            new BenzeneAspNetBridge(app.Services);

        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            RawPath = EndpointPath,
            RawQueryString = "",
            Headers = new Dictionary<string, string>(),
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                DomainName = "example.com",
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                {
                    Method = "GET",
                    Path = EndpointPath
                }
            }
        };

        var response = await bridge.HandleAsync(request, new TestLambdaContext());

        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(EndpointResponseBody, response.Body);
    }

    [Fact]
    public async Task RestBridge_RoutesAV1ShapedRequest_ThroughTheRealAspNetPipeline()
    {
        await using var app = await StartHostAsync();

        var bridge = (IAwsHttpBridge<APIGatewayProxyRequest, APIGatewayProxyResponse>)
            new BenzeneAspNetRestBridge(app.Services);

        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = EndpointPath,
            Headers = new Dictionary<string, string>()
        };

        var response = await bridge.HandleAsync(request, new TestLambdaContext());

        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(EndpointResponseBody, response.Body);
    }

    [Fact]
    public async Task AlbBridge_RoutesAnAlbShapedRequest_ThroughTheRealAspNetPipeline()
    {
        await using var app = await StartHostAsync();

        var bridge = (IAwsHttpBridge<ApplicationLoadBalancerRequest, ApplicationLoadBalancerResponse>)
            new BenzeneAspNetAlbBridge(app.Services);

        var request = new ApplicationLoadBalancerRequest
        {
            HttpMethod = "GET",
            Path = EndpointPath,
            Headers = new Dictionary<string, string>(),
            RequestContext = new ApplicationLoadBalancerRequest.ALBRequestContext
            {
                Elb = new ApplicationLoadBalancerRequest.ElbInfo
                {
                    TargetGroupArn = "arn:aws:elasticloadbalancing:eu-west-1:123456789012:targetgroup/benzene/abc"
                }
            }
        };

        var response = await bridge.HandleAsync(request, new TestLambdaContext());

        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(EndpointResponseBody, response.Body);
        // ALB rejects a response with no statusDescription with a 502 - the AWS marshaller sets it for
        // every response, which is exactly why a real target group never sees that failure mode.
        Assert.Equal("200 OK", response.StatusDescription);
    }
}
