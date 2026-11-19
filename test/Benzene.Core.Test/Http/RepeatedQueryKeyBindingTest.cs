using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.AspNet.Core;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Azure.Function.AspNet;
using Benzene.Azure.Function.AspNet.TestHelpers;
using Benzene.Azure.Function.Core;
using Benzene.Core.Helper;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Http.Routing;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Benzene.Test.Http;

// FR-1 (adopter feature requests): a repeated query key binds every value to a collection property,
// and the first value to a scalar, the same on every HTTP host.
public class RepeatedQueryKeyBindingTest
{
    // An empty body object: the Azure test helper serializes a missing message as the JSON text "null".
    private static HttpBuilder<QueryEchoRequest> Request()
    {
        return HttpBuilder.Create("GET", Defaults.QueryEchoPath, new QueryEchoRequest())
            .WithHeader("x-correlation-id", Guid.NewGuid().ToString())
            .WithQuery("ids", "a", "b")
            .WithQuery("numbers", "1", "2")
            .WithQuery("statuses", "open", "BLOCKED")
            .WithQuery("id", "first", "second");
    }

    private static void AssertBound(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("first", root.GetProperty("id").GetString());
        Assert.Equal(new[] { "a", "b" }, Strings(root.GetProperty("ids")));
        Assert.Equal(new[] { 1, 2 }, Ints(root.GetProperty("numbers")));
        Assert.Equal(2, root.GetProperty("statuses").GetArrayLength());
    }

    private static IEnumerable<string> Strings(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            yield return item.GetString();
        }
    }

    private static IEnumerable<int> Ints(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            yield return item.GetInt32();
        }
    }

    [Fact]
    public async Task ApiGatewayV1_RepeatedKeys_BindCollectionsAndFirstValueForScalars()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway.UseMessageHandlers()))
            .BuildHost();

        var response = await host.SendApiGatewayAsync(Request().AsApiGatewayRequest());

        Assert.Equal(200, response.StatusCode);
        AssertBound(response.Body);
    }

    [Fact]
    public async Task ApiGatewayV1_ElementThatDoesNotConvert_IsBadRequest()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway.UseMessageHandlers()))
            .BuildHost();

        var request = HttpBuilder.Create("GET", Defaults.QueryEchoPath)
            .WithQuery("numbers", "1", "x")
            .AsApiGatewayRequest();
        var response = await host.SendApiGatewayAsync(request);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task ApiGatewayV2_CommaJoinedRepeatedKeys_BindCollectionsAndFirstValueForScalars()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGatewayV2(apiGateway => apiGateway.UseMessageHandlers()))
            .BuildHost();

        // API Gateway HTTP APIs comma-join a repeated key before Lambda sees the event.
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            Version = "2.0",
            RawPath = Defaults.QueryEchoPath,
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "GET", Path = Defaults.QueryEchoPath }
            },
            Headers = new Dictionary<string, string> { { "x-correlation-id", Guid.NewGuid().ToString() } },
            QueryStringParameters = new Dictionary<string, string>
            {
                ["ids"] = "a,b",
                ["numbers"] = "1,2",
                ["statuses"] = "open,BLOCKED",
                ["id"] = "first,second"
            }
        };
        var response = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(request);

        Assert.Equal(200, response.StatusCode);
        AssertBound(response.Body);
    }

    [Fact]
    public async Task AzureFunctions_RepeatedKeys_BindCollectionsAndFirstValueForScalars()
    {
        // Before FR-1 this host comma-joined a repeated key into a scalar ("first,second").
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseHttp(http => http.UseMessageHandlers()))
            .Build();

        var response = await app.HandleHttpRequest(Request().AsAspNetCoreHttpRequest()) as ContentResult;

        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);
        AssertBound(response.Content);
    }

    [Fact]
    public void AspNetCore_RepeatedKeys_BindCollectionsAndFirstValueForScalars()
    {
        var routeFinder = new Mock<IRouteFinder>();
        routeFinder
            .Setup(x => x.Find(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new HttpTopicRoute(Defaults.QueryEchoTopic, new Dictionary<string, object>()));
        var mappings = new Mock<IHttpHeaderMappings>();
        mappings.Setup(x => x.GetMappings()).Returns(new Dictionary<string, string>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = Defaults.QueryEchoPath;
        httpContext.Request.QueryString = new QueryString("?ids=a&ids=b&numbers=1&numbers=2&statuses=open&statuses=BLOCKED&id=first&id=second");

        var enriched = new AspNetRequestEnricher(routeFinder.Object, mappings.Object)
            .Enrich(new QueryEchoRequest(), new Benzene.AspNet.Core.AspNetContext(httpContext));
        var bound = DictionaryUtils.Enrich(new QueryEchoRequest(), enriched);

        Assert.Equal("first", bound.Id);
        Assert.Equal(new[] { "a", "b" }, bound.Ids);
        Assert.Equal(new List<int> { 1, 2 }, bound.Numbers);
        Assert.Equal(new[] { QueryEchoStatus.Open, QueryEchoStatus.Blocked }, bound.Statuses);
    }
}
