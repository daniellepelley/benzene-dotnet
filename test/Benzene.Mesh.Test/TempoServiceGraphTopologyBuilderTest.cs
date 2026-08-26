using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Tracing.Tempo;
using Xunit;

namespace Benzene.Mesh.Test;

public class TempoServiceGraphTopologyBuilderTest
{
    private const string PrometheusUrl = "https://prometheus.example/api/v1/query";
    private static readonly DateTimeOffset FixedClock = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    private class RoutingByMetricHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responsesByMetricSubstring = new();

        public RoutingByMetricHttpMessageHandler Map(string metricSubstring, string body)
        {
            _responsesByMetricSubstring[metricSubstring] = body;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query)["query"] ?? string.Empty;
            var match = _responsesByMetricSubstring.FirstOrDefault(x => query.Contains(x.Key));
            var body = match.Value ?? """{ "status": "success", "data": { "resultType": "vector", "result": [] } }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static string VectorResult(params (string Client, string Server, double Value)[] samples)
    {
        var entries = samples.Select(s =>
            $$"""{ "metric": { "client": "{{s.Client}}", "server": "{{s.Server}}" }, "value": [1700000000, "{{s.Value}}"] }""");
        return $$"""{ "status": "success", "data": { "resultType": "vector", "result": [ {{string.Join(",", entries)}} ] } }""";
    }

    private static TempoServiceGraphTopologyBuilder CreateBuilder(RoutingByMetricHttpMessageHandler handler)
    {
        var client = new PrometheusQueryClient(new HttpClient(handler));
        var options = new TempoTopologyOptions(PrometheusUrl);
        return new TempoServiceGraphTopologyBuilder(client, options, () => FixedClock);
    }

    [Fact]
    public async Task BuildAsync_FullData_JoinsIntoSingleEdge()
    {
        var handler = new RoutingByMetricHttpMessageHandler()
            .Map("traces_service_graph_request_total", VectorResult(("orders-api", "payments-api", 10)))
            .Map("traces_service_graph_request_failed_total", VectorResult(("orders-api", "payments-api", 1)))
            // Prometheus applies the "* 1000" seconds->ms conversion server-side (it's part of the
            // PromQL query text), so these canned values are already post-conversion milliseconds.
            .Map("0.50", VectorResult(("orders-api", "payments-api", 20)))
            .Map("0.95", VectorResult(("orders-api", "payments-api", 80)))
            .Map("0.99", VectorResult(("orders-api", "payments-api", 150)));

        var topology = await CreateBuilder(handler).BuildAsync();

        var edge = Assert.Single(topology.Edges);
        Assert.Equal("orders-api", edge.Client);
        Assert.Equal("payments-api", edge.Server);
        Assert.Equal(TopologyEdgeSource.Tempo, edge.Source);
        Assert.Equal(10, edge.RequestsPerMinute);
        Assert.Equal(0.1, edge.ErrorRate);
        Assert.Equal(20, edge.P50LatencyMs!.Value, 3);
        Assert.Equal(80, edge.P95LatencyMs!.Value, 3);
        Assert.Equal(150, edge.P99LatencyMs!.Value, 3);
        Assert.Equal(FixedClock, topology.GeneratedAtUtc);
    }

    [Fact]
    public async Task BuildAsync_RequestsButNoFailures_ErrorRateIsZeroNotNull()
    {
        var handler = new RoutingByMetricHttpMessageHandler()
            .Map("traces_service_graph_request_total", VectorResult(("orders-api", "payments-api", 10)));

        var topology = await CreateBuilder(handler).BuildAsync();

        var edge = Assert.Single(topology.Edges);
        Assert.Equal(0, edge.ErrorRate);
        Assert.Null(edge.P50LatencyMs);
        Assert.Null(edge.P95LatencyMs);
        Assert.Null(edge.P99LatencyMs);
    }

    [Fact]
    public async Task BuildAsync_NoDataAtAllForAnyMetric_NoEdges()
    {
        var handler = new RoutingByMetricHttpMessageHandler();

        var topology = await CreateBuilder(handler).BuildAsync();

        Assert.Empty(topology.Edges);
    }

    [Fact]
    public async Task BuildAsync_MultipleEdges_AllPresent()
    {
        var handler = new RoutingByMetricHttpMessageHandler()
            .Map("traces_service_graph_request_total", VectorResult(
                ("orders-api", "payments-api", 20),
                ("orders-api", "shipping-api", 5)))
            .Map("traces_service_graph_request_failed_total", VectorResult(
                ("orders-api", "shipping-api", 5)));

        var topology = await CreateBuilder(handler).BuildAsync();

        Assert.Equal(2, topology.Edges.Length);
        var ordersToPayments = topology.Edges.Single(e => e.Server == "payments-api");
        var ordersToShipping = topology.Edges.Single(e => e.Server == "shipping-api");
        Assert.Equal(20, ordersToPayments.RequestsPerMinute);
        Assert.Equal(0, ordersToPayments.ErrorRate);
        Assert.Equal(5, ordersToShipping.RequestsPerMinute);
        Assert.Equal(1.0, ordersToShipping.ErrorRate);
    }

    [Fact]
    public async Task BuildAsync_ZeroRequestsButFailedDataSomehow_DoesNotThrow_ErrorRateIsZero()
    {
        var handler = new RoutingByMetricHttpMessageHandler()
            .Map("traces_service_graph_request_failed_total", VectorResult(("orders-api", "payments-api", 2)));

        var topology = await CreateBuilder(handler).BuildAsync();

        var edge = Assert.Single(topology.Edges);
        Assert.Null(edge.RequestsPerMinute);
        Assert.Equal(0, edge.ErrorRate);
    }

    private sealed class ThrowingForMetricHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _throwingMetricSubstring;
        private readonly Dictionary<string, string> _responsesByMetricSubstring = new();

        public ThrowingForMetricHttpMessageHandler(string throwingMetricSubstring) => _throwingMetricSubstring = throwingMetricSubstring;

        public ThrowingForMetricHttpMessageHandler Map(string metricSubstring, string body)
        {
            _responsesByMetricSubstring[metricSubstring] = body;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query)["query"] ?? string.Empty;
            if (query.Contains(_throwingMetricSubstring))
            {
                // A genuine connection-level failure (timeout, DNS, refused) - PrometheusQueryClient
                // only swallows a reachable-but-unsuccessful *response*, so this must propagate up to
                // the builder exactly like a real Prometheus hiccup would.
                throw new HttpRequestException("simulated Prometheus connection failure");
            }

            var match = _responsesByMetricSubstring.FirstOrDefault(x => query.Contains(x.Key));
            var body = match.Value ?? """{ "status": "success", "data": { "resultType": "vector", "result": [] } }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task BuildAsync_OnePromQlCallThrows_DegradesThatEdgeDimension_RatherThanFailingTheWholeBuild()
    {
        // #75: a transient Prometheus failure on ONE of the 5 sequential PromQL calls (here, the p99
        // latency query) must not take down mesh:topology - the other 4 dimensions still populate the
        // edge, and BuildAsync completes rather than throwing.
        var handler = new ThrowingForMetricHttpMessageHandler("0.99")
            .Map("traces_service_graph_request_total", VectorResult(("orders-api", "payments-api", 10)))
            .Map("traces_service_graph_request_failed_total", VectorResult(("orders-api", "payments-api", 1)))
            .Map("0.50", VectorResult(("orders-api", "payments-api", 20)))
            .Map("0.95", VectorResult(("orders-api", "payments-api", 80)));

        var topology = await CreateBuilder(handler).BuildAsync();

        var edge = Assert.Single(topology.Edges);
        Assert.Equal("orders-api", edge.Client);
        Assert.Equal("payments-api", edge.Server);
        Assert.Equal(10, edge.RequestsPerMinute);
        Assert.Equal(0.1, edge.ErrorRate);
        Assert.Equal(20, edge.P50LatencyMs!.Value, 3);
        Assert.Equal(80, edge.P95LatencyMs!.Value, 3);
        Assert.Null(edge.P99LatencyMs); // the failing dimension degrades to absent, not fatal
    }

    [Fact]
    public async Task BuildAsync_EveryPromQlCallThrows_ReturnsAnEmptyTopology_RatherThanThrowing()
    {
        var handler = new ThrowingForMetricHttpMessageHandler(""); // matches every query (empty substring)

        var topology = await CreateBuilder(handler).BuildAsync();

        Assert.Empty(topology.Edges);
    }
}
