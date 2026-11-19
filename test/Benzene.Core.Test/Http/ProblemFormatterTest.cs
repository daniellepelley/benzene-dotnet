using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Benzene.Abstractions.Results;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Azure.Function.AspNet;
using Benzene.Azure.Function.AspNet.TestHelpers;
using Benzene.Azure.Function.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.Core.Messages;
using Benzene.Http.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Benzene.Testing;
using ContentResult = Microsoft.AspNetCore.Mvc.ContentResult;
using Newtonsoft.Json.Linq;
using Xunit;
using AzureAspNetContext = Benzene.Azure.Function.AspNet.AspNetContext;

namespace Benzene.Test.Http;

// FR-9 (adopter feature requests): a service keeps an error body format its callers already depend on.
// The formatter is registered per context, so the HTTP routes change and the envelope endpoint keeps
// the RFC 9457 document the spec requires.
public class ProblemFormatterTest
{
    // The adopter's existing format: { "error": { "code", "message", "fields": [...] } }.
    private static object LegacyError(ProblemDetails problem, IBenzeneResult result) => new
    {
        error = new
        {
            code = result.Status,
            message = problem.Detail ?? problem.Title,
            httpStatus = problem.Status,
            fields = problem.Errors?.Select(e => new { name = e.Field, reason = e.Message }).ToArray(),
        }
    };

    private static AwsLambdaBenzeneTestHost V1Host(bool format, Func<IBenzeneResult, bool> applies = null)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services
                .ConfigureServiceCollection()
                .UsingBenzene(x =>
                {
                    if (format)
                    {
                        x.AddProblemFormatter<ApiGatewayContext>(LegacyError, applies: applies);
                    }
                }))
            .Configure(app => app.UseApiGateway(apiGateway => apiGateway
                .UseBenzeneMessage(new BenzeneMessageHttpOptions(), messageApp => messageApp.UseMessageHandlers())
                .UseMessageHandlers()))
            .BuildHost();
    }

    private static Task<APIGatewayProxyResponse> Post(AwsLambdaBenzeneTestHost host, string path, object body = null)
    {
        return host.SendApiGatewayAsync(HttpBuilder.Create("POST", path, body ?? new ProblemBodyRequest()));
    }

    [Fact]
    public async Task ApiGatewayV1_ByDefault_AValidationErrorIsAProblemDocument()
    {
        var response = await Post(V1Host(format: false), Defaults.ProblemValidationPath);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("application/problem+json", response.Headers["content-type"]);
        var body = JObject.Parse(response.Body);
        Assert.Equal(ProblemTypes.ValidationError, (string)body["type"]);
        Assert.Equal(422, (int)body["status"]);
        Assert.Equal("/name", (string)body["errors"]![0]!["field"]);
    }

    [Fact]
    public async Task ApiGatewayV1_WithAFormatter_AValidationErrorIsInTheCustomFormat()
    {
        var response = await Post(V1Host(format: true), Defaults.ProblemValidationPath);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("application/json", response.Headers["content-type"]);
        var error = JObject.Parse(response.Body)["error"]!;
        Assert.Equal(BenzeneResultStatus.ValidationError, (string)error["code"]);
        Assert.Equal(422, (int)error["httpStatus"]);
        Assert.Equal("/name", (string)error["fields"]![0]!["name"]);
        Assert.Null(JObject.Parse(response.Body)["type"]);
    }

    [Fact]
    public async Task ApiGatewayV1_AnUnmatchedRoute_HasAProblemDocumentByDefault()
    {
        var response = await Post(V1Host(format: false), "/no-such-route");

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(ProblemTypes.NotFound, (string)JObject.Parse(response.Body)["type"]);
    }

    [Fact]
    public async Task ApiGatewayV1_AnUnmatchedRoute_IsInTheCustomFormatWhenOverridden()
    {
        var response = await Post(V1Host(format: true), "/no-such-route");

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(BenzeneResultStatus.NotFound, (string)JObject.Parse(response.Body)["error"]!["code"]);
    }

    [Fact]
    public async Task ApiGatewayV1_ASuccessIsUntouched()
    {
        var response = await V1Host(format: true).SendApiGatewayAsync(HttpBuilder.Create("GET", Defaults.HeaderEchoPath));

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("1", (string)JObject.Parse(response.Body)["id"]);
    }

    [Fact]
    public async Task ApiGatewayV1_AppliesNarrowsWhichFailuresAreFormatted()
    {
        var host = V1Host(format: true, applies: r => r.Status == BenzeneResultStatus.ValidationError);

        var validation = await Post(host, Defaults.ProblemValidationPath);
        var notFound = await Post(host, "/no-such-route");

        Assert.NotNull(JObject.Parse(validation.Body)["error"]);
        Assert.Equal("application/json", validation.Headers["content-type"]);
        Assert.Equal(ProblemTypes.NotFound, (string)JObject.Parse(notFound.Body)["type"]);
        Assert.Equal("application/problem+json", notFound.Headers["content-type"]);
    }

    [Fact]
    public async Task Envelope_KeepsTheProblemDocument_WhenTheFormatterIsForTheHttpContext()
    {
        var envelope = new { topic = Defaults.ProblemValidationTopic, headers = new Dictionary<string, string>(), body = "{}" };

        var response = await V1Host(format: true).SendApiGatewayAsync(HttpBuilder.Create("POST", "/benzene/invoke", envelope));

        var inner = JObject.Parse(response.Body);
        Assert.False((bool)inner["isSuccessful"]);
        var problem = JObject.Parse((string)inner["body"]!);
        Assert.Equal(ProblemTypes.ValidationError, (string)problem["type"]);
        Assert.Equal("/name", (string)problem["errors"]![0]!["field"]);
    }

    [Fact]
    public async Task ApiGatewayV1_AHandlerAuthoredProblemKeepsItsTypeAndExtensionMembers()
    {
        // Regression: the HTTP mapper rebuilt the document from the status, dropping the handler's own
        // type, title and extension members on every HTTP route.
        var response = await Post(V1Host(format: false), Defaults.ProblemAuthoredPath);

        Assert.Equal(409, response.StatusCode);
        var body = JObject.Parse(response.Body);
        Assert.Equal(OutOfStockProblem.TypeUri, (string)body["type"]);
        Assert.Equal("Out of stock", (string)body["title"]);
        Assert.Equal("SKU-1", (string)body["sku"]);
        Assert.Equal(409, (int)body["status"]);
    }

    [Fact]
    public async Task ApiGatewayV2_WithAFormatter_AValidationErrorAndAnUnmatchedRouteAreInTheCustomFormat()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services
                .ConfigureServiceCollection()
                .UsingBenzene(x => x.AddProblemFormatter<ApiGatewayV2Context>(LegacyError)))
            .Configure(app => app.UseApiGatewayV2(apiGateway => apiGateway.UseMessageHandlers()))
            .BuildHost();

        var validation = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(V2Request(Defaults.ProblemValidationPath));
        var notFound = await host.SendEventAsync<APIGatewayHttpApiV2ProxyResponse>(V2Request("/no-such-route"));

        Assert.Equal(422, validation.StatusCode);
        Assert.Equal("application/json", validation.Headers["content-type"]);
        Assert.Equal("/name", (string)JObject.Parse(validation.Body)["error"]!["fields"]![0]!["name"]);
        Assert.Equal(404, notFound.StatusCode);
        Assert.Equal(BenzeneResultStatus.NotFound, (string)JObject.Parse(notFound.Body)["error"]!["code"]);
    }

    private static APIGatewayHttpApiV2ProxyRequest V2Request(string path)
    {
        return new APIGatewayHttpApiV2ProxyRequest
        {
            Version = "2.0",
            RawPath = path,
            Body = "{}",
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "POST", Path = path }
            },
            Headers = new Dictionary<string, string>
            {
                { "x-correlation-id", Guid.NewGuid().ToString() },
                { "content-type", "application/json" }
            }
        };
    }

    [Theory]
    [InlineData(Defaults.ProblemValidationPath, 422, BenzeneResultStatus.ValidationError)]
    [InlineData("/no-such-route", 404, BenzeneResultStatus.NotFound)]
    public async Task AzureFunctions_WithAFormatter_FailuresAreInTheCustomFormat(string path, int status, string code)
    {
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services
                .ConfigureServiceCollection()
                .UsingBenzene(x => x.AddProblemFormatter<AzureAspNetContext>(LegacyError)))
            .Configure(app => app.UseHttp(http => http.UseMessageHandlers()))
            .Build();

        var request = HttpBuilder.Create("POST", path, new ProblemBodyRequest()).AsAspNetCoreHttpRequest();
        var response = await app.HandleHttpRequest(request) as ContentResult;

        Assert.NotNull(response);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal(code, (string)JObject.Parse(response.Content)["error"]!["code"]);
    }

    [Fact]
    public void AMapper_GivesARejectionWithNoHandlerABody()
    {
        // A middleware rejection before routing (rate limiting an unknown topic) has no handler
        // definition. wire-contracts §1.3: a failure's body is its problem document, so it still has one.
        var result = new MessageHandlerResult(new Topic("unknown"), null, BenzeneResult.TooManyRequests("slow down"));

        var body = new DefaultResponsePayloadMapper<object>().Map(new object(), result, new JsonSerializer());

        Assert.Equal(ProblemTypes.TooManyRequests, (string)JObject.Parse(body)["type"]);
    }

    [Fact]
    public void AMapper_GivesASuccessWithNoHandlerNoBody()
    {
        var result = new MessageHandlerResult(new Topic("cors"), null, BenzeneResult.Ok());

        Assert.Null(new DefaultResponsePayloadMapper<object>().Map(new object(), result, new JsonSerializer()));
    }

    [Fact]
    public void AStringFromTheFormatterIsWrittenAsItIs()
    {
        var formatter = new DelegateProblemFormatter<object>((p, r) => "<error>" + r.Status + "</error>", "application/xml");
        var result = new MessageHandlerResult(new Topic("x"), null, BenzeneResult.NotFound("gone"));

        var body = new DefaultResponsePayloadMapper<object>(formatter).Map(new object(), result, new JsonSerializer());

        Assert.Equal("<error>not-found</error>", body);
    }
}
