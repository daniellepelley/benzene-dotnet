using System.Text.Json;
using System.Text.Json.Nodes;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Mesh.Collector;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Conformance.Test;

/// <summary>
/// Runs docs/specification/conformance/mesh-service-version-cases.json (mesh.md §2.4, D26): the
/// catalog is keyed by <c>(service, serviceVersion)</c>, observed through the service query's
/// <c>versions</c> list while the fleet query keeps one row per service. Same runner and matching rule
/// as <see cref="MeshCollectorConformanceTest"/>, except that a <c>versions</c> array is compared as a
/// set keyed by <c>serviceVersion</c> (same length, each expected element subset-matching the actual
/// element with its <c>serviceVersion</c>), because the fixture pins no order.
/// </summary>
public class MeshServiceVersionConformanceTest
{
    private static readonly Lazy<MeshCollectorConformanceTest.CollectorFixture> Fixture = new(() =>
        ConformanceFixtures.Load<MeshCollectorConformanceTest.CollectorFixture>("mesh-service-version-cases.json"));

    public static IEnumerable<object[]> CaseNames() =>
        Fixture.Value.Cases.Select(x => new object[] { x.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task ServiceVersionCase_StepsProduceTheExpectedResponses(string caseName)
    {
        var collectorCase = Fixture.Value.Cases.Single(x => x.Name == caseName);

        var services = new ServiceCollection();
        services.AddLogging();
        var collectorStore = new MeshCollectorStore();
        services.AddSingleton(collectorStore);
        services.AddSingleton<IMeshFleetReadModel>(collectorStore);

        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();

        var pipelineBuilder = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipelineBuilder.UseMessageHandlers(MeshCollectorHandlers.All);
        var application = new BenzeneMessageApplication(pipelineBuilder.Build());
        var resolverFactory = container.CreateServiceResolverFactory();

        for (var i = 0; i < collectorCase.Steps.Count; i++)
        {
            var step = collectorCase.Steps[i];
            var response = await application.HandleAsync(new BenzeneMessageRequest
            {
                Topic = step.Request.Topic,
                Headers = step.Request.Headers,
                Body = step.Request.Body
            }, resolverFactory);

            Assert.True(step.Expected.StatusCode == response.StatusCode,
                $"step {i} ({step.Request.Topic}): statusCode '{response.StatusCode}', expected '{step.Expected.StatusCode}' (body: {response.Body})");

            if (step.Expected.Body is not { } expectedBody)
            {
                continue;
            }
            Assert.False(string.IsNullOrEmpty(response.Body), $"step {i}: expected a body but none was written");
            using var actualBody = JsonDocument.Parse(response.Body);
            var mismatch = FindMismatch(expectedBody, actualBody.RootElement, $"step {i} $");
            Assert.True(mismatch == null, $"{caseName}: body mismatch at {mismatch}");
        }
    }

    private static string? FindMismatch(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != JsonValueKind.Object
            || !expected.TryGetProperty("versions", out var expectedVersions)
            || expectedVersions.ValueKind != JsonValueKind.Array)
        {
            return MeshCollectorConformanceTest.FindMeshSubsetMismatch(expected, actual, path);
        }

        // Everything but versions by the mesh rule...
        var rest = JsonNode.Parse(expected.GetRawText())!.AsObject();
        rest.Remove("versions");
        using var restDocument = JsonDocument.Parse(rest.ToJsonString());
        var mismatch = MeshCollectorConformanceTest.FindMeshSubsetMismatch(restDocument.RootElement, actual, path);
        if (mismatch != null)
        {
            return mismatch;
        }

        // ...and versions as a set keyed by serviceVersion.
        if (!actual.TryGetProperty("versions", out var actualVersions) || actualVersions.ValueKind != JsonValueKind.Array)
        {
            return $"{path}.versions: missing";
        }
        var expectedItems = expectedVersions.EnumerateArray().ToArray();
        var actualItems = actualVersions.EnumerateArray().ToArray();
        if (expectedItems.Length != actualItems.Length)
        {
            return $"{path}.versions: expected {expectedItems.Length} items but found {actualItems.Length}";
        }
        foreach (var expectedItem in expectedItems)
        {
            var key = ServiceVersionOf(expectedItem);
            var actualItem = actualItems.Where(x => ServiceVersionOf(x) == key).ToArray();
            if (actualItem.Length != 1)
            {
                return $"{path}.versions: expected exactly one entry with serviceVersion '{key}' but found {actualItem.Length}";
            }
            var itemMismatch = MeshCollectorConformanceTest.FindMeshSubsetMismatch(expectedItem, actualItem[0], $"{path}.versions[{key}]");
            if (itemMismatch != null)
            {
                return itemMismatch;
            }
        }
        return null;
    }

    private static string? ServiceVersionOf(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("serviceVersion", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
