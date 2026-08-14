using System.Threading.Tasks;
using Benzene.Azure.Function.AspNet;
using Benzene.Azure.Function.AspNet.TestHelpers;
using Benzene.Azure.Function.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.DataAnnotations;
using Benzene.Results;
using Benzene.Test.Examples;
using Benzene.Testing;
using Benzene.Xml;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using ProblemDetails = Benzene.Results.ProblemDetails;

namespace Benzene.Test.Azure;

public class AspNetPipelineTest
{
    private static readonly ExampleRequestPayload Payload = new() { Name = "some-message" };

    [Fact]
    public async Task Send()
    {
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseHttp(http => http
                    .UseMessageHandlers()))
            .Build();

        var request = HttpBuilder.Create("GET", "/example", Payload).AsAspNetCoreHttpRequest();

        var response = await app.HandleHttpRequest(request) as ContentResult;
        Assert.NotNull(response);
        Assert.NotNull(response.Content);
        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Send_Xml()
    {
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection()
            ).Configure(app => app
                .UseHttp(http => http
                    .UseXml()
                    .UseMessageHandlers()))
            .Build();

        var request = HttpBuilder.Create("GET", "/example", Payload)
                .WithHeader("content-type", "application/xml")
                .AsAspNetCoreHttpRequest(new XmlSerializer());

        var response = await app.HandleHttpRequest(request) as ContentResult;

        Assert.NotNull(response);
        Assert.NotNull(response.Content);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/xml", response.ContentType);
    }

    [Fact]
    public async Task Send_ValidationError()
    {
        var app = new InlineAzureFunctionStartUp()
            .ConfigureServices(services => services .ConfigureServiceCollection())
            .Configure(app => app
                .UseHttp(http => http
                    .UseMessageHandlers(x => x.UseDataAnnotationsValidation())))
            .Build();

        var request = HttpBuilder.Create("GET", "/example", new ExampleRequestPayload
        {
            Name = "12345678901"
        }).AsAspNetCoreHttpRequest();

        var response = await app.HandleHttpRequest(request) as ContentResult;

        Assert.NotNull(response);

        var payload = new JsonSerializer().Deserialize<ProblemDetails>(response.Content);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("validation-error", payload.BenzeneStatus);
        // Numeric Status is an HTTP-binding concern filled in by Phase 4 of
        // work/problem-details-plan.md; Phase 3's transport-neutral emission never sets it, even on
        // an HTTP-hosted pipeline like this one - the HTTP response line's 422 above comes from a
        // separate mapper (IHttpStatusCodeMapper), not this body.
        Assert.Null(payload.Status);
        Assert.NotEmpty(payload.Detail);
    }
}
