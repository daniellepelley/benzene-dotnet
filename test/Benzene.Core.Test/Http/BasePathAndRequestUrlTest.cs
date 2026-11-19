using System;
using System.Collections.Generic;
using System.Text.Json;
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
using Benzene.Http.RequestUrl;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Benzene.Test.Http;

// FR-2 (adopter feature requests): UseBasePath strips a prefix before routing on every HTTP host, and
// IRequestUrl gives a handler the public URL to build absolute links from.
public class BasePathAndRequestUrlTest
{
    [Theory]
    [InlineData("/tasks/1", "/tasks", true, "/1")]
    [InlineData("/tasks", "/tasks", true, "/")]
    [InlineData("/tasks/", "/tasks", true, "/")]
    [InlineData("/TASKS/1", "/tasks", true, "/1")]
    [InlineData("/tasksX/1", "/tasks", false, "/tasksX/1")]
    [InlineData("/other", "/tasks", false, "/other")]
    public void TryStrip(string path, string basePath, bool expected, string expectedRemainder)
    {
        Assert.Equal(expected, BasePathMiddleware<ApiGatewayContext>.TryStrip(path, basePath, out var remainder));
        Assert.Equal(expectedRemainder, remainder);
    }

    [Theory]
    [InlineData("tasks", "/tasks")]
    [InlineData("/tasks/", "/tasks")]
    [InlineData("/", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeBasePath(string basePath, string expected)
    {
        Assert.Equal(expected, BasePathMiddleware<ApiGatewayContext>.NormalizeBasePath(basePath));
    }

    [Fact]
    public void RequestUrl_Absolute()
    {
        var url = new RequestUrlHolder();
        url.Set("https", "api.example.com", "/tasks", "/");

        Assert.Equal("https://api.example.com/tasks", url.BaseUrl);
        Assert.Equal("https://api.example.com/tasks/123", url.Absolute("/123").ToString());
        Assert.Equal("https://api.example.com/tasks/123", url.Absolute("123").ToString());
        Assert.Equal("https://elsewhere.example/x", url.Absolute("https://elsewhere.example/x").ToString());
    }

    [Fact]
    public void RequestUrl_ReadBeforeItIsFilled_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new RequestUrlHolder().Host);
        Assert.Contains("UseRequestUrl", error.Message);
    }

    [Fact]
    public void RequestUrl_NoHost_AbsoluteThrows()
    {
        var url = new RequestUrlHolder();
        url.Set("https", "", "", "/");

        Assert.Throws<InvalidOperationException>(() => url.Absolute("/1"));
    }

    private static AwsLambdaBenzeneTestHost V1Host(Action<RequestUrlOptions> configure)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway
                .UseRequestUrl(configure)
                .UseMessageHandlers()))
            .BuildHost();
    }

    private static RequestUrlResponse Read(string body)
    {
        return JsonSerializer.Deserialize<RequestUrlResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task ApiGatewayV1_BasePath_RoutesTheRemainder_AndBuildsAbsoluteLinks()
    {
        var request = HttpBuilder.Create("GET", "/tasks" + Defaults.RequestUrlPath)
            .WithHeader("host", "api.example.com")
            .WithHeader("x-forwarded-proto", "https");

        var response = await V1Host(o => o.BasePath = "/tasks").SendApiGatewayAsync(request);

        Assert.Equal(200, response.StatusCode);
        var body = Read(response.Body);
        Assert.Equal("https://api.example.com/tasks/123", body.Absolute);
        Assert.Equal("/tasks", body.BasePath);
        Assert.Equal(Defaults.RequestUrlPath, body.Path);
    }

    [Fact]
    public async Task ApiGatewayV1_OutsideTheBasePath_IsNotFound()
    {
        var response = await V1Host(o => o.BasePath = "/tasks")
            .SendApiGatewayAsync(HttpBuilder.Create("GET", Defaults.RequestUrlPath).WithHeader("host", "api.example.com"));

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task ApiGatewayV1_PublicBaseUrl_WinsOverHeaders()
    {
        var request = HttpBuilder.Create("GET", Defaults.RequestUrlPath).WithHeader("host", "internal.example");

        var response = await V1Host(o => o.PublicBaseUrl = "https://public.example.com").SendApiGatewayAsync(request);

        Assert.Equal("https://public.example.com/123", Read(response.Body).Absolute);
    }

    [Fact]
    public async Task ApiGatewayV1_ForwardedHost_IsIgnoredUnlessTrusted()
    {
        var request = HttpBuilder.Create("GET", Defaults.RequestUrlPath)
            .WithHeader("host", "api.example.com")
            .WithHeader("x-forwarded-host", "attacker.example");

        var untrusted = await V1Host(_ => { }).SendApiGatewayAsync(request);
        var trusted = await V1Host(o => o.TrustForwardedHost = true).SendApiGatewayAsync(request);

        Assert.Equal("https://api.example.com/123", Read(untrusted.Body).Absolute);
        Assert.Equal("https://attacker.example/123", Read(trusted.Body).Absolute);
    }

    [Fact]
    public async Task ApiGatewayV2_BasePath_RoutesTheRemainder()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGatewayV2(apiGateway => apiGateway
                .UseBasePath("/tasks")
                .UseMessageHandlers()))
            .BuildHost();

        var path = "/tasks" + Defaults.RequestUrlPath;
        var response = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(new APIGatewayHttpApiV2ProxyRequest
        {
            Version = "2.0",
            RawPath = path,
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "GET", Path = path }
            },
            Headers = new Dictionary<string, string> { ["host"] = "api.example.com", ["x-forwarded-proto"] = "https" }
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("https://api.example.com/tasks/123", Read(response.Body).Absolute);
    }

    [Fact]
    public async Task AzureFunctions_BasePath_StripsTheApiPrefix()
    {
        // Azure Functions prefixes HTTP routes with /api by default; UseBasePath("/api") strips it.
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseHttp(http => http
                .UseBasePath("/api")
                .UseMessageHandlers()))
            .Build();

        var request = HttpBuilder.Create("GET", "/api" + Defaults.RequestUrlPath, new HeaderEchoRequest())
            .WithHeader("host", "func.example.net")
            .AsAspNetCoreHttpRequest();
        var response = await app.HandleHttpRequest(request) as ContentResult;

        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);
        var body = Read(response.Content);
        Assert.Equal("/api", body.BasePath);
        Assert.Equal(Defaults.RequestUrlPath, body.Path);
        Assert.EndsWith("func.example.net/api/123", body.Absolute);
        Assert.Equal("/api", request.PathBase.Value);
    }

    [Fact]
    public void AspNetCore_SetBasePath_MovesThePrefixIntoPathBase_LikeAppMap()
    {
        var httpContext = new global::Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Path = "/tasks/1";
        var context = new Benzene.AspNet.Core.AspNetContext(httpContext);
        var location = new Benzene.AspNet.Core.AspNetHttpRequestLocation();

        location.SetBasePath(context, "/tasks", "/1");

        Assert.Equal("/tasks", httpContext.Request.PathBase.Value);
        Assert.Equal("/1", location.GetPath(context));
        Assert.Equal("http", location.GetScheme(context));
    }
}
