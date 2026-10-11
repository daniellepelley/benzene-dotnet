using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Benzene.Abstractions;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.AspNet.Core;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.Conformance.Test.Handlers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.HostedService;
using Benzene.Http.Routing;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Benzene.ResponseEvents;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.Derivation;
using Benzene.Schema.OpenApi.EventService;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Conformance.Test;

/// <summary>
/// Runs docs/specification/conformance/contract-document-cases.json (contract-document.md).
/// <para>
/// <b>Producer groups</b> run against this port's real producer: the canonical types are registered
/// the way an application registers them - handlers through <c>UseMessageHandlers</c>, events through
/// <c>AddResponseEventDeclarations</c>, sends through <c>AddSendDeclarations</c> (a
/// <see cref="MeshOutboundRegistry"/>), identity through <c>SetApplicationInfo</c> - and the document is
/// the one <c>benzene:spec</c> answers over the envelope pipeline (<c>specHttpCases</c>: over HTTP).
/// <c>namingCases</c> derive through the opt-in <see cref="ContractDocumentStartUpCheck"/>, so a
/// collision fails host start-up, before any message is handled.
/// </para>
/// <para>
/// <b>Client-generator groups</b> run through the deserializer and the generator's own
/// <see cref="TopicScope"/> and <see cref="SchemaClosure"/> (internal; exposed to this project via
/// <c>InternalsVisibleTo</c> on <c>Benzene.CodeGen.Client</c>).
/// </para>
/// </summary>
public class ContractDocumentConformanceTest
{
    public class Fixture
    {
        public Dictionary<string, JsonElement> Documents { get; set; } = new();
        public List<DerivationCase> DerivationCases { get; set; } = new();
        public List<NamingCase> NamingCases { get; set; } = new();
        public List<SpecTopicCase> SpecTopicCases { get; set; } = new();
        public List<SpecHttpCase> SpecHttpCases { get; set; } = new();
        public List<ParseCase> ParseCases { get; set; } = new();
        public List<TopicScopeCase> TopicScopeCases { get; set; } = new();
        public List<SchemaClosureCase> SchemaClosureCases { get; set; } = new();
    }

    public class Registration
    {
        public string Topic { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string? Request { get; set; }
        public string? Response { get; set; }
        public string? Message { get; set; }
    }

    public class Registrations
    {
        public List<Registration> Requests { get; set; } = new();
        public List<Registration> Events { get; set; } = new();
        public List<Registration> Sends { get; set; } = new();
    }

    public class PlacementInfo
    {
        public string Cloud { get; set; } = string.Empty;
        public string? Region { get; set; }
    }

    /// <summary>A derivation case's identity and placement (contract-document.md §1), which the document carries and neither hash covers.</summary>
    public class ServiceInfo
    {
        public string? Service { get; set; }
        public string? ServiceVersion { get; set; }
        public string? VersionScheme { get; set; }
        public PlacementInfo? Placement { get; set; }
    }

    public class DerivationCase
    {
        public string Name { get; set; } = string.Empty;
        public ServiceInfo? ServiceInfo { get; set; }
        public Registrations Registrations { get; set; } = new();
        public JsonElement Expected { get; set; }

        public override string ToString() => Name;
    }

    public class NamingExpectation
    {
        public string? Error { get; set; }
        public string? Name { get; set; }
        public string[]? ComponentNames { get; set; }
    }

    public class NamingCase
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, JsonElement> Types { get; set; } = new();
        public Dictionary<string, string>? DeclaredNames { get; set; }
        public Registrations Registrations { get; set; } = new();
        public NamingExpectation Expected { get; set; } = new();

        public override string ToString() => Name;
    }

    public class SpecTopicExpectation
    {
        public string StatusCode { get; set; } = string.Empty;
        public JsonElement? Document { get; set; }
    }

    public class SpecTopicCase
    {
        public string Name { get; set; } = string.Empty;
        public JsonElement? Request { get; set; }
        public SpecTopicExpectation Expected { get; set; } = new();

        public override string ToString() => Name;
    }

    public class SpecHttpExpectation
    {
        public int HttpStatus { get; set; }
        public JsonElement? Document { get; set; }
    }

    public class SpecHttpCase
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public SpecHttpExpectation Expected { get; set; } = new();

        public override string ToString() => Name;
    }

    public class TopicOptions
    {
        public string[]? Topics { get; set; }
    }

    public class ExpectedEntry
    {
        public string Topic { get; set; } = string.Empty;
        public bool? VersionPresent { get; set; }
        public string? Version { get; set; }
        public bool? ResponsePresent { get; set; }
    }

    public class ExpectedShape
    {
        public string? SchemaDialect { get; set; }
        public string? Service { get; set; }
        public List<ExpectedEntry>? Requests { get; set; }
        public List<ExpectedEntry>? Events { get; set; }
        public List<ExpectedEntry>? Sends { get; set; }
    }

    public class ExpectedErrorShape
    {
        public string Kind { get; set; } = string.Empty;
        public string[] UnknownTopics { get; set; } = Array.Empty<string>();
        public string[] ValidTopics { get; set; } = Array.Empty<string>();
    }

    public class ParseCase
    {
        public string Name { get; set; } = string.Empty;
        public string DocumentRef { get; set; } = string.Empty;
        public TopicOptions? Options { get; set; }
        public ExpectedShape? Expected { get; set; }
        public ExpectedErrorShape? ExpectedError { get; set; }

        public override string ToString() => Name;
    }

    public class TopicScopeCase
    {
        public string Name { get; set; } = string.Empty;
        public string DocumentRef { get; set; } = string.Empty;
        public TopicOptions Options { get; set; } = new();
        public string[] ExpectedTopics { get; set; } = Array.Empty<string>();
        public string[]? ExpectedEvents { get; set; }
        public string[]? ExpectedSends { get; set; }

        public override string ToString() => Name;
    }

    public class SchemaClosureCase
    {
        public string Name { get; set; } = string.Empty;
        public string DocumentRef { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public string[] ExpectedComponents { get; set; } = Array.Empty<string>();

        public override string ToString() => Name;
    }

    private static readonly Lazy<Fixture> FixtureData = new(() =>
        ConformanceFixtures.Load<Fixture>("contract-document-cases.json"));

    public static IEnumerable<object[]> DerivationCases() => FixtureData.Value.DerivationCases.Select(c => new object[] { c });
    public static IEnumerable<object[]> NamingCases() => FixtureData.Value.NamingCases.Select(c => new object[] { c });
    public static IEnumerable<object[]> SpecTopicCases() => FixtureData.Value.SpecTopicCases.Select(c => new object[] { c });
    public static IEnumerable<object[]> SpecHttpCases() => FixtureData.Value.SpecHttpCases.Select(c => new object[] { c });
    public static IEnumerable<object[]> ParseCases() => FixtureData.Value.ParseCases.Select(c => new object[] { c });
    public static IEnumerable<object[]> TopicScopeCases() => FixtureData.Value.TopicScopeCases.Select(c => new object[] { c });
    public static IEnumerable<object[]> SchemaClosureCases() => FixtureData.Value.SchemaClosureCases.Select(c => new object[] { c });

    // ------------------------------------------------------------------
    // The canonical registrations (conformance/README.md "Canonical payload types")
    // ------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, Type> CanonicalTypes = new Dictionary<string, Type>
    {
        ["GreetRequest"] = typeof(GreetRequest),
        ["GreetResponse"] = typeof(GreetReply),
        ["Greeted"] = typeof(Greeted),
        ["LogEntry"] = typeof(LogEntry),
        ["LookupRequest"] = typeof(LookupRequest),
        ["LookupResponse"] = typeof(LookupResponse),
        ["ShapesRequest"] = typeof(ShapesRequest),
        ["ShapesChild"] = typeof(ShapesChild),
        ["TreeNode"] = typeof(TreeNode),
    };

    private static readonly IReadOnlyDictionary<string, Type> CanonicalHandlers = new Dictionary<string, Type>
    {
        ["conformance:greet"] = typeof(GreetConformanceHandler),
        ["conformance:shapes"] = typeof(ShapesConformanceHandler),
        ["conformance:tree"] = typeof(TreeConformanceHandler),
    };

    private static Registrations CanonicalService => FixtureData.Value.DerivationCases.Single(c => c.Name == "canonical-service").Registrations;

    /// <summary>Registers <paramref name="registrations"/> the way an application does and returns the container and the envelope pipeline.</summary>
    private static (MicrosoftBenzeneServiceContainer Container, Benzene.Abstractions.Middleware.IMiddlewarePipeline<BenzeneMessageContext> Pipeline) BuildService(Registrations registrations, ServiceInfo? serviceInfo = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();
        // The identity through IApplicationInfo and the placement through the deployment-side fields -
        // how an application (and the cloud service) supplies them.
        container.SetApplicationInfo(serviceInfo?.Service ?? "conformance", serviceInfo?.ServiceVersion ?? string.Empty,
            string.Empty, serviceInfo?.VersionScheme);
        if (serviceInfo?.Placement != null)
        {
            container.SetContractDocumentDeployment(new ContractDocumentDeployment
            {
                Placement = new ContractDocumentPlacement(serviceInfo.Placement.Cloud, serviceInfo.Placement.Region)
            });
        }

        if (registrations.Events.Count > 0)
        {
            container.AddResponseEventDeclarations(registrations.Events
                .Select(e => (Benzene.Abstractions.Messages.IMessageDefinition)new ResponseEventDefinition(e.Topic, CanonicalTypes[e.Message!]))
                .ToArray());
        }

        if (registrations.Sends.Count > 0)
        {
            var sends = new MeshOutboundRegistry();
            foreach (var send in registrations.Sends)
            {
                sends.Register(send.Topic, send.Version, CanonicalTypes[send.Request!],
                    send.Response == null ? typeof(Void) : CanonicalTypes[send.Response]);
            }

            container.AddSendDeclarations(sends);
        }

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipelineBuilder.UseSpec();
        pipelineBuilder.UseMessageHandlers(registrations.Requests.Select(r => CanonicalHandlers[r.Topic]).ToArray());
        return (container, pipelineBuilder.Build());
    }

    private static async Task<IBenzeneMessageResponse> SendSpecAsync(Registrations registrations, string? body,
        ServiceInfo? serviceInfo = null)
    {
        var (container, pipeline) = BuildService(registrations, serviceInfo);
        var application = new BenzeneMessageApplication(pipeline);
        return await application.HandleAsync(new BenzeneMessageRequest
        {
            Topic = BenzeneTopic.Spec,
            Body = body,
        }, container.CreateServiceResolverFactory());
    }

    // ------------------------------------------------------------------
    // derivationCases
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(DerivationCases))]
    public async Task DerivationCase_TheServedDocumentDerivesTheExpectedEntriesAndHashes(DerivationCase testCase)
    {
        var response = await SendSpecAsync(testCase.Registrations, null, testCase.ServiceInfo);
        Assert.Equal("ok", response.StatusCode);

        var document = JsonNode.Parse(response.Body!)!.AsObject();
        var components = document["components"]?["schemas"] as JsonObject;
        var expected = JsonNode.Parse(testCase.Expected.GetRawText())!.AsObject();

        Assert.Equal(EventServiceDocument.SchemaDialectOpenApi30, document["schemaDialect"]?.GetValue<string>());
        Assert.Equal(testCase.ServiceInfo?.Service ?? "conformance", document["service"]?.GetValue<string>());

        // The identity fields expected carries (service, serviceVersion, versionScheme, placement),
        // subset-matched: the document carries them, and neither hash covers them (§1, §6.2).
        using (var actualDocument = JsonDocument.Parse(response.Body!))
        {
            foreach (var property in testCase.Expected.EnumerateObject()
                         .Where(x => x.Name is not ("requests" or "events" or "sends" or "contractHash" or "registrationHash")))
            {
                Assert.True(actualDocument.RootElement.TryGetProperty(property.Name, out var actual),
                    $"{testCase.Name}: the document has no '{property.Name}'");
                var mismatch = ConformanceFixtures.FindSubsetMismatch(property.Value, actual, "$." + property.Name);
                Assert.True(mismatch == null, $"{testCase.Name}: {mismatch}");
            }
        }

        foreach (var (member, schemaMembers) in new[]
                 {
                     ("requests", new[] { "request", "response" }),
                     ("events", new[] { "message" }),
                     ("sends", new[] { "request", "response" }),
                 })
        {
            var derivedEntries = document[member]!.AsArray().Select(x => x!.AsObject()).ToArray();
            var expectedEntries = expected[member]!.AsArray().Select(x => x!.AsObject()).ToArray();
            Assert.True(expectedEntries.Length == derivedEntries.Length,
                $"{testCase.Name}: expected {expectedEntries.Length} {member} entries but derived {derivedEntries.Length}");

            foreach (var expectedEntry in expectedEntries)
            {
                var topic = expectedEntry["topic"]!.GetValue<string>();
                var version = expectedEntry["version"]?.GetValue<string>();
                var derived = derivedEntries.SingleOrDefault(x =>
                    x["topic"]!.GetValue<string>() == topic && x["version"]?.GetValue<string>() == version);
                Assert.True(derived != null, $"{testCase.Name}: no derived {member} entry for '{topic}' version '{version}'");

                foreach (var schemaMember in schemaMembers)
                {
                    var expectedSchema = expectedEntry[schemaMember];
                    var derivedSchema = derived![schemaMember];
                    if (expectedSchema == null)
                    {
                        Assert.True(derivedSchema == null,
                            $"{testCase.Name}: {member} '{topic}'.{schemaMember} must be absent but was {derivedSchema?.ToJsonString()}");
                        continue;
                    }

                    Assert.True(derivedSchema != null, $"{testCase.Name}: {member} '{topic}'.{schemaMember} is missing");
                    var normalizedExpected = ContractHash.NormalizeSchema(expectedSchema, null);
                    var normalizedDerived = ContractHash.NormalizeSchema(derivedSchema, components);
                    Assert.True(JsonNode.DeepEquals(normalizedExpected, normalizedDerived),
                        $"{testCase.Name}: {member} '{topic}'.{schemaMember}\n expected {normalizedExpected!.ToJsonString()}\n derived  {normalizedDerived!.ToJsonString()}");
                }
            }
        }

        // §4.1 rule 3: valid keys; §1: every $ref resolves into components.schemas.
        foreach (var (key, _) in components ?? new JsonObject())
        {
            Assert.True(ComponentNames.IsValid(key), $"{testCase.Name}: component key '{key}' is not valid");
        }

        foreach (var reference in References(document))
        {
            Assert.StartsWith("#/components/schemas/", reference);
            Assert.True(components != null && components.ContainsKey(reference.Substring("#/components/schemas/".Length)),
                $"{testCase.Name}: $ref '{reference}' does not resolve");
        }

        Assert.Equal(expected["contractHash"]!.GetValue<string>(), document["contractHash"]?.GetValue<string>());
        Assert.Equal(expected["registrationHash"]!.GetValue<string>(), document["registrationHash"]?.GetValue<string>());
    }

    private static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref" && value is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
                    {
                        yield return reference;
                    }

                    foreach (var nested in References(value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonArray array:
                foreach (var nested in array.SelectMany(References))
                {
                    yield return nested;
                }

                break;
        }
    }

    // ------------------------------------------------------------------
    // namingCases
    // ------------------------------------------------------------------

    private static Type NamingType(string qualifiedName, bool declared) => (qualifiedName, declared) switch
    {
        ("orders.Item", false) => typeof(Naming.Orders.Item),
        ("payments.Item", false) => typeof(Naming.Payments.Item),
        ("orders.Item", true) => typeof(Naming.Declared.Orders.Item),
        ("payments.Item", true) => typeof(Naming.Declared.Payments.Item),
        _ => throw new InvalidOperationException($"No .NET type is mapped to the fixture's '{qualifiedName}'."),
    };

    [Theory]
    [MemberData(nameof(NamingCases))]
    public void NamingCase_FailsAtStartUpOrUsesTheDeclaredNames(NamingCase testCase)
    {
        var declared = testCase.DeclaredNames is { Count: > 0 };
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();
        container.SetApplicationInfo("conformance", string.Empty, string.Empty);
        container.AddContractDocumentStartUpCheck();
        foreach (var request in testCase.Registrations.Requests)
        {
            var definition = MessageHandlerDefinition.CreateInstance(request.Topic, string.Empty,
                NamingType(request.Request!, declared), typeof(Void), typeof(ShapesConformanceHandler));
            container.AddSingleton<IMessageHandlerDefinition>(_ => definition);
        }

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipelineBuilder.UseSpec();
        pipelineBuilder.UseMessageHandlers(Array.Empty<Type>());
        pipelineBuilder.Build();
        var factory = container.CreateServiceResolverFactory();

        if (testCase.Expected.Error != null)
        {
            Assert.Equal(ComponentNameCollisionException.Kind, testCase.Expected.Error);
            var failure = Assert.ThrowsAny<Exception>(() => factory.RunStartUpChecks());
            var collision = Unwrap<ComponentNameCollisionException>(failure);
            Assert.True(collision != null, $"{testCase.Name}: start-up failed, but not with a component-name collision: {failure}");
            Assert.Equal(testCase.Expected.Name, collision!.Name);
            // The failure names both types, the topics that use them and the remedy.
            foreach (var qualifiedName in testCase.Types.Keys)
            {
                Assert.Contains(NamingType(qualifiedName, false).FullName!, collision.Message);
            }

            foreach (var request in testCase.Registrations.Requests)
            {
                Assert.Contains(request.Topic, collision.Message);
            }

            Assert.Contains("SchemaName", collision.Message);
            return;
        }

        factory.RunStartUpChecks();

        using var resolver = factory.CreateScope();
        var document = JsonNode.Parse(new SpecBuilder().CreateSpec(resolver, new SpecRequest()))!.AsObject();
        var components = document["components"]!["schemas"]!.AsObject();
        foreach (var name in testCase.Expected.ComponentNames ?? Array.Empty<string>())
        {
            Assert.True(components.ContainsKey(name), $"{testCase.Name}: no component '{name}' in {components.ToJsonString()}");
        }

        // And each declared name carries its type's shape.
        foreach (var (qualifiedName, declaredName) in testCase.DeclaredNames ?? new Dictionary<string, string>())
        {
            var expectedShape = ContractHash.NormalizeSchema(JsonNode.Parse(testCase.Types[qualifiedName].GetRawText()), null);
            var derivedShape = ContractHash.NormalizeSchema(components[declaredName], components);
            Assert.True(JsonNode.DeepEquals(expectedShape, derivedShape),
                $"{testCase.Name}: '{declaredName}' expected {expectedShape!.ToJsonString()} but derived {derivedShape!.ToJsonString()}");
        }
    }

    private static T? Unwrap<T>(Exception? exception) where T : Exception
    {
        while (exception != null)
        {
            if (exception is T match)
            {
                return match;
            }

            if (exception is AggregateException aggregate)
            {
                return aggregate.InnerExceptions.Select(Unwrap<T>).FirstOrDefault(x => x != null);
            }

            exception = exception.InnerException;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // specTopicCases
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SpecTopicCases))]
    public async Task SpecTopicCase_AnswersWithTheStatusAndDocument(SpecTopicCase testCase)
    {
        // null means an absent body; anything else is sent as the envelope body.
        var body = testCase.Request is { ValueKind: not JsonValueKind.Null } request ? request.GetRawText() : null;

        var response = await SendSpecAsync(CanonicalService, body);

        Assert.Equal(testCase.Expected.StatusCode, response.StatusCode);
        if (testCase.Expected.Document is { } expectedDocument)
        {
            using var actual = JsonDocument.Parse(response.Body!);
            var mismatch = ConformanceFixtures.FindSubsetMismatch(expectedDocument, actual.RootElement);
            Assert.True(mismatch == null, $"{testCase.Name}: document mismatch at {mismatch}");
        }
    }

    // ------------------------------------------------------------------
    // specHttpCases - over this port's reference HTTP binding (AspNet, a real socket): GET /benzene/spec
    // is an ordinary HTTP mapping onto benzene:spec, so it runs the same pipeline (and authentication)
    // as every topic, and the request enricher copies its query parameters onto the request.
    // ------------------------------------------------------------------

    public class SpecHttpStartUp : BenzeneStartUp
    {
        public static int Port;

        public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

        public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
            => services.UsingBenzene(x => x
                .AddBenzene()
                .SetApplicationInfo("conformance", string.Empty, string.Empty)
                .AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("GET", "/benzene/spec", BenzeneTopic.Spec)));

        public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
            .UseWorker(worker => worker.UseAspNet(
                asp => asp.UseSpec().UseMessageHandlers(typeof(GreetConformanceHandler)),
                options => options.Urls = $"http://127.0.0.1:{Port}"));
    }

    [Theory]
    [MemberData(nameof(SpecHttpCases))]
    public async Task SpecHttpCase_GetBenzeneSpecAnswersThroughThePipeline(SpecHttpCase testCase)
    {
        SpecHttpStartUp.Port = GetFreePort();
        var host = new HostBuilder().UseBenzene<SpecHttpStartUp>().Build();
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        foreach (var service in hostedServices)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"http://127.0.0.1:{SpecHttpStartUp.Port}{testCase.Path}");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(testCase.Expected.HttpStatus, (int)response.StatusCode);
            if (testCase.Expected.Document is { } expectedDocument)
            {
                using var actual = JsonDocument.Parse(body);
                var mismatch = ConformanceFixtures.FindSubsetMismatch(expectedDocument, actual.RootElement);
                Assert.True(mismatch == null, $"{testCase.Name}: document mismatch at {mismatch}");
            }
        }
        finally
        {
            foreach (var service in hostedServices)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ------------------------------------------------------------------
    // Client-generator groups
    // ------------------------------------------------------------------

    private static EventServiceDocument DeserializeDocument(string documentRef)
    {
        var json = FixtureData.Value.Documents[documentRef].GetRawText();
        return new EventServiceDocumentDeserializer().Deserialize(json);
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void ParseCase_MatchesTheExpectedShapeOrFailsLoud(ParseCase testCase)
    {
        if (testCase.ExpectedError?.Kind == UnknownSchemaDialectException.Kind)
        {
            Assert.Throws<UnknownSchemaDialectException>(() => DeserializeDocument(testCase.DocumentRef));
            return;
        }

        var document = DeserializeDocument(testCase.DocumentRef);

        if (testCase.ExpectedError != null)
        {
            Assert.Equal("unknown-topics", testCase.ExpectedError.Kind);
            var options = new ClientSdkOptions { Namespace = "x", Topics = testCase.Options?.Topics };

            var exception = Assert.Throws<ArgumentException>(() => TopicScope.Apply(document, options));

            foreach (var unknownTopic in testCase.ExpectedError.UnknownTopics)
            {
                Assert.Contains(unknownTopic, exception.Message);
            }

            foreach (var validTopic in testCase.ExpectedError.ValidTopics)
            {
                Assert.Contains(validTopic, exception.Message);
            }

            return;
        }

        var expected = testCase.Expected!;
        if (expected.SchemaDialect != null)
        {
            Assert.Equal(expected.SchemaDialect, document.SchemaDialect);
        }

        if (expected.Service != null)
        {
            Assert.Equal(expected.Service, document.Service);
        }

        if (expected.Requests != null)
        {
            // A benzene: entry is ignored, not listed (§1).
            Assert.DoesNotContain(document.Requests, r => BenzeneTopic.IsReserved(r.Topic));
            foreach (var row in expected.Requests)
            {
                var actual = MatchRow(document.Requests.Select(r => (r.Topic, r.Version, (object?)r.Response)), row, testCase.Name);
                AssertResponse(row, actual.Response, testCase.Name);
            }
        }

        if (expected.Events != null)
        {
            Assert.Equal(expected.Events.Count, document.Events.Length);
            foreach (var row in expected.Events)
            {
                MatchRow(document.Events.Select(e => (e.Topic, e.Version, (object?)null)), row, testCase.Name);
            }
        }

        if (expected.Sends != null)
        {
            Assert.Equal(expected.Sends.Count, document.Sends.Length);
            foreach (var row in expected.Sends)
            {
                var actual = MatchRow(document.Sends.Select(s => (s.Topic, s.Version, (object?)s.Response)), row, testCase.Name);
                AssertResponse(row, actual.Response, testCase.Name);
            }
        }
    }

    // Rows are keyed by (topic, version): a row asserting a version matches the entry with it.
    private static (string Topic, string Version, object? Response) MatchRow(
        IEnumerable<(string Topic, string Version, object? Response)> entries, ExpectedEntry row, string caseName)
    {
        var candidates = entries.Where(e => e.Topic == row.Topic).ToArray();
        if (row.Version != null)
        {
            candidates = candidates.Where(e => e.Version == row.Version).ToArray();
        }
        else if (row.VersionPresent == false)
        {
            candidates = candidates.Where(e => string.IsNullOrEmpty(e.Version)).ToArray();
        }

        Assert.True(candidates.Length == 1, $"{caseName}: expected one entry for {row.Topic}/{row.Version} but found {candidates.Length}");
        var actual = candidates[0];
        if (row.VersionPresent.HasValue)
        {
            Assert.Equal(row.VersionPresent.Value, !string.IsNullOrEmpty(actual.Version));
        }

        return actual;
    }

    private static void AssertResponse(ExpectedEntry row, object? response, string caseName)
    {
        if (row.ResponsePresent.HasValue)
        {
            Assert.True(row.ResponsePresent.Value == (response != null),
                $"{caseName}: {row.Topic} response expected present={row.ResponsePresent.Value}");
        }
    }

    [Theory]
    [MemberData(nameof(TopicScopeCases))]
    public void TopicScopeCase_ProjectsToExactlyTheExpectedTopics(TopicScopeCase testCase)
    {
        var document = DeserializeDocument(testCase.DocumentRef);
        var options = new ClientSdkOptions
        {
            Namespace = "x",
            Topics = testCase.Options.Topics,
        };

        var scoped = TopicScope.Apply(document, options);

        static string[] Sorted(IEnumerable<string> topics) => topics.OrderBy(t => t, StringComparer.Ordinal).ToArray();

        // Compared as sets (README.md): a topic served at two versions is two entries, one topic.
        Assert.Equal(Sorted(testCase.ExpectedTopics.Distinct()), Sorted(scoped.Requests.Select(r => r.Topic).Distinct()));

        if (testCase.ExpectedEvents != null)
        {
            Assert.Equal(Sorted(testCase.ExpectedEvents), Sorted(scoped.Events.Select(e => e.Topic)));
        }

        if (testCase.ExpectedSends != null)
        {
            Assert.Equal(Sorted(testCase.ExpectedSends), Sorted(scoped.Sends.Select(s => s.Topic)));
        }
    }

    [Theory]
    [MemberData(nameof(SchemaClosureCases))]
    public void SchemaClosureCase_ReachesExactlyTheExpectedComponents(SchemaClosureCase testCase)
    {
        var document = DeserializeDocument(testCase.DocumentRef);
        var request = document.Requests.Single(r => r.Topic == testCase.Topic);

        var reached = SchemaClosure.ReachableNames(document.Components.Schemas, request.Request, request.Response);

        var actual = reached.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var expected = testCase.ExpectedComponents.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);

        // And the topic-scoped projection (§5.3) carries exactly that set.
        var projected = TopicScope.ProjectTopic(document, request);
        Assert.Equal(expected, projected.Components.Schemas.Keys.OrderBy(t => t, StringComparer.Ordinal).ToArray());
        Assert.Empty(projected.Events);
        Assert.Empty(projected.Sends);
    }
}
