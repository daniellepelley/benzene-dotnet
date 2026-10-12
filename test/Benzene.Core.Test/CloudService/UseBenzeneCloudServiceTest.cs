using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Aws.Lambda.Core;
using Benzene.Clients;
using Benzene.Clients.TraceContext;
using Benzene.CloudService;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Benzene.Testing;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Xunit;

namespace Benzene.Test.CloudService;

public class UseBenzeneCloudServiceTest
{
    private static AwsLambdaBenzeneTestHost CreateHost(Action<ICloudServiceBuilder>? configure = null)
    {
        return new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services.ConfigureServiceCollection())
            .Configure(app => app
                .UseApiGateway(apiGateway => apiGateway
                    .UseBenzeneCloudService("orders", configure)
                )
            )
            .BuildHost();
    }

    private static object CreateEnvelope(string topic, string body = Defaults.Message)
    {
        return new
        {
            topic,
            headers = new Dictionary<string, string>(),
            body
        };
    }

    [Fact]
    public async Task EnvelopeEndpoint_IsAtTheDefaultStandardPath()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope(Defaults.Topic)));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"statusCode\":\"ok\"", response.Body);
    }

    [Fact]
    public async Task HealthEndpoint_IsAtTheDefaultStandardPath()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("GET", CloudServicePaths.Health, new { }));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("isHealthy", response.Body);
    }

    [Fact]
    public async Task SpecEndpoint_IsAtTheDefaultStandardPath()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("GET", CloudServicePaths.Spec, new { }));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task SpecEndpoint_ServesTheContractDocument_IdentityFromTheCloudService_SendsFromWithSends()
    {
        var host = CreateHost(x => x
            .WithServiceVersion("1.4.2")
            .WithSends(new MeshOutboundRegistry()
                .Register<ExampleRequestPayload>("payments:take")
                .Register<ExampleRequestPayload, ExampleResponsePayload>("inventory:reserve", "v2")));

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("GET", CloudServicePaths.Spec, new { }));

        Assert.Equal(200, response.StatusCode);
        var document = System.Text.Json.Nodes.JsonNode.Parse(response.Body)!.AsObject();
        Assert.Equal("openapi-3.0", document["schemaDialect"]!.GetValue<string>());
        Assert.Equal("orders", document["service"]!.GetValue<string>());
        Assert.Equal("1.4.2", document["serviceVersion"]!.GetValue<string>());
        Assert.StartsWith("sha256:", document["contractHash"]!.GetValue<string>());
        Assert.StartsWith("sha256:", document["registrationHash"]!.GetValue<string>());
        var sends = document["sends"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        var take = Assert.Single(sends, x => x["topic"]!.GetValue<string>() == "payments:take");
        Assert.False(take.ContainsKey("response")); // one-way
        var reserve = Assert.Single(sends, x => x["topic"]!.GetValue<string>() == "inventory:reserve");
        Assert.Equal("v2", reserve["version"]!.GetValue<string>());
        Assert.True(reserve.ContainsKey("response"));
        // No benzene: topic is ever listed (contract-document.md §1).
        Assert.DoesNotContain(document["requests"]!.AsArray(), x => x!["topic"]!.GetValue<string>().StartsWith("benzene:"));
    }

    [Fact]
    public async Task HealthCheckTopic_IsInterceptedOnTheEnvelopePipeline()
    {
        var host = CreateHost();

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope("benzene:healthcheck", "{}")));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("isHealthy", response.Body);
    }

    private static System.Text.Json.Nodes.JsonObject EnvelopeBody(string response)
    {
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(response)!.AsObject();
        Assert.Equal("ok", envelope["statusCode"]!.GetValue<string>());
        return System.Text.Json.Nodes.JsonNode.Parse(envelope["body"]!.GetValue<string>())!.AsObject();
    }

    [Fact]
    public async Task SpecTopic_ServesTheContractDocument_WithTheProfileSelfAssessmentAndDeploymentFields()
    {
        var host = CreateHost(x => x.WithPlacement("aws", "eu-west-1"));

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope("benzene:spec", "{}")));

        Assert.Equal(200, response.StatusCode);
        var document = EnvelopeBody(response.Body);
        Assert.Equal("orders", document["service"]!.GetValue<string>());
        Assert.Equal("dotnet", document["runtime"]!.GetValue<string>());
        Assert.Equal("aws", document["placement"]!["cloud"]!.GetValue<string>());
        Assert.Equal("eu-west-1", document["placement"]!["region"]!.GetValue<string>());
        Assert.Equal(CloudServiceProfileReport.ProfileName, document["profile"]!["name"]!.GetValue<string>());
        // No collector configured: the profile (R1–R5, R7) needs none, so the service is a full Cloud
        // Service and profile.missing is omitted. The mesh feeds it lacks are the meshed-service claim's
        // M1/M2 (mesh.md §7.1), which never appear in profile.missing.
        Assert.False(document["profile"]!.AsObject().ContainsKey("missing"));
    }

    [Fact]
    public async Task SpecTopic_RelocatedPath_ProfileMissingListsR7Only()
    {
        var host = CreateHost(cloud => cloud.WithoutMesh().WithHealthPath("/health"));

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope("benzene:spec", "{}")));

        Assert.Equal(200, response.StatusCode);
        var missing = EnvelopeBody(response.Body)["profile"]!["missing"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        // WithoutMesh withdraws M1 and M2 - but those are the meshed-service claim, never profile ids.
        Assert.Equal(new[] { "R7" }, missing);
    }

    [Fact]
    public async Task WithCollector_TheProfileSelfAssessmentIsFullyConformant()
    {
        // Port 9 is the discard service - registration will fail and be retried, which is exactly
        // the spec's degradation behavior: an unreachable collector never affects the service.
        var host = CreateHost(cloud => cloud.WithCollector("http://localhost:9/benzene/invoke"));

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope("benzene:spec", "{}")));

        Assert.Equal(200, response.StatusCode);
        var profile = EnvelopeBody(response.Body)["profile"]!.AsObject();
        Assert.Equal(CloudServiceProfileReport.ProfileName, profile["name"]!.GetValue<string>());
        Assert.False(profile.ContainsKey("missing"));
    }

    [Fact]
    public async Task TheRetiredBenzeneMeshTopic_IsNotServed()
    {
        // spec-0.4.0 retired the ServiceDescriptor topic: the self-description is benzene:spec.
        var host = CreateHost(cloud => cloud.WithCollector("http://localhost:9/benzene/invoke"));

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope("benzene:mesh", "{}")));

        // Processed (200), and not-found inside: nothing answers the topic.
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"statusCode\":\"not-found\"", response.Body);
    }

    [Fact]
    public async Task WithoutMesh_TheSpecTopicStillAnswers()
    {
        var host = CreateHost(cloud => cloud.WithoutMesh());

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope("benzene:spec", "{}")));

        Assert.Equal("orders", EnvelopeBody(response.Body)["service"]!.GetValue<string>());
    }

    [Fact]
    public async Task RelocatedSurface_StillWorks_AndIsFlaggedInTheProfileSelfAssessment()
    {
        var host = CreateHost(cloud => cloud.WithInvokePath("/custom-invoke"));

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", "/custom-invoke", CreateEnvelope("benzene:spec", "{}")));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("R7", EnvelopeBody(response.Body)["profile"]!["missing"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task DomainTopics_AreRoutedOnBothSurfaces()
    {
        var host = CreateHost();

        var viaEnvelope = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope(Defaults.Topic)));
        var viaHttp = await host.SendApiGatewayAsync(
            HttpBuilder.Create("GET", Defaults.Path, Defaults.MessageAsObject));

        Assert.Equal(200, viaEnvelope.StatusCode);
        Assert.Equal(200, viaHttp.StatusCode);
    }

    /// <summary>A loopback listener standing in for the collector: records every envelope it receives
    /// and answers each with an accepted envelope.</summary>
    private sealed class FakeCollector : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<System.Text.Json.Nodes.JsonObject> _envelopes = new();

        public FakeCollector()
        {
            Port = GetFreeTcpPort();
            _listener.Prefixes.Add($"http://localhost:{Port}/benzene/invoke/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch
                    {
                        return;
                    }
                    using var reader = new StreamReader(context.Request.InputStream);
                    _envelopes.Enqueue(System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject());
                    var answer = System.Text.Encoding.UTF8.GetBytes("{\"statusCode\":\"ok\",\"isSuccessful\":true,\"headers\":{},\"body\":\"{\\\"accepted\\\":1}\"}");
                    context.Response.StatusCode = 200;
                    await context.Response.OutputStream.WriteAsync(answer);
                    context.Response.OutputStream.Close();
                }
            });
        }

        public int Port { get; }

        public string Url => $"http://localhost:{Port}/benzene/invoke";

        /// <summary>The bodies received on <paramref name="topic"/>, in order, parsed.</summary>
        public System.Text.Json.Nodes.JsonObject[] Bodies(string topic) => _envelopes
            .Where(x => x["topic"]!.GetValue<string>() == topic)
            .Select(x => System.Text.Json.Nodes.JsonNode.Parse(x["body"]!.GetValue<string>())!.AsObject())
            .ToArray();

        public async Task<System.Text.Json.Nodes.JsonObject[]> WaitForAsync(string topic, Func<System.Text.Json.Nodes.JsonObject[], bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var bodies = Bodies(topic);
                if (done(bodies))
                {
                    return bodies;
                }
                await Task.Delay(50);
            }
            return Bodies(topic);
        }

        public void Dispose() => _listener.Close();
    }

    [Fact]
    public async Task WithHandlers_RegistersAReducedDocumentAtWireUp_BeforeAnyRequest()
    {
        // WithHandlers means a document is derivable at wire-up (not on first invocation), so
        // registration reaches the collector even though this test never sends the service a request.
        // Its events and sends are declared in the container, which wire-up cannot see: they are named
        // degraded, and empty, rather than asserted as none (contract-document.md §1).
        using var collector = new FakeCollector();

        CreateHost(cloud => cloud
            .WithHandlers(typeof(ExampleMessageHandler))
            .WithCollector(collector.Url));

        var registrations = await collector.WaitForAsync("benzene:mesh:register", x => x.Length > 0);
        var document = Assert.Single(registrations);
        Assert.Equal("orders", document["service"]!.GetValue<string>());
        Assert.Equal("openapi-3.0", document["schemaDialect"]!.GetValue<string>());
        Assert.NotEmpty(document["requests"]!.AsArray());
        Assert.Equal(new[] { "events", "sends" }, document["degraded"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.False(document.ContainsKey("topics")); // not the retired ServiceDescriptor
        Assert.False(document.ContainsKey("instanceId"));
    }

    [Fact]
    public async Task TheRegisterBody_IsTheDocumentBenzeneSpecServes_AndHeartbeatsCarryItsRegistrationHash()
    {
        // mesh.md §2: the register body is the same document benzene:spec serves. The wire-up path
        // registers a reduced one first; the first invocation re-registers the complete document.
        using var collector = new FakeCollector();
        var host = CreateHost(cloud => cloud
            .WithHandlers(typeof(ExampleMessageHandler))
            .WithServiceVersion("1.4.2", "semver")
            .WithSends(new MeshOutboundRegistry().Register<ExampleRequestPayload>("payments:take"))
            .WithCollector(collector.Url));

        var spec = await host.SendApiGatewayAsync(HttpBuilder.Create("GET", CloudServicePaths.Spec, new { }));
        var served = System.Text.Json.Nodes.JsonNode.Parse(spec.Body)!.AsObject();

        var registrations = await collector.WaitForAsync("benzene:mesh:register",
            x => x.Any(d => !d.ContainsKey("degraded")));
        var complete = registrations.Last();
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(served, complete),
            $"registered {complete.ToJsonString()}\n served {served.ToJsonString()}");
        Assert.Equal("1.4.2", complete["serviceVersion"]!.GetValue<string>());
        Assert.Equal("semver", complete["versionScheme"]!.GetValue<string>());
        Assert.Contains(complete["sends"]!.AsArray(), x => x!["topic"]!.GetValue<string>() == "payments:take");

        var hash = complete["registrationHash"]!.GetValue<string>();
        var heartbeats = await collector.WaitForAsync("benzene:mesh:heartbeat",
            x => x.Any(h => h["registrationHash"]?.GetValue<string>() == hash));
        var heartbeat = heartbeats.Last(h => h["registrationHash"]?.GetValue<string>() == hash);
        Assert.Equal("orders", heartbeat["service"]!.GetValue<string>());
        Assert.Equal("1.4.2", heartbeat["serviceVersion"]!.GetValue<string>());
        Assert.False(heartbeat.ContainsKey("descriptorHash"));
    }

    [Fact]
    public async Task WithMiddleware_RunsInsideTheEnvelopePipeline_BeforeTheRouter()
    {
        var host = CreateHost(cloud => cloud.WithMiddleware(envelope => envelope.Use(
            _ => new FuncWrapperMiddleware<BenzeneMessageContext>("Test", (context, next) =>
            {
                if (context.BenzeneMessageRequest.Topic != "custom-intercept")
                {
                    return next();
                }
                context.BenzeneMessageResponse.StatusCode = "ok";
                context.BenzeneMessageResponse.Body = "{\"intercepted\":true}";
                return Task.CompletedTask;
            }))));

        var response = await host.SendApiGatewayAsync(
            HttpBuilder.Create("POST", CloudServicePaths.Invoke, CreateEnvelope("custom-intercept")));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("intercepted", response.Body);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void ProfileReport_EvaluatesTheWiringHonestly()
    {
        var report = BuildReport(cloud => cloud.WithCollector("http://collector/benzene/invoke"));
        Assert.True(report.IsConformant);
        Assert.Empty(report.Missing);
        Assert.True(report.IsMeshed);
        Assert.Empty(report.MeshMissing);

        // The profile is R1–R5 and R7, in order; the meshed-service claim is M1 and M2, apart.
        Assert.Equal(new[] { "R1", "R2", "R3", "R4", "R5", "R7" }, report.Requirements.Select(x => x.Id));
        Assert.Equal(new[] { "M1", "M2" }, report.MeshRequirements.Select(x => x.Id));

        // No collector: still a Cloud Service (the profile needs none), but not meshed.
        var noCollector = BuildReport(null);
        Assert.True(noCollector.IsConformant);
        Assert.Empty(noCollector.Missing);
        Assert.False(noCollector.IsMeshed);
        // #167: mesh is on by default but UseMeshTrace is only wired when a collector/exporter is
        // also configured - M2 (formerly R8) must be missing here too, not just M1.
        Assert.Equal(new[] { "M1", "M2" }, noCollector.MeshMissing);

        var withoutMesh = BuildReport(cloud => cloud.WithoutMesh());
        Assert.True(withoutMesh.IsConformant);
        Assert.Equal(new[] { "M1", "M2" }, withoutMesh.MeshMissing);

        var relocated = BuildReport(cloud => cloud
            .WithCollector("http://collector/benzene/invoke")
            .WithSpecPath("/spec"));
        Assert.Equal(new[] { "R7" }, relocated.Missing);
        Assert.Empty(relocated.MeshMissing);
        // A meshed service must be a Cloud Service first (mesh.md §7.1).
        Assert.False(relocated.IsMeshed);
    }

    [Fact]
    public void ProfileReport_MeshIdsNeverAppearInProfileMissing()
    {
        foreach (var report in new[]
                 {
                     BuildReport(null),
                     BuildReport(cloud => cloud.WithoutMesh()),
                     BuildReport(cloud => cloud.WithoutMesh().WithInvokePath("/invoke"))
                 })
        {
            Assert.All(report.Missing, id => Assert.Matches("^R[1-57]$", id));
            Assert.All(report.ToProfile().Missing, id => Assert.Matches("^R[1-57]$", id));
        }
    }

    [Fact]
    public void ProfileReport_MeshEnabledWithNoCollectorOrExporter_M2IsNotSatisfied()
    {
        // #167: the default wiring (mesh on, no collector/exporter configured) used to report trace
        // propagation (then R8, now M2) satisfied just because MeshEnabled was true - but Extensions.cs
        // only wires UseMeshTrace when a traceExporter/CollectorEnvelopeUrl is also configured, so
        // MeshSpan.Current is genuinely null under this exact wiring. M2 must say so.
        var report = BuildReport(null);

        var m2 = report.MeshRequirements.Single(x => x.Id == "M2");
        Assert.False(m2.Satisfied);
        Assert.Contains(m2.Id, report.MeshMissing);
        Assert.DoesNotContain(m2.Id, report.Missing);
    }

    [Fact]
    public void ProfileReport_MeshEnabledWithAnExplicitTraceExporter_M2IsSatisfied_EvenWithNoCollector()
    {
        // The other half of the same condition Extensions.cs wires UseMeshTrace with:
        // WithTraceExporter alone (no collector URL) is enough to actually wire trace propagation,
        // so M2 must be satisfied here - it must track the resolved exporter, not CollectorEnvelopeUrl.
        // M1 still needs the collector (registration and heartbeats have no destination).
        var report = BuildReport(cloud => cloud.WithTraceExporter(new NoOpMeshTraceExporter()));

        var m2 = report.MeshRequirements.Single(x => x.Id == "M2");
        Assert.True(m2.Satisfied);
        Assert.Equal(new[] { "M1" }, report.MeshMissing);
        // No outbound routes were registered: nothing to forward, and the note says how routes would be seen.
        Assert.Equal(CloudServiceProfileReport.NoOutboundRoutes, m2.Note);
    }

    // mesh.md §3 / §7.1 M2: the forwarded traceparent must carry the mesh span. The self-check reflects
    // whether the outbound routing forwards it.

    [Fact]
    public void ProfileReport_OutboundRoutesWithoutMeshForwarding_M2IsNotSatisfied()
    {
        var report = BuildReport(cloud => cloud.WithCollector("http://collector/benzene/invoke"),
            services => services.UsingBenzene(x => x
                // The route is a stand-in (no transport); only the wiring is under test.
                .AddBenzeneStartUpChecks(Benzene.Abstractions.StartUpChecks.BenzeneStartUpCheckMode.Disabled)
                .AddOutboundRouting(routing => routing
                .Route("payments:capture", route => route.UseW3CTraceContext().OnRequest(_ => { })))));

        var m2 = report.MeshRequirements.Single(x => x.Id == "M2");
        Assert.False(m2.Satisfied);
        Assert.Equal(CloudServiceProfileReport.OutboundForwardingMissing, m2.Note);
        Assert.Equal(new[] { "M2" }, report.MeshMissing);
        Assert.False(report.IsMeshed);
    }

    [Fact]
    public void ProfileReport_OutboundRoutingForwardsTheMeshSpan_M2IsSatisfied()
    {
        var report = BuildReport(cloud => cloud.WithCollector("http://collector/benzene/invoke"),
            services => services.UsingBenzene(x => x
                // The route is a stand-in (no transport); only the wiring is under test.
                .AddBenzeneStartUpChecks(Benzene.Abstractions.StartUpChecks.BenzeneStartUpCheckMode.Disabled)
                .AddOutboundRouting(routing => routing
                .UseMeshTraceContext()
                .Route("payments:capture", route => route.OnRequest(_ => { })))));

        var m2 = report.MeshRequirements.Single(x => x.Id == "M2");
        Assert.True(m2.Satisfied);
        Assert.Equal(CloudServiceProfileReport.OutboundForwardingWired, m2.Note);
        Assert.Contains("published events", m2.Note);
        Assert.True(report.IsMeshed);
    }

    private sealed class NoOpMeshTraceExporter : IMeshTraceExporter
    {
        public void Export(MeshTraceEvent traceEvent) { }
    }

    private static CloudServiceProfileReport BuildReport(Action<ICloudServiceBuilder>? configure,
        Action<global::Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureServices = null)
    {
        CloudServiceProfileReport? report = null;
        new InlineAwsLambdaStartUp()
            .ConfigureServices(services =>
            {
                services.ConfigureServiceCollection();
                configureServices?.Invoke(services);
            })
            .Configure(app => app
                .UseApiGateway(apiGateway => apiGateway
                    .UseBenzeneCloudService("orders", cloud =>
                    {
                        configure?.Invoke(cloud);
                        cloud.WithProfileReport(x => report = x);
                    })
                )
            )
            .BuildHost();
        return report!;
    }
}
