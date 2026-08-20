using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.AspNet.Core;
using Benzene.Conformance.Test.Handlers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.HostedService;
using Benzene.Http.Routing;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Conformance.Test;

/// <summary>
/// Runs docs/specification/conformance/problem-details-cases.json (wire-contracts.md §1.3, §3.1,
/// §4.1) - the gate that Phases 3-5 of work/archive/problem-details-plan-2026-08.md actually implemented what
/// Phase 1 pinned. Mirrors <see cref="StatusMappingConformanceTest"/>'s and
/// <see cref="EnvelopeConformanceTest"/>'s patterns: <c>registry</c> is asserted directly against
/// <see cref="Benzene.Results.ProblemTypes"/>, <c>envelopeCases</c> run through the real
/// BenzeneMessage pipeline with the canonical <c>conformance:problem</c> handler registered
/// alongside the other canonical handlers, and <c>httpRules</c> run against the AspNet-hosted
/// pipeline (this repo's reference HTTP binding) - the only group of the three that needs a real
/// HTTP response line to assert against.
/// </summary>
public class ProblemDetailsConformanceTest
{
    public class ProblemDetailsFixture
    {
        public RegistryGroup Registry { get; set; } = new();
        public List<EnvelopeConformanceTest.EnvelopeCase> EnvelopeCases { get; set; } = new();
        public HttpRulesGroup HttpRules { get; set; } = new();
    }

    public class RegistryGroup
    {
        public List<RegistryRow> Rows { get; set; } = new();
        public UnknownStatusRow UnknownStatus { get; set; } = new();
    }

    public class RegistryRow
    {
        public string BenzeneStatus { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int HttpStatus { get; set; }
    }

    public class UnknownStatusRow
    {
        public int HttpStatus { get; set; }
    }

    public class HttpRulesGroup
    {
        public List<HttpRuleRow> FailureCases { get; set; } = new();
        public HttpRuleSuccessCase SuccessCase { get; set; } = new();
    }

    public class HttpRuleRow
    {
        public string BenzeneStatus { get; set; } = string.Empty;
        public int HttpStatus { get; set; }
    }

    public class HttpRuleSuccessCase
    {
        public string BenzeneStatus { get; set; } = string.Empty;
        public int HttpStatus { get; set; }
        public string ContentType { get; set; } = string.Empty;
    }

    private static readonly Lazy<ProblemDetailsFixture> Fixture = new(() =>
        ConformanceFixtures.Load<ProblemDetailsFixture>("problem-details-cases.json"));

    // ------------------------------------------------------------------
    // registry - directly assertable against Benzene.Results.ProblemTypes, no message to build.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> RegistryRows() =>
        Fixture.Value.Registry.Rows.Select(x => new object[] { x.BenzeneStatus, x.Type, x.HttpStatus });

    [Theory]
    [MemberData(nameof(RegistryRows))]
    public void Registry_RowMatchesProblemTypes(string benzeneStatus, string expectedType, int expectedHttpStatus)
    {
        Assert.Equal(expectedType, ProblemTypes.TypeFor(benzeneStatus));
        Assert.Equal(expectedHttpStatus, ProblemTypes.HttpStatusFor(benzeneStatus));
    }

    [Fact]
    public void Registry_UnknownStatus_HasNoTypeAndFallsToTheGenericErrorHttpStatus()
    {
        const string unknownStatus = "some-application-defined-status";

        Assert.Null(ProblemTypes.TypeFor(unknownStatus));
        Assert.Equal(Fixture.Value.Registry.UnknownStatus.HttpStatus, ProblemTypes.HttpStatusFor(unknownStatus));
    }

    // ------------------------------------------------------------------
    // envelopeCases - run against the canonical conformance:problem handler, same case format (and
    // bodyExclude support) as EnvelopeConformanceTest.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> EnvelopeCaseNames() =>
        Fixture.Value.EnvelopeCases.Select(x => new object[] { x.Name });

    [Theory]
    [MemberData(nameof(EnvelopeCaseNames))]
    public async Task EnvelopeCase_ProducesTheExpectedProblemDocument(string caseName)
    {
        var envelopeCase = Fixture.Value.EnvelopeCases.Single(x => x.Name == caseName);

        var response = await RunPipelineAsync(new BenzeneMessageRequest
        {
            Topic = envelopeCase.Request.Topic,
            Headers = envelopeCase.Request.Headers,
            Body = envelopeCase.Request.Body
        });

        Assert.NotNull(response);
        Assert.Equal(envelopeCase.Expected.StatusCode, response.StatusCode);

        if (envelopeCase.Expected.IsSuccessful is { } expectedIsSuccessful)
        {
            Assert.True(expectedIsSuccessful == response.IsSuccessful,
                $"{caseName}: expected isSuccessful={expectedIsSuccessful} but found {response.IsSuccessful}");
        }

        if (envelopeCase.Expected.Body is { } expectedBody)
        {
            Assert.False(string.IsNullOrEmpty(response.Body), $"{caseName}: expected a response body but none was written");
            using var actualBody = JsonDocument.Parse(response.Body);
            var mismatch = ConformanceFixtures.FindSubsetMismatch(expectedBody, actualBody.RootElement);
            Assert.True(mismatch == null, $"{caseName}: body mismatch at {mismatch}");
        }

        if (envelopeCase.Expected.BodyExclude is { } bodyExclude)
        {
            Assert.False(string.IsNullOrEmpty(response.Body), $"{caseName}: expected a response body but none was written");
            using var actualBody = JsonDocument.Parse(response.Body);
            foreach (var excludedMember in bodyExclude)
            {
                Assert.False(actualBody.RootElement.TryGetProperty(excludedMember, out _),
                    $"{caseName}: body member '{excludedMember}' was expected to be genuinely absent but was present");
            }
        }
    }

    private static async Task<IBenzeneMessageResponse> RunPipelineAsync(BenzeneMessageRequest request)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipelineBuilder.UseMessageHandlers(
            typeof(GreetConformanceHandler),
            typeof(StatusConformanceHandler),
            typeof(ProblemConformanceHandler));
        var pipeline = pipelineBuilder.Build();

        var application = new BenzeneMessageApplication(pipeline);
        return await application.HandleAsync(request, container.CreateServiceResolverFactory());
    }

    // ------------------------------------------------------------------
    // httpRules - required only for ports that ship an HTTP binding; run against the AspNet-hosted
    // pipeline (Benzene.AspNet.Core, Kestrel, over a real socket - the same probe shape as
    // AspNetProblemDetailsPipelineTest, chosen for the same reason: only a real response line
    // reliably flips the HTTP status code and content-type this group asserts on). The canonical
    // conformance:status handler drives every row: it returns the requested benzeneStatus verbatim,
    // which is exactly what this group needs to walk every row of the registry over a real HTTP
    // response.
    // ------------------------------------------------------------------

    public class HttpRulesStartUp : BenzeneStartUp
    {
        public static int Port;

        public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

        public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
            => services.UsingBenzene(x => x
                .AddBenzene()
                .AddScoped<StatusConformanceHandler>()
                .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                    "conformance:status", "", typeof(StatusRequest), typeof(StatusReply), typeof(StatusConformanceHandler)))
                .AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("POST", "/conformance-status", "conformance:status")));

        public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
            .UseWorker(worker => worker.UseAspNet(
                asp => asp.UseMessageHandlers(),
                options => options.Urls = $"http://127.0.0.1:{Port}"));
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int StatusCode, string? ContentType, string Body)> PostStatusAsync(int port, string benzeneStatus)
    {
        var host = new HostBuilder().UseBenzene<HttpRulesStartUp>().Build();
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        foreach (var service in hostedServices)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            using var client = new HttpClient();
            var jsonBody = JsonSerializer.Serialize(new { status = benzeneStatus });
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/conformance-status",
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

    public static IEnumerable<object[]> HttpRuleFailureCases() =>
        Fixture.Value.HttpRules.FailureCases.Select(x => new object[] { x.BenzeneStatus, x.HttpStatus });

    [Theory]
    [MemberData(nameof(HttpRuleFailureCases))]
    public async Task HttpRule_FailureCase_MapsToTheHttpStatusLineAndTheSameStatusInTheBody(string benzeneStatus, int expectedHttpStatus)
    {
        HttpRulesStartUp.Port = GetFreePort();
        var (statusCode, contentType, body) = await PostStatusAsync(HttpRulesStartUp.Port, benzeneStatus);

        Assert.Equal(expectedHttpStatus, statusCode);
        Assert.NotNull(contentType);
        Assert.StartsWith("application/problem+json", contentType);

        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.TryGetProperty("status", out var statusMember),
            $"{benzeneStatus}: expected a numeric 'status' member on the HTTP-bound problem document");
        Assert.Equal(expectedHttpStatus, statusMember.GetInt32());
    }

    [Fact]
    public async Task HttpRule_SuccessCase_IsUnaffected_NoProblemDocumentOrdinaryContentType()
    {
        var successCase = Fixture.Value.HttpRules.SuccessCase;
        HttpRulesStartUp.Port = GetFreePort();
        var (statusCode, contentType, body) = await PostStatusAsync(HttpRulesStartUp.Port, successCase.BenzeneStatus);

        Assert.Equal(successCase.HttpStatus, statusCode);
        Assert.NotNull(contentType);
        Assert.StartsWith(successCase.ContentType, contentType);
        Assert.DoesNotContain("\"status\"", body);
        Assert.DoesNotContain("\"benzeneStatus\"", body);
        Assert.DoesNotContain("\"type\"", body);
    }
}
