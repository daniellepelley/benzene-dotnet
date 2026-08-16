using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Contracts;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Cross-version compatibility: "does this version break a consumer still on the previous one?"
/// <para>
/// Distinct from <c>MeshTopicEntry.Changes</c>, which is run-over-run. Both schemas live in the same
/// catalogue, in the same run, so this needs no history — which is why it can ship on its own.
/// </para>
/// <para>
/// The honesty rules are tested as hard as the classifications, because the failure that matters here
/// is not a missed change, it is a verdict the product did not earn. A single-version topic must read
/// <c>notCompared</c>, never <c>compatible</c>.
/// </para>
/// </summary>
public class MeshAggregatorCompatibilityTest : IDisposable
{
    private const string SpecUrl = "https://orders-api.example/spec?type=benzene";
    private const string HealthUrl = "https://orders-api.example/healthcheck";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _rootDirectory =
        Path.Combine(Path.GetTempPath(), "benzene-mesh-compat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SingleVersionTopic_IsNotCompared_NeverCompatible()
    {
        // The load-bearing honesty case. An empty change set from a comparison that never ran must
        // not render as an all-clear — that is the defect that makes a reader stop looking.
        var catalog = await Aggregate(
            """{"requests":[{"topic":"order:create","request":{"$ref":"#/components/schemas/A"}}],"components":{"schemas":{"A":{"type":"object","properties":{"id":{"type":"string"}}}}}}""");

        var entry = Assert.Single(catalog.Topics, t => t.Topic == "order:create");

        Assert.NotNull(entry.Compatibility);
        Assert.Equal(MeshCompatibilityVerdict.NotCompared, entry.Compatibility!.Overall);
        Assert.Equal(MeshNotComparedReason.OnlyOneVersion, entry.Compatibility.NotComparedReason);
        Assert.Null(entry.Compatibility.BaselineVersion);
        Assert.Empty(entry.Compatibility.Changes);
    }

    [Fact]
    public async Task RequiredPropertyAddedToRequest_IsBreaking_AndNamesTheField()
    {
        var catalog = await Aggregate(Versioned(
            v1Request: """{"type":"object","required":["customerId"],"properties":{"customerId":{"type":"string"}}}""",
            v2Request: """{"type":"object","required":["customerId","channel"],"properties":{"customerId":{"type":"string"},"channel":{"type":"string"}}}"""));

        var v2 = Assert.Single(catalog.Topics, t => t.Topic == "order:create" && t.Version == "2");

        Assert.Equal(MeshCompatibilityVerdict.Breaking, v2.Compatibility!.Overall);
        Assert.Equal("1", v2.Compatibility.BaselineVersion);

        var change = Assert.Single(v2.Compatibility.Changes);
        Assert.Equal("requiredPropertyAdded", change.Kind);
        Assert.Equal("request", change.Direction);
        Assert.Equal("order:create.request.channel", change.Path);
        Assert.Equal(MeshCompatibilityVerdict.Breaking, change.Compatibility);
    }

    [Fact]
    public async Task PropertyRemovedFromRequest_IsOnlyAWarning()
    {
        // The asymmetry the whole taxonomy exists for: the service ignores a field the client still
        // sends, so this is not structurally breaking — even though the BA's parcels-to-flats case
        // shows it can still be the most consequential change in an estate. Classification is
        // primary; the verdict is secondary and attributed.
        var catalog = await Aggregate(Versioned(
            v1Request: """{"type":"object","properties":{"line1":{"type":"string"},"line2":{"type":"string"}}}""",
            v2Request: """{"type":"object","properties":{"line1":{"type":"string"}}}"""));

        var v2 = Assert.Single(catalog.Topics, t => t.Topic == "order:create" && t.Version == "2");

        Assert.Equal(MeshCompatibilityVerdict.Warning, v2.Compatibility!.Overall);
        Assert.Equal("propertyRemoved", Assert.Single(v2.Compatibility.Changes).Kind);
    }

    [Fact]
    public async Task TypeChange_IsBreaking_AndRecordsThatTheWalkStopped()
    {
        var catalog = await Aggregate(Versioned(
            v1Request: """{"type":"object","properties":{"amount":{"type":"integer"}}}""",
            v2Request: """{"type":"object","properties":{"amount":{"type":"number"}}}"""));

        var v2 = Assert.Single(catalog.Topics, t => t.Topic == "order:create" && t.Version == "2");

        Assert.Equal(MeshCompatibilityVerdict.Breaking, v2.Compatibility!.Overall);
        Assert.Equal("typeChanged", Assert.Single(v2.Compatibility.Changes).Kind);

        // Anything beneath a changed type was never compared, so the count is a floor. The paths are
        // carried so a UI can say that at the node instead of presenting a floor as a total.
        Assert.Equal(new[] { "order:create.request.amount" }, v2.Compatibility.TruncatedPaths);
    }

    [Fact]
    public async Task RenameSurfacesAsBothHalves_WithNoRenameKind()
    {
        var catalog = await Aggregate(Versioned(
            v1Request: """{"type":"object","required":["customerId"],"properties":{"customerId":{"type":"string"}}}""",
            v2Request: """{"type":"object","required":["customerRef"],"properties":{"customerRef":{"type":"string"}}}"""));

        var v2 = Assert.Single(catalog.Topics, t => t.Topic == "order:create" && t.Version == "2");

        Assert.Equal(2, v2.Compatibility!.Changes.Length);
        Assert.Contains(v2.Compatibility.Changes, c => c.Kind == "propertyRemoved");
        Assert.Contains(v2.Compatibility.Changes, c => c.Kind == "requiredPropertyAdded");
        Assert.DoesNotContain(v2.Compatibility.Changes, c => c.Kind.Contains("rename", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SideAbsentOnOneVersion_IsNamed_RatherThanSilentlyCoveredByTheVerdict()
    {
        // v1 publishes a response, v2 does not. The request side still compares cleanly, so there IS
        // a verdict — but it does not cover the response, and the reader is told which side it misses.
        var spec = """
        {"requests":[
          {"topic":"order:create","version":"1","request":{"$ref":"#/components/schemas/R1"},"response":{"$ref":"#/components/schemas/R1"}},
          {"topic":"order:create","version":"2","request":{"$ref":"#/components/schemas/R1"}}],
         "components":{"schemas":{"R1":{"type":"object","properties":{"id":{"type":"string"}}}}}}
        """;

        var catalog = await Aggregate(spec);
        var v2 = Assert.Single(catalog.Topics, t => t.Topic == "order:create" && t.Version == "2");

        Assert.Equal(MeshCompatibilityVerdict.Compatible, v2.Compatibility!.Overall);
        Assert.Equal(new[] { "response" }, v2.Compatibility.NotComparedSides);
    }

    [Fact]
    public async Task ReservedTopicsCarryNoCompatibility()
    {
        var catalog = await Aggregate(
            """{"requests":[{"topic":"order:create"}]}""");

        Assert.All(catalog.Topics.Where(t => t.Reserved), t => Assert.Null(t.Compatibility));
    }

    private static string Versioned(string v1Request, string v2Request) =>
        """
        {"requests":[
          {"topic":"order:create","version":"1","request":{"$ref":"#/components/schemas/V1"}},
          {"topic":"order:create","version":"2","request":{"$ref":"#/components/schemas/V2"}}],
         "components":{"schemas":{"V1":__V1__,"V2":__V2__}}}
        """
            .Replace("__V1__", v1Request)
            .Replace("__V2__", v2Request);

    private async Task<MeshTopicCatalog> Aggregate(string spec)
    {
        var handler = new RoutingHttpMessageHandler()
            .MapGet(SpecUrl, HttpStatusCode.OK, spec)
            .MapGet(HealthUrl, HttpStatusCode.OK,
                """{"isHealthy":true,"results":[],"totalDurationMs":1}""");

        var store = new FileSystemMeshArtifactStore(_rootDirectory);
        var aggregator = new MeshAggregator(
            new IMeshServiceSource[] { new HttpMeshServiceSource(new HttpClient(handler)) }, store);

        await aggregator.RunOnceAsync(new MeshServiceRegistry(
            new[] { new MeshServiceRegistryEntry("orders-api", SpecUrl, HealthUrl) }));

        return JsonSerializer.Deserialize<MeshTopicCatalog>(
            (await store.TryReadAsync("topics.json"))!, JsonOptions)!;
    }

    private class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode StatusCode, string? Content)> _responses = new();

        public RoutingHttpMessageHandler MapGet(string url, HttpStatusCode statusCode, string? content)
        {
            _responses[url] = (statusCode, content);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!_responses.TryGetValue(url, out var response))
            {
                throw new InvalidOperationException($"No stubbed response configured for {url}");
            }

            var message = new HttpResponseMessage(response.StatusCode);
            if (response.Content != null)
            {
                message.Content = new StringContent(response.Content);
            }

            return Task.FromResult(message);
        }
    }
}
