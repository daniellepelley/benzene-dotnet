using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benzene.CodeGen.Cli.Core.Commands.Spec;
using Benzene.Schema.OpenApi;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// MeshSpecSource: --mesh <manifest-url> --service <name> - fetches manifest.json, finds the named
// service entry, then fetches its services/{name}.json snapshot resolved *relative to the manifest
// URL*, exactly as the Mesh UI resolves relative-path artifacts (`new URL(relativePath,
// manifestUrl)`), and returns its cached specJson verbatim.
public class MeshSpecSourceTest
{
    private const string ManifestUrl = "https://mesh.example.com/mesh-store/manifest.json";

    private static string ManifestJson(params (string Name, string Status)[] services)
    {
        var entries = string.Join(",", Array.ConvertAll(services, s =>
            $"{{\"name\":\"{s.Name}\",\"status\":\"{s.Status}\",\"contractDrift\":false,\"specUrl\":\"https://{s.Name}.example.com/benzene/spec\",\"healthUrl\":\"https://{s.Name}.example.com/benzene/health\"}}"));
        return $"{{\"generatedAtUtc\":\"2026-08-01T00:00:00Z\",\"services\":[{entries}]}}";
    }

    private static string SnapshotJson(string name, string specJson)
    {
        var escapedSpec = specJson.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"{{\"name\":\"{name}\",\"fetchedAtUtc\":\"2026-08-01T00:00:00Z\",\"specJson\":\"{escapedSpec}\",\"specHash\":\"abc\",\"previousSpecHash\":null,\"contractDrift\":false,\"health\":null,\"error\":null}}";
    }

    [Fact]
    public async Task GetSpecJsonAsync_ResolvesSnapshotRelativeToManifestUrl_AndReturnsItsSpecJsonVerbatim()
    {
        var routes = new Dictionary<string, (HttpStatusCode Status, string Body)>
        {
            [ManifestUrl] = (HttpStatusCode.OK, ManifestJson(("orders-api", "Healthy"))),
            ["https://mesh.example.com/mesh-store/services/orders-api.json"] =
                (HttpStatusCode.OK, SnapshotJson("orders-api", "{\"info\":{\"title\":\"Orders\"}}")),
        };
        var handler = new RoutingHandler(routes);
        var source = new MeshSpecSource(ManifestUrl, "orders-api", new HttpClient(handler), ownsClient: false);

        var json = await source.GetSpecJsonAsync(new SpecRequest("benzene", "json"));

        Assert.Equal("{\"info\":{\"title\":\"Orders\"}}", json);
    }

    [Fact]
    public async Task GetSpecJsonAsync_ServiceNameLookupIsCaseInsensitive()
    {
        var routes = new Dictionary<string, (HttpStatusCode Status, string Body)>
        {
            [ManifestUrl] = (HttpStatusCode.OK, ManifestJson(("Orders-Api", "Healthy"))),
            ["https://mesh.example.com/mesh-store/services/Orders-Api.json"] =
                (HttpStatusCode.OK, SnapshotJson("Orders-Api", "{\"ok\":true}")),
        };
        var handler = new RoutingHandler(routes);
        var source = new MeshSpecSource(ManifestUrl, "orders-api", new HttpClient(handler), ownsClient: false);

        var json = await source.GetSpecJsonAsync(new SpecRequest("benzene", "json"));

        Assert.Equal("{\"ok\":true}", json);
    }

    [Fact]
    public async Task GetSpecJsonAsync_UnknownService_ThrowsListingKnownServices()
    {
        var routes = new Dictionary<string, (HttpStatusCode Status, string Body)>
        {
            [ManifestUrl] = (HttpStatusCode.OK, ManifestJson(("orders-api", "Healthy"), ("payments-api", "Healthy"))),
        };
        var handler = new RoutingHandler(routes);
        var source = new MeshSpecSource(ManifestUrl, "shipping-api", new HttpClient(handler), ownsClient: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetSpecJsonAsync(new SpecRequest("benzene", "json")));

        Assert.Contains("shipping-api", exception.Message);
        Assert.Contains("orders-api", exception.Message);
        Assert.Contains("payments-api", exception.Message);
    }

    [Fact]
    public async Task GetSpecJsonAsync_SnapshotHasNoCachedSpec_Throws()
    {
        var routes = new Dictionary<string, (HttpStatusCode Status, string Body)>
        {
            [ManifestUrl] = (HttpStatusCode.OK, ManifestJson(("orders-api", "SpecFetchFailed"))),
            ["https://mesh.example.com/mesh-store/services/orders-api.json"] =
                (HttpStatusCode.OK,
                    "{\"name\":\"orders-api\",\"fetchedAtUtc\":\"2026-08-01T00:00:00Z\",\"specJson\":null,\"specHash\":null,\"previousSpecHash\":null,\"contractDrift\":false,\"health\":null,\"error\":\"TimeoutException\"}"),
        };
        var handler = new RoutingHandler(routes);
        var source = new MeshSpecSource(ManifestUrl, "orders-api", new HttpClient(handler), ownsClient: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetSpecJsonAsync(new SpecRequest("benzene", "json")));
        Assert.Contains("no cached spec", exception.Message);
    }

    [Fact]
    public async Task GetSpecJsonAsync_ManifestNotFound_Throws()
    {
        var routes = new Dictionary<string, (HttpStatusCode Status, string Body)>
        {
            [ManifestUrl] = (HttpStatusCode.NotFound, ""),
        };
        var handler = new RoutingHandler(routes);
        var source = new MeshSpecSource(ManifestUrl, "orders-api", new HttpClient(handler), ownsClient: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetSpecJsonAsync(new SpecRequest("benzene", "json")));
        Assert.Contains("404", exception.Message);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes;

        public RoutingHandler(Dictionary<string, (HttpStatusCode Status, string Body)> routes)
        {
            _routes = routes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!_routes.TryGetValue(url, out var route))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"no fixture route for {url}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body) });
        }
    }
}
