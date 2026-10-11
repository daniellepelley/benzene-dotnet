using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.AspNet.Core;
using Benzene.Conformance.Test.Handlers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.FluentValidation;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.HostedService;
using Benzene.Http.BenzeneMessage;
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
        public EnvelopeEndpointGroup EnvelopeEndpoint { get; set; } = new();
        public HealthEndpointGroup HealthEndpoint { get; set; } = new();
    }

    public class EnvelopeEndpointGroup
    {
        public string Path { get; set; } = string.Empty;
        public List<EnvelopeEndpointCase> Cases { get; set; } = new();
    }

    public class EnvelopeEndpointCase
    {
        public string Name { get; set; } = string.Empty;
        public EnvelopeConformanceTest.EnvelopeRequest? Request { get; set; }
        public string? RawBody { get; set; }
        public EnvelopeEndpointExpectation Expected { get; set; } = new();
    }

    public class EnvelopeEndpointExpectation
    {
        public int HttpStatus { get; set; }
        public EnvelopeConformanceTest.EnvelopeExpectation? Envelope { get; set; }
    }

    public class HealthEndpointGroup
    {
        public string Path { get; set; } = string.Empty;
        public List<HealthEndpointCase> Cases { get; set; } = new();
    }

    public class HealthEndpointCase
    {
        public string Name { get; set; } = string.Empty;
        public List<HealthEndpointCheck> Checks { get; set; } = new();
        public HealthEndpointExpectation Expected { get; set; } = new();
    }

    public class HealthEndpointCheck
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class HealthEndpointExpectation
    {
        public int HttpStatus { get; set; }
        public JsonElement? Body { get; set; }
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

    // The capabilities this port implements, for cases marked "requires" (README.md): validation is
    // Benzene.FluentValidation/Benzene.DataAnnotations - the runner wires FluentValidation.
    private static readonly HashSet<string> Capabilities = new(StringComparer.Ordinal) { "validation" };

    public static IEnumerable<object[]> EnvelopeCaseNames() =>
        Fixture.Value.EnvelopeCases
            .Where(x => x.Requires == null || Capabilities.Contains(x.Requires))
            .Select(x => new object[] { x.Name });

    [Fact]
    public void EveryRequiredCapability_IsOneThisRunnerKnows()
    {
        // A case requiring a capability this runner has never heard of would be skipped silently -
        // fail instead, so a new capability in the fixture is a decision, not an accident.
        Assert.All(Fixture.Value.EnvelopeCases.Where(x => x.Requires != null),
            x => Assert.Contains(x.Requires!, Capabilities));
    }

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

        if (envelopeCase.Expected.ErrorFields is { } errorFields)
        {
            EnvelopeConformanceTest.AssertErrorFields(caseName, errorFields, response.Body);
        }
    }

    private static async Task<IBenzeneMessageResponse> RunPipelineAsync(BenzeneMessageRequest request)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        // conformance:validate is validated through this port's own integration (README.md's canonical
        // handlers), FluentValidation; the other canonical handlers have no validator, so pass through.
        pipelineBuilder.UseMessageHandlers(
            new[]
            {
                typeof(GreetConformanceHandler),
                typeof(StatusConformanceHandler),
                typeof(ProblemConformanceHandler),
                typeof(ValidateConformanceHandler),
            },
            router => router.UseFluentValidation(new[] { typeof(ValidateConformanceValidator) }));
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

    // ------------------------------------------------------------------
    // httpRules.envelopeEndpoint - the envelope endpoint (transport-bindings §1): every processed
    // envelope is HTTP 200 with the response envelope as the body; only a body that is not an
    // envelope is a carrier failure. Hosted on AspNet like the rows above.
    // ------------------------------------------------------------------

    public class EnvelopeEndpointStartUp : BenzeneStartUp
    {
        public static int Port;
        public static string Path = "/benzene/invoke";

        public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

        public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
            => services.UsingBenzene(x => x.AddBenzene());

        public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
            .UseWorker(worker => worker.UseAspNet(
                asp => asp.UseBenzeneMessage(new BenzeneMessageHttpOptions { Path = Path },
                    message => message.UseMessageHandlers(typeof(GreetConformanceHandler), typeof(StatusConformanceHandler))),
                options => options.Urls = $"http://127.0.0.1:{Port}"));
    }

    public class HealthEndpointStartUp : BenzeneStartUp
    {
        public static int Port;
        public static string Path = "/benzene/health";
        public static List<HealthEndpointCheck> Checks = new();

        public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

        public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
            => services.UsingBenzene(x => x
                .AddBenzene()
                .AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("GET", Path, BenzeneTopic.HealthCheck)));

        public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        {
            // Exactly the case's checks, each reporting its stated status.
            var checks = Checks.Select(check => (IHealthCheck)new InlineHealthCheck(check.Name, () => Task.FromResult(check.Status switch
            {
                HealthCheckStatus.Ok => HealthCheckResult.CreateInstance(true, check.Name),
                HealthCheckStatus.Warning => HealthCheckResult.CreateWarning(check.Name),
                HealthCheckStatus.Failed => HealthCheckResult.CreateInstance(false, check.Name),
                _ => throw new InvalidOperationException($"Unknown check status '{check.Status}' in the fixture.")
            }))).ToArray();

            app.UseWorker(worker => worker.UseAspNet(
                asp => asp
                    .UseHealthCheck(BenzeneTopic.HealthCheck, checks)
                    .UseMessageHandlers(typeof(GreetConformanceHandler)),
                options => options.Urls = $"http://127.0.0.1:{Port}"));
        }
    }

    private static async Task<(int StatusCode, string Body)> SendAsync<TStartUp>(Func<HttpClient, Task<HttpResponseMessage>> send)
        where TStartUp : BenzeneStartUp, new()
    {
        var host = new HostBuilder().UseBenzene<TStartUp>().Build();
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        foreach (var service in hostedServices)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            using var client = new HttpClient();
            var response = await send(client);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            foreach (var service in hostedServices)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    public static IEnumerable<object[]> EnvelopeEndpointCaseNames() =>
        Fixture.Value.HttpRules.EnvelopeEndpoint.Cases.Select(x => new object[] { x.Name });

    [Theory]
    [MemberData(nameof(EnvelopeEndpointCaseNames))]
    public async Task HttpRule_EnvelopeEndpoint_AnswersAsTheCaseExpects(string caseName)
    {
        var group = Fixture.Value.HttpRules.EnvelopeEndpoint;
        var endpointCase = group.Cases.Single(x => x.Name == caseName);
        EnvelopeEndpointStartUp.Port = GetFreePort();
        EnvelopeEndpointStartUp.Path = group.Path;

        var requestBody = endpointCase.RawBody ?? JsonSerializer.Serialize(new
        {
            topic = endpointCase.Request!.Topic,
            headers = endpointCase.Request.Headers,
            body = endpointCase.Request.Body
        });
        var (statusCode, body) = await SendAsync<EnvelopeEndpointStartUp>(client => client.PostAsync(
            $"http://127.0.0.1:{EnvelopeEndpointStartUp.Port}{group.Path}",
            new StringContent(requestBody, Encoding.UTF8, "application/json")));

        Assert.True(endpointCase.Expected.HttpStatus == statusCode,
            $"{caseName}: HTTP {statusCode}, expected {endpointCase.Expected.HttpStatus} (body: {body})");

        if (endpointCase.Expected.Envelope is not { } expected)
        {
            return;
        }

        using var envelope = JsonDocument.Parse(body);
        var root = envelope.RootElement;
        Assert.Equal(expected.StatusCode, root.GetProperty("statusCode").GetString());
        if (expected.IsSuccessful is { } expectedIsSuccessful)
        {
            Assert.True(root.TryGetProperty("isSuccessful", out var isSuccessful), $"{caseName}: the envelope has no isSuccessful");
            Assert.Equal(expectedIsSuccessful, isSuccessful.GetBoolean());
        }

        if (expected.Body is null && expected.BodyExclude is null)
        {
            return;
        }

        var innerBody = root.GetProperty("body").GetString();
        Assert.False(string.IsNullOrEmpty(innerBody), $"{caseName}: expected an envelope body but none was written");
        using var inner = JsonDocument.Parse(innerBody!);
        if (expected.Body is { } expectedBody)
        {
            var mismatch = ConformanceFixtures.FindSubsetMismatch(expectedBody, inner.RootElement);
            Assert.True(mismatch == null, $"{caseName}: envelope body mismatch at {mismatch}");
        }
        foreach (var excludedMember in expected.BodyExclude ?? new List<string>())
        {
            Assert.False(inner.RootElement.TryGetProperty(excludedMember, out _),
                $"{caseName}: envelope body member '{excludedMember}' was expected to be genuinely absent but was present");
        }
    }

    // ------------------------------------------------------------------
    // httpRules.healthEndpoint - GET /benzene/health with exactly the case's checks: 200 when
    // isHealthy, 503 when not, the health response as the body either way.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> HealthEndpointCaseNames() =>
        Fixture.Value.HttpRules.HealthEndpoint.Cases.Select(x => new object[] { x.Name });

    [Theory]
    [MemberData(nameof(HealthEndpointCaseNames))]
    public async Task HttpRule_HealthEndpoint_StatusLineCarriesTheVerdict(string caseName)
    {
        var group = Fixture.Value.HttpRules.HealthEndpoint;
        var healthCase = group.Cases.Single(x => x.Name == caseName);
        HealthEndpointStartUp.Port = GetFreePort();
        HealthEndpointStartUp.Path = group.Path;
        HealthEndpointStartUp.Checks = healthCase.Checks;

        var (statusCode, body) = await SendAsync<HealthEndpointStartUp>(client =>
            client.GetAsync($"http://127.0.0.1:{HealthEndpointStartUp.Port}{group.Path}"));

        Assert.True(healthCase.Expected.HttpStatus == statusCode,
            $"{caseName}: HTTP {statusCode}, expected {healthCase.Expected.HttpStatus} (body: {body})");
        if (healthCase.Expected.Body is { } expectedBody)
        {
            using var actual = JsonDocument.Parse(body);
            var mismatch = ConformanceFixtures.FindSubsetMismatch(expectedBody, actual.RootElement);
            Assert.True(mismatch == null, $"{caseName}: health body mismatch at {mismatch}");
        }
    }
}
