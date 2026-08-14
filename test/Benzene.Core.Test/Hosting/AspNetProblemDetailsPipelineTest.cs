using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.DataAnnotations;
using Benzene.HostedService;
using Benzene.Http.Routing;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Test.Hosting;

// Deliberately not annotated with [Message]/[HttpEndpoint] - see AspNetUnifiedStartUpTest's
// PingHandler for why (a bare reflection-scan .UseMessageHandlers() would otherwise pick this up
// from any other test in the assembly).
public class QuarantinedNameRequest
{
    [Required]
    [MaxLength(5)]
    public string Name { get; set; } = "";
}

public class QuarantinedNameHandler : IMessageHandler<QuarantinedNameRequest, Void>
{
    public Task<IBenzeneResult<Void>> HandleAsync(QuarantinedNameRequest request)
        => Task.FromResult(BenzeneResult.Ok(new Void()));
}

public class AspNetProblemDetailsStartUp : BenzeneStartUp
{
    public static int Port;

    public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddBenzene()
            .AddMessageHandlers()
            .AddScoped<QuarantinedNameHandler>()
            .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                "aspnet-problem-details-name", "", typeof(QuarantinedNameRequest), typeof(Void), typeof(QuarantinedNameHandler)))
            .AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("POST", "/name", "aspnet-problem-details-name")));

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
        .UseWorker(worker => worker.UseAspNet(
            asp => asp.UseMessageHandlers(x => x.UseDataAnnotationsValidation()),
            options => options.Urls = $"http://127.0.0.1:{Port}"));
}

/// <summary>
/// Coverage for Phase 4 of work/problem-details-plan.md over the reference self-host HTTP binding
/// (<c>Benzene.AspNet.Core</c>, Kestrel, over a real socket - the same probe shape as
/// <c>Customization/CustomStatusProbeTest.cs</c>, chosen because the in-memory
/// <see cref="Microsoft.AspNetCore.Http.DefaultHttpContext"/> pattern
/// <see cref="AspNetUnifiedStartUpTest"/> uses never flips <c>Response.HasStarted</c>, so
/// <see cref="AspApplicationBuilder"/> falls through to ASP.NET Core's own default 404 terminal
/// handler afterwards and clobbers the status code this test needs to assert on): a validation
/// failure gets the mapped HTTP status line, <c>application/problem+json</c>, and a body
/// <c>status</c> member equal to the response code - derived from the same
/// <see cref="Benzene.Http.IHttpStatusCodeMapper"/> in both places.
/// </summary>
public class AspNetProblemDetailsPipelineTest
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int StatusCode, string? ContentType, string Body)> PostAsync(int port, string jsonBody)
    {
        var host = new HostBuilder().UseBenzene<AspNetProblemDetailsStartUp>().Build();
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        foreach (var service in hostedServices)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/name",
                new StringContent(jsonBody, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), body);
        }
        finally
        {
            foreach (var service in hostedServices)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Send_ValidationError_MapsToTheHttpStatusLineAndCarriesTheSameStatusInTheBody()
    {
        AspNetProblemDetailsStartUp.Port = GetFreePort();
        var (statusCode, contentType, body) = await PostAsync(AspNetProblemDetailsStartUp.Port, "{\"name\":\"way-too-long-a-name\"}");

        var problem = new JsonSerializer().Deserialize<ProblemDetails>(body);

        Assert.Equal(422, statusCode);
        Assert.StartsWith("application/problem+json", contentType);
        Assert.Equal(422, problem.Status);
        Assert.Equal("validation-error", problem.BenzeneStatus);
        Assert.NotEmpty(problem.Detail);
    }

    [Fact]
    public async Task Send_SuccessfulResult_IsUnaffected_PlainJsonNoStatusOrBenzeneStatusMember()
    {
        AspNetProblemDetailsStartUp.Port = GetFreePort();
        var (statusCode, contentType, body) = await PostAsync(AspNetProblemDetailsStartUp.Port, "{\"name\":\"ok\"}");

        Assert.Equal(200, statusCode);
        Assert.StartsWith("application/json", contentType);
        Assert.DoesNotContain("\"status\"", body);
        Assert.DoesNotContain("\"benzeneStatus\"", body);
    }
}
