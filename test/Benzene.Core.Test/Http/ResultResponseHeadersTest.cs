using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Azure.Function.AspNet;
using Benzene.Azure.Function.AspNet.TestHelpers;
using Benzene.Azure.Function.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Http.BenzeneMessage;
using Benzene.Http.Cors;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Benzene.Test.Http;

// FR-3 (adopter feature requests): a handler's result carries response headers onto the HTTP response,
// and never replaces a header the pipeline set (CORS, the rendered content type).
public class ResultResponseHeadersTest
{
    private static AwsLambdaBenzeneTestHost V1Host(bool withCors = false)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway =>
            {
                if (withCors)
                {
                    apiGateway.UseCors(new CorsSettings { AllowedDomains = new[] { "example.com" } });
                }

                apiGateway.UseMessageHandlers();
            }))
            .BuildHost();
    }

    [Fact]
    public async Task ApiGatewayV1_CreatedWithLocation_Is201WithTheHeader()
    {
        var response = await V1Host().SendApiGatewayAsync(HttpBuilder.Create("GET", Defaults.HeaderEchoPath));

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(HeaderEchoMessageHandler.Location, response.Headers["Location"]);
        Assert.Equal("1", response.Headers["X-Thing"]);
    }

    [Fact]
    public async Task ApiGatewayV1_TheRenderedContentTypeWins()
    {
        var response = await V1Host().SendApiGatewayAsync(HttpBuilder.Create("GET", Defaults.HeaderEchoPath));

        Assert.Equal("application/json", response.Headers["content-type"]);
    }

    [Fact]
    public async Task ApiGatewayV1_CorsHeadersWin()
    {
        var request = HttpBuilder.Create("GET", Defaults.HeaderEchoPath).WithHeader("origin", "https://example.com");

        var response = await V1Host(withCors: true).SendApiGatewayAsync(request);

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("https://example.com", response.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal(HeaderEchoMessageHandler.Location, response.Headers["Location"]);
    }

    [Fact]
    public async Task ApiGatewayV1_AFailureResultCarriesItsHeaders()
    {
        var response = await V1Host().SendApiGatewayAsync(HttpBuilder.Create("GET", Defaults.HeaderConflictPath));

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("/things/1", response.Headers["X-Existing"]);
    }

    [Fact]
    public async Task ApiGatewayV2_CreatedWithLocation_Is201WithTheHeader()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGatewayV2(apiGateway => apiGateway.UseMessageHandlers()))
            .BuildHost();

        var response = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(new APIGatewayHttpApiV2ProxyRequest
        {
            Version = "2.0",
            RawPath = Defaults.HeaderEchoPath,
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "GET", Path = Defaults.HeaderEchoPath }
            },
            Headers = new Dictionary<string, string> { { "x-correlation-id", Guid.NewGuid().ToString() } }
        });

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(HeaderEchoMessageHandler.Location, response.Headers["Location"]);
        Assert.Equal("application/json", response.Headers["content-type"]);
    }

    [Fact]
    public async Task AzureFunctions_CreatedWithLocation_WithCors_DoesNotThrowAndKeepsCors()
    {
        // The Azure adapter's header write throws on a duplicate name, so writing a result header CORS
        // already set would have failed the request.
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseHttp(http => http
                .UseCors(new CorsSettings { AllowedDomains = new[] { "example.com" } })
                .UseMessageHandlers()))
            .Build();

        var request = HttpBuilder.Create("GET", Defaults.HeaderEchoPath, new HeaderEchoRequest())
            .WithHeader("origin", "https://example.com")
            .AsAspNetCoreHttpRequest();
        var response = await app.HandleHttpRequest(request) as ContentResult;

        Assert.NotNull(response);
        Assert.Equal(201, response.StatusCode);
        var headers = request.HttpContext.Response.Headers;
        Assert.Equal(HeaderEchoMessageHandler.Location, headers["Location"].ToString());
        Assert.Equal("https://example.com", headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal("application/json", response.ContentType);
    }

    [Fact]
    public async Task Envelope_ResultHeadersAppearInTheResponseEnvelope()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway
                .UseBenzeneMessage(new BenzeneMessageHttpOptions(), messageApp => messageApp.UseMessageHandlers())
                .UseMessageHandlers()))
            .BuildHost();

        var envelope = new { topic = Defaults.HeaderEchoTopic, headers = new Dictionary<string, string>(), body = "{}" };
        var response = await host.SendApiGatewayAsync(HttpBuilder.Create("POST", "/benzene/invoke", envelope));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains($"\"location\":\"{HeaderEchoMessageHandler.Location}\"", response.Body, StringComparison.OrdinalIgnoreCase);
    }
}
