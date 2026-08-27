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

    // #188: a per-trace fetch that throws (a genuine connection-level failure - GetStringOrNullAsync only
    // swallows a reachable-but-unsuccessful *response*) must not propagate out of GetCorrelationAsync and
    // discard every already-fetched result. Only the throwing trace is dropped.
    private sealed class ThrowingForOneTraceHandler : HttpMessageHandler
    {
        private readonly string _throwingTraceId;
        private readonly Func<string, (HttpStatusCode, string)> _route;

        public ThrowingForOneTraceHandler(string throwingTraceId, Func<string, (HttpStatusCode, string)> route)
        {
            _throwingTraceId = throwingTraceId;
            _route = route;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith($"/api/traces/{_throwingTraceId}"))
            {
                // A genuine connection-level failure (timeout, DNS, refused) - not a reachable-but-
                // unsuccessful response - so this must propagate up to the per-trace fetch exactly like a
                // real Tempo hiccup would, for #188's isolation to matter.
                throw new HttpRequestException("simulated Tempo connection failure");
            }

            var (status, body) = _route(path);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task GetCorrelationAsync_IsolatesAPerTraceFetchFailure_AndKeepsTheRest()
    {
        // #188: 3 matched traces, the middle one's per-trace fetch throws. The old sequential foreach had
        // no try/catch, so this exception would propagate out of GetCorrelationAsync entirely and the
        // composite's own fetch-isolation would degrade the WHOLE correlation to null - losing t-a and
        // t-c's already-fetched results too. Per-trace isolation must return the other 2 (N-1), not 0.
        const string correlationId = "ticket-42";
        var search = """
        { "traces": [
            { "traceID": "t-a", "startTimeUnixNano": "1500000000000000000" },
            { "traceID": "t-b", "startTimeUnixNano": "1500000050000000000" },
            { "traceID": "t-c", "startTimeUnixNano": "1500000100000000000" }
        ] }
        """;
        var handler = new ThrowingForOneTraceHandler("t-b", path =>
        {
            if (path.StartsWith("/api/search"))
            {
                return (HttpStatusCode.OK, search);
            }

            var isA = path.Contains("t-a");
            return (HttpStatusCode.OK, TraceBody(
                isA ? "orders:create" : "billing:charge",
                "ok",
                isA ? "orders-api" : "billing-api",
                correlationId));
        });
        var source = new TempoTraceSource(new HttpClient(handler), new TempoTraceSourceOptions(TempoUrl));

        var view = await source.GetCorrelationAsync(correlationId);

        Assert.NotNull(view);
        Assert.Equal(2, view!.Traces.Count); // t-b dropped, t-a and t-c kept - not zero
        Assert.DoesNotContain(view.Traces, t => t.TraceId == "t-b");
        Assert.Contains(view.Traces, t => t.TraceId == "t-a");
        Assert.Contains(view.Traces, t => t.TraceId == "t-c");
    }

    [Fact]
    public async Task GetCorrelationAsync_LogsAWarning_WhenAPerTraceFetchFails()
    {
        const string correlationId = "ticket-42";
        var search = """{ "traces": [ { "traceID": "t-a" }, { "traceID": "t-b" } ] }""";
        var handler = new ThrowingForOneTraceHandler("t-b", path => path.StartsWith("/api/search")
            ? (HttpStatusCode.OK, search)
            : (HttpStatusCode.OK, TraceBody("orders:create", "ok", "orders-api", correlationId)));
        var logger = new RecordingLogger();
        var source = new TempoTraceSource(new HttpClient(handler), new TempoTraceSourceOptions(TempoUrl), logger);

        await source.GetCorrelationAsync(correlationId);

        Assert.Contains(logger.Messages, m => m.Contains("t-b", StringComparison.Ordinal));
    }

    // #188: every per-trace fetch blocks until ALL of them have arrived, then all unblock together. This
    // is only satisfiable if the correlation fetch fans the requests out concurrently (BoundedFanOut) - a
    // sequential `foreach await` would issue the second fetch only after the first completes, which never
    // happens here, so a regression to sequential deadlocks this test instead of passing it. The outer
    // Task.WhenAny/Delay is a deadlock guard, not a timing assertion.
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

            await _allArrived.Task; // only resolves once every expected fetch has arrived

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "batches": [] }""") };
        }
    }

    [Fact]
    public async Task GetCorrelationAsync_FetchesMatchedTracesConcurrently_NotSequentially()
    {
        var search = """{ "traces": [ { "traceID": "t-a" }, { "traceID": "t-b" }, { "traceID": "t-c" } ] }""";
        var handler = new GatedConcurrencyHandler(search, expectedConcurrentRequests: 3);
        var options = new TempoTraceSourceOptions(TempoUrl); // SearchConcurrency default (8) >= 3
        var source = new TempoTraceSource(new HttpClient(handler), options);

        var task = source.GetCorrelationAsync("ticket-1");
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(task, completed); // did not deadlock - all 3 fetches were in flight together
    }

    [Fact]
    public async Task GetCorrelationAsync_UsesTheConfiguredCorrelationSearchLimit()
    {
        // #190: the search's `limit` query param comes from options, not a hardcoded 100.
        var handler = new RoutingHandler(path =>
        {
            if (path.StartsWith("/api/search"))
            {
                Assert.Contains("&limit=42", path);
            }

            return (HttpStatusCode.OK, """{ "traces": [] }""");
        });
        var options = new TempoTraceSourceOptions(TempoUrl) { CorrelationSearchLimit = 42 };
        var source = new TempoTraceSource(new HttpClient(handler), options);

        await source.GetCorrelationAsync("ticket-1");
    }

    [Fact]
    public async Task GetCorrelationAsync_LogsAWarning_WhenTheSearchReturnsExactlyTheConfiguredLimit()
    {
        // #190: exactly CorrelationSearchLimit matches means Tempo's /api/search - which has no further
        // paging - may have more matches beyond the limit that were never returned. Logged, not silent
        // (X-Ray's #77 at-limit warning, same rationale).
        var search = """{ "traces": [ { "traceID": "t-a" }, { "traceID": "t-b" } ] }""";
        var handler = new RoutingHandler(path => path.StartsWith("/api/search")
            ? (HttpStatusCode.OK, search)
            : (HttpStatusCode.OK, TraceBody("orders:create", "ok", "orders-api")));
        var options = new TempoTraceSourceOptions(TempoUrl) { CorrelationSearchLimit = 2 };
        var logger = new RecordingLogger();
        var source = new TempoTraceSource(new HttpClient(handler), options, logger);

        await source.GetCorrelationAsync("ticket-1");

        Assert.Contains(logger.Messages, m => m.Contains("CorrelationSearchLimit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCorrelationAsync_DoesNotWarn_WhenBelowTheConfiguredLimit()
    {
        var search = """{ "traces": [ { "traceID": "t-a" } ] }""";
        var handler = new RoutingHandler(path => path.StartsWith("/api/search")
            ? (HttpStatusCode.OK, search)
            : (HttpStatusCode.OK, TraceBody("orders:create", "ok", "orders-api")));
        var options = new TempoTraceSourceOptions(TempoUrl) { CorrelationSearchLimit = 100 };
        var logger = new RecordingLogger();
        var source = new TempoTraceSource(new HttpClient(handler), options, logger);

        await source.GetCorrelationAsync("ticket-1");

        Assert.Empty(logger.Messages);
    }

    // A minimal ILogger that records formatted messages, for asserting on warning logs without pulling in
    // a mocking framework's extension-method plumbing for ILogger (mirrors XRayTraceSourceTest).
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
}
