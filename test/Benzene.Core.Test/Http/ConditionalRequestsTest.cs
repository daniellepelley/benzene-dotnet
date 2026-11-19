using System;
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
using Benzene.Http.Conditional;
using Benzene.Results;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Benzene.Test.Http;

// FR-6 (adopter feature requests, spec 1.3): precondition-failed (412) and precondition-required (428)
// for If-Match, and the HTTP binding's 304 rule for If-None-Match.
public class ConditionalRequestsTest
{
    [Theory]
    [InlineData("\"v2\"", "\"v2\"", true)]
    [InlineData("\"v1\", \"v2\"", "\"v2\"", true)]
    [InlineData("*", "\"v2\"", true)]
    [InlineData("\"v1\"", "\"v2\"", false)]
    [InlineData("W/\"v2\"", "\"v2\"", false)]
    [InlineData("\"v2\"", "W/\"v2\"", false)]
    [InlineData(null, "\"v2\"", false)]
    public void IfMatch_IsAStrongComparison(string header, string current, bool expected)
    {
        Assert.Equal(expected, EntityTags.IfMatch(header, current));
    }

    [Theory]
    [InlineData("\"v2\"", "\"v2\"", true)]
    [InlineData("W/\"v2\"", "\"v2\"", true)]
    [InlineData("\"v2\"", "W/\"v2\"", true)]
    [InlineData("\"v1\", W/\"v2\"", "\"v2\"", true)]
    [InlineData("*", "\"v2\"", true)]
    [InlineData("\"v1\"", "\"v2\"", false)]
    [InlineData("", "\"v2\"", false)]
    public void IfNoneMatch_IsAWeakComparison(string header, string current, bool expected)
    {
        Assert.Equal(expected, EntityTags.IfNoneMatch(header, current));
    }

    [Fact]
    public void CheckIfMatch()
    {
        Assert.Equal(BenzeneResultStatus.PreconditionRequired, Preconditions.CheckIfMatch<string>(null, "\"v2\"")!.Status);
        Assert.Null(Preconditions.CheckIfMatch<string>(null, "\"v2\"", required: false));
        Assert.Equal(BenzeneResultStatus.PreconditionFailed, Preconditions.CheckIfMatch<string>("\"v1\"", "\"v2\"")!.Status);
        Assert.Null(Preconditions.CheckIfMatch<string>("\"v2\"", "\"v2\""));
    }

    [Fact]
    public void TheNewStatusesAreFailures()
    {
        Assert.True(BenzeneResultStatus.IsFailure(BenzeneResultStatus.PreconditionFailed));
        Assert.True(BenzeneResultStatus.IsFailure(BenzeneResultStatus.PreconditionRequired));
        Assert.False(BenzeneResult.PreconditionFailed<string>().IsSuccessful);
        Assert.Equal(BenzeneResultStatus.Ok, System.Net.HttpStatusCode.NotModified.Convert().Status);
        Assert.Equal(BenzeneResultStatus.PreconditionFailed, System.Net.HttpStatusCode.PreconditionFailed.Convert().Status);
    }

    private static AwsLambdaBenzeneTestHost Host()
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway.UseMessageHandlers()))
            .BuildHost();
    }

    private static HttpBuilder<ETagTaskRequest> Get(string ifNoneMatch = null)
    {
        var builder = HttpBuilder.Create("GET", Defaults.ETagTaskPath, new ETagTaskRequest())
            .WithHeader("x-correlation-id", Guid.NewGuid().ToString());
        return ifNoneMatch == null ? builder : builder.WithHeader("if-none-match", ifNoneMatch);
    }

    private static HttpBuilder<ETagTaskRequest> Put(string ifMatch = null)
    {
        var builder = HttpBuilder.Create("PUT", Defaults.ETagTaskPath, new ETagTaskRequest { Title = "new" })
            .WithHeader("x-correlation-id", Guid.NewGuid().ToString());
        return ifMatch == null ? builder : builder.WithHeader("if-match", ifMatch);
    }

    [Fact]
    public async Task Get_NoIfNoneMatch_Is200WithTheETag()
    {
        var response = await Host().SendApiGatewayAsync(Get());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(ETagTaskStore.CurrentETag, response.Headers["ETag"]);
        Assert.Contains("write the docs", response.Body);
    }

    [Theory]
    [InlineData("\"v2\"")]
    [InlineData("W/\"v2\"")]
    [InlineData("*")]
    public async Task Get_MatchingIfNoneMatch_Is304WithNoBody(string ifNoneMatch)
    {
        var response = await Host().SendApiGatewayAsync(Get(ifNoneMatch));

        Assert.Equal(304, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(response.Body));
        Assert.Equal(ETagTaskStore.CurrentETag, response.Headers["ETag"]);
    }

    [Fact]
    public async Task Get_StaleIfNoneMatch_Is200()
    {
        var response = await Host().SendApiGatewayAsync(Get("\"v1\""));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("write the docs", response.Body);
    }

    [Fact]
    public async Task Put_NoIfMatch_Is428WithAProblemDocument()
    {
        var response = await Host().SendApiGatewayAsync(Put());

        Assert.Equal(428, response.StatusCode);
        Assert.Contains("https://benzene.app/problems/precondition-required", response.Body);
    }

    [Fact]
    public async Task Put_StaleIfMatch_Is412()
    {
        var response = await Host().SendApiGatewayAsync(Put("\"v1\""));

        Assert.Equal(412, response.StatusCode);
        Assert.Contains("https://benzene.app/problems/precondition-failed", response.Body);
    }

    [Fact]
    public async Task Put_CurrentIfMatch_Succeeds_AndAnIfNoneMatchOnAPutIsNot304()
    {
        var response = await Host().SendApiGatewayAsync(Put("\"v2\"").WithHeader("if-none-match", "\"v3\""));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(EntityTags.Strong("v3"), response.Headers["ETag"]);
    }

    [Fact]
    public async Task AzureFunctions_MatchingIfNoneMatch_Is304()
    {
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseHttp(http => http.UseMessageHandlers()))
            .Build();

        var response = await app.HandleHttpRequest(Get("\"v2\"").AsAspNetCoreHttpRequest()) as ContentResult;

        Assert.NotNull(response);
        Assert.Equal(304, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(response.Content));
    }

    [Fact]
    public async Task ApiGatewayV2_StaleIfMatch_Is412()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app.UseApiGatewayV2(apiGateway => apiGateway.UseMessageHandlers()))
            .BuildHost();

        var response = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(new APIGatewayHttpApiV2ProxyRequest
        {
            Version = "2.0",
            RawPath = Defaults.ETagTaskPath,
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "PUT", Path = Defaults.ETagTaskPath }
            },
            Headers = new System.Collections.Generic.Dictionary<string, string>
            {
                ["x-correlation-id"] = Guid.NewGuid().ToString(),
                ["if-match"] = "\"v1\""
            },
            Body = "{\"title\":\"new\"}"
        });

        Assert.Equal(412, response.StatusCode);
    }
}
