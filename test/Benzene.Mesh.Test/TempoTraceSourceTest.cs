using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Fleet.Tempo;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// The Tempo-backed trace source: trace-by-id (<c>/api/traces/{id}</c>, OTLP/JSON) mapped to the mesh's
/// waterfall, correlation + recent-flows via TraceQL search (<c>/api/search</c>). Reads the Benzene span
/// attributes verbatim (Tempo preserves keys), filters to topic-bearing spans, and orders by start time —
/// the non-AWS realisation of <c>IMeshTraceSource</c>, verified against Tempo's documented API shapes.
/// </summary>
public class TempoTraceSourceTest
{
    private const string TempoUrl = "http://tempo:3200";

    // Routes by path so trace-by-id and search can be stubbed independently in one client.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode, string)> _route;
        public int Requests { get; private set; }

        public RoutingHandler(Func<string, (HttpStatusCode, string)> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var (status, body) = _route(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static TempoTraceSource Source(Func<string, (HttpStatusCode, string)> route, out RoutingHandler handler)
    {
        handler = new RoutingHandler(route);
        return new TempoTraceSource(new HttpClient(handler), new TempoTraceSourceOptions(TempoUrl));
    }

    private static string TraceBody(string topic, string status, string service, string correlationId = "",
        string startNano = "1500000000000000000", string endNano = "1500000000400000000")
    {
        var correlationAttr = correlationId.Length == 0
            ? ""
            : ", { \"key\": \"benzene.correlation-id\", \"value\": { \"stringValue\": \"" + correlationId + "\" } }";
        return $$"""
        {
          "batches": [
            {
              "resource": { "attributes": [ { "key": "service.name", "value": { "stringValue": "{{service}}" } } ] },
              "scopeSpans": [
                {
                  "spans": [
                    {
                      "spanId": "aabbccdd", "parentSpanId": "",
                      "startTimeUnixNano": "{{startNano}}",
                      "endTimeUnixNano": "{{endNano}}",
                      "attributes": [
                        { "key": "benzene.topic", "value": { "stringValue": "{{topic}}" } },
                        { "key": "benzene.version", "value": { "stringValue": "v1" } },
                        { "key": "benzene.status", "value": { "stringValue": "{{status}}" } },
                        { "key": "benzene.exception.type", "value": { "stringValue": "System.TimeoutException" } }{{correlationAttr}}
                      ]
                    },
                    {
                      "spanId": "eeff0011",
                      "startTimeUnixNano": "1500000000100000000",
                      "endTimeUnixNano": "1500000000150000000",
                      "attributes": [ { "key": "http.method", "value": { "stringValue": "POST" } } ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;
    }

    [Fact]
    public async Task GetTraceAsync_MapsTopicBearingSpans_AndSkipsNonBenzeneSpans()
    {
        var source = Source(_ => (HttpStatusCode.OK, TraceBody("orders:create", "ok", "orders-api")), out _);

        var view = await source.GetTraceAsync("trace-1");

        Assert.NotNull(view);
        Assert.Equal("trace-1", view!.TraceId);
        var evt = Assert.Single(view.Events); // only the topic-bearing span, not the http.method span
        Assert.Equal("orders:create", evt.Topic);
        Assert.Equal("v1", evt.TopicVersion);
        Assert.Equal("ok", evt.Status);
        Assert.Equal("System.TimeoutException", evt.ExceptionType); // the failure's WHY (spec §3), read when present
        Assert.Equal("orders-api", evt.Service);
        Assert.Equal("aabbccdd", evt.SpanId);
        Assert.Null(evt.ParentSpanId);       // empty parentSpanId → null
        Assert.Equal(400, evt.DurationMs, 3); // (end-start) nanos → ms
    }

    [Fact]
    public async Task GetTraceAsync_PrefersBenzeneServiceTag_OverResourceServiceName()
    {
        // The pipeline's benzene.service is authoritative; the resource service.name (here an infra name) is
        // only the fallback — keeps the mesh's own namespace winning uniformly across the trace-plane mappers.
        var body = """
        { "batches": [ {
          "resource": { "attributes": [ { "key": "service.name", "value": { "stringValue": "aws-lambda" } } ] },
          "scopeSpans": [ { "spans": [ {
            "spanId": "aabbccdd", "startTimeUnixNano": "1500000000000000000", "endTimeUnixNano": "1500000000400000000",
            "attributes": [
              { "key": "benzene.topic", "value": { "stringValue": "orders:create" } },
              { "key": "benzene.service", "value": { "stringValue": "orders-api" } }
            ] } ] } ] } ] }
        """;
        var source = Source(_ => (HttpStatusCode.OK, body), out _);

        var view = await source.GetTraceAsync("trace-1");

        Assert.Equal("orders-api", Assert.Single(view!.Events).Service); // not "aws-lambda"
    }

    [Fact]
    public async Task GetTraceAsync_UnknownTrace_ReturnsNull()
    {
        var source = Source(_ => (HttpStatusCode.NotFound, ""), out _);
        Assert.Null(await source.GetTraceAsync("nope"));
    }

    [Fact]
    public async Task GetTraceAsync_TraceWithoutBenzeneSpans_ReturnsNull()
    {
        var body = """{ "batches": [ { "resource": {}, "scopeSpans": [ { "spans": [ { "spanId": "x", "attributes": [ { "key": "db.system", "value": { "stringValue": "dynamodb" } } ] } ] } ] } ] }""";
        var source = Source(_ => (HttpStatusCode.OK, body), out _);
        Assert.Null(await source.GetTraceAsync("t1"));
    }

    [Fact]
    public async Task GetCorrelationAsync_SearchesByAnnotation_ThenFetchesAndGroupsByTrace()
    {
        const string correlationId = "ticket-42";
        var search = """{ "traces": [ { "traceID": "t-b", "startTimeUnixNano": "1500000100000000000" }, { "traceID": "t-a", "startTimeUnixNano": "1500000000000000000" } ] }""";
        var source = Source(path =>
        {
            if (path.StartsWith("/api/search"))
            {
                Assert.Contains("benzene.correlation-id", Uri.UnescapeDataString(path));
                Assert.Contains(correlationId, Uri.UnescapeDataString(path));
                return (HttpStatusCode.OK, search);
            }
            // Each fetched trace carries a distinct topic and start time so earliest-first is checkable
            // (t-a's span starts before t-b's, independent of the order search returned them).
            var isA = path.Contains("t-a");
            return (HttpStatusCode.OK, TraceBody(
                isA ? "orders:create" : "billing:charge",
                "ok",
                isA ? "orders-api" : "billing-api",
                correlationId,
                startNano: isA ? "1500000000000000000" : "1500000100000000000",
                endNano: isA ? "1500000000400000000" : "1500000100400000000"));
        }, out _);

        var view = await source.GetCorrelationAsync(correlationId);

        Assert.NotNull(view);
        Assert.Equal(correlationId, view!.CorrelationId);
        Assert.Equal(2, view.Traces.Count);
        // Earliest-first by the trace's own events, regardless of search order.
        Assert.Equal("t-a", view.Traces[0].TraceId);
        Assert.Equal("orders:create", view.Traces[0].Events.Single().Topic);
        Assert.Equal("t-b", view.Traces[1].TraceId);
    }

    [Fact]
    public async Task GetCorrelationAsync_NoMatches_ReturnsNull()
    {
        var source = Source(_ => (HttpStatusCode.OK, """{ "traces": [] }"""), out _);
        Assert.Null(await source.GetCorrelationAsync("nobody"));
    }

    [Fact]
    public async Task GetRecentFlowsAsync_MapsSearchSummaries_NewestFirst()
    {
        var search = """
        {
          "traces": [
            { "traceID": "old", "rootServiceName": "orders-api", "startTimeUnixNano": "1500000000000000000", "durationMs": 120 },
            { "traceID": "new", "rootServiceName": "billing-api", "startTimeUnixNano": "1500000100000000000", "durationMs": 55 }
          ]
        }
        """;
        var source = Source(path =>
        {
            Assert.StartsWith("/api/search", path);
            Assert.Contains("benzene.topic", Uri.UnescapeDataString(path));
            return (HttpStatusCode.OK, search);
        }, out var handler);

        var flows = await source.GetRecentFlowsAsync(20);

        Assert.Equal(2, flows.Count);
        Assert.Equal("new", flows[0].TraceId);   // newest first
        Assert.Equal(55, flows[0].DurationMs, 3);
        Assert.Equal("billing-api", Assert.Single(flows[0].Services));
        Assert.Equal(0, flows[0].Events);
        Assert.Equal("old", flows[1].TraceId);
        Assert.Equal(1, handler.Requests);       // recent flows is a single search, no per-row fetch
    }

    [Fact]
    public async Task GetRecentFlowsAsync_HonoursTheLimit()
    {
        var source = Source(_ => (HttpStatusCode.OK,
            """{ "traces": [ { "traceID": "a", "startTimeUnixNano": "3" }, { "traceID": "b", "startTimeUnixNano": "2" }, { "traceID": "c", "startTimeUnixNano": "1" } ] }"""), out _);

        var flows = await source.GetRecentFlowsAsync(2);

        Assert.Equal(2, flows.Count);
    }

    // A minimal ILogger that records formatted messages, for asserting on the search-limit warning log
    // without pulling in a mocking framework's extension-method plumbing for ILogger (matches
    // XRayTraceSourceTest's RecordingLogger).
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static string SearchBody(params string[] traceIds)
        => "{ \"traces\": [ " + string.Join(", ", traceIds.Select(id => "{ \"traceID\": \"" + id + "\" }")) + " ] }";

    // #188(a): a mid-loop failure among several matched traces must not discard the traces already
    // fetched successfully. Before the fix, GetCorrelationAsync fetched sequentially with no per-trace
    // isolation, so one trace's connection failure propagated out and lost every already-fetched trace
    // too (CompositeMeshFleetReadModel's single outer try/catch then degrades the *entire* search).
    [Fact]
    public async Task GetCorrelationAsync_IsolatesAPerTraceFetchFailure_AndReturnsTheSuccessfulTraces()
    {
        const string correlationId = "ticket-1";
        var traceIds = Enumerable.Range(0, 6).Select(i => $"t-{i}").ToArray();
        const string failingTraceId = "t-3";

        var source = Source(path =>
        {
            if (path.StartsWith("/api/search"))
            {
                return (HttpStatusCode.OK, SearchBody(traceIds));
            }

            if (path.Contains(failingTraceId))
            {
                throw new HttpRequestException("simulated transient connection failure");
            }

            return (HttpStatusCode.OK, TraceBody("orders:create", "ok", "orders-api", correlationId));
        }, out _);

        var view = await source.GetCorrelationAsync(correlationId);

        Assert.NotNull(view);
        Assert.Equal(5, view!.Traces.Count); // every trace except the one whose fetch failed
        Assert.DoesNotContain(view.Traces, t => t.TraceId == failingTraceId);
    }

    // #188(b): per-trace fetches during a correlation search must run with bounded concurrency, not fully
    // sequentially (the review's latency probe: max concurrency was 1 before the fix). Every expected
    // request must arrive before any of them is allowed to complete - only satisfiable if the fetches are
    // issued concurrently; a regression to sequential fetching deadlocks this test instead of passing it.
    private sealed class GatedConcurrencyHandler : HttpMessageHandler
    {
        private readonly string _searchBody;
        private readonly int _expectedConcurrentRequests;
        private int _arrived;
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedConcurrencyHandler(string searchBody, int expectedConcurrentRequests)
        {
            _searchBody = searchBody;
            _expectedConcurrentRequests = expectedConcurrentRequests;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/search"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_searchBody) };
            }

            if (Interlocked.Increment(ref _arrived) == _expectedConcurrentRequests)
            {
                _allArrived.TrySetResult();
            }

            await _allArrived.Task; // only resolves once every expected trace fetch has arrived

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "batches": [] }""") };
        }
    }

    [Fact]
    public async Task GetCorrelationAsync_FetchesMatchedTracesConcurrently_NotSequentially()
    {
        var traceIds = new[] { "t-0", "t-1", "t-2" };
        var handler = new GatedConcurrencyHandler(SearchBody(traceIds), traceIds.Length);
        var options = new TempoTraceSourceOptions(TempoUrl); // SearchConcurrency default (8) >= 3
        var source = new TempoTraceSource(new HttpClient(handler), options);

        var task = source.GetCorrelationAsync("ticket-1");
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(task, completed); // did not deadlock - all 3 fetches were in flight together
        Assert.Null(await task);      // each fetch returned an empty trace body → no events → filtered out
    }

    // #190: a correlation search that hits its configured SearchLimit logs a warning rather than
    // truncating silently, mirroring Jaeger's SearchLimitPerService and X-Ray's #77-fixed pattern.
    [Fact]
    public async Task GetCorrelationAsync_LogsAWarning_WhenTheSearchHitsItsConfiguredLimit()
    {
        var traceIds = new[] { "t-0", "t-1" };
        var handler = new RoutingHandler(path =>
        {
            if (path.StartsWith("/api/search"))
            {
                Assert.Contains("limit=2", path); // the configured SearchLimit is honoured on the wire
                return (HttpStatusCode.OK, SearchBody(traceIds));
            }
            return (HttpStatusCode.OK, TraceBody("orders:create", "ok", "orders-api"));
        });
        var logger = new RecordingLogger();
        var options = new TempoTraceSourceOptions(TempoUrl) { SearchLimit = 2 };
        var source = new TempoTraceSource(new HttpClient(handler), options, logger);

        await source.GetCorrelationAsync("ticket-1");

        Assert.Contains(logger.Messages, m => m.Contains("limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetCorrelationAsync_DoesNotLog_WhenTheSearchIsWellUnderTheLimit()
    {
        var traceIds = new[] { "t-0" };
        var handler = new RoutingHandler(path => path.StartsWith("/api/search")
            ? (HttpStatusCode.OK, SearchBody(traceIds))
            : (HttpStatusCode.OK, TraceBody("orders:create", "ok", "orders-api")));
        var logger = new RecordingLogger();
        var options = new TempoTraceSourceOptions(TempoUrl) { SearchLimit = 100 }; // default - 1 match, nowhere near it
        var source = new TempoTraceSource(new HttpClient(handler), options, logger);

        await source.GetCorrelationAsync("ticket-1");

        Assert.DoesNotContain(logger.Messages, m => m.Contains("limit", StringComparison.OrdinalIgnoreCase));
    }
}
