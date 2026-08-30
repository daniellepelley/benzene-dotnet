using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Mesh.Fleet.Jaeger;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// The Jaeger-backed trace source: trace-by-id (<c>/api/traces/{id}</c>) mapped from Jaeger's own model
/// (microsecond times, <c>references</c> parentage, <c>processes</c> service names), plus correlation and
/// recent-flows via a per-service search fan-out (<c>/api/traces?service=…</c>), deduped by trace id. The
/// second non-AWS <c>IMeshTraceSource</c>, verified against Jaeger's documented API shapes.
/// </summary>
public class JaegerTraceSourceTest
{
    private const string JaegerUrl = "http://jaeger:16686";
    private static readonly string[] TwoServices = { "orders-api", "billing-api" };

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode, string)> _route;
        public int ServiceListCalls { get; private set; }

        public RoutingHandler(Func<string, (HttpStatusCode, string)> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/services")) ServiceListCalls++;
            var (status, body) = _route(path);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static JaegerTraceSource Source(Func<string, (HttpStatusCode, string)> route,
        string[]? services, out RoutingHandler handler)
    {
        handler = new RoutingHandler(route);
        var options = new JaegerTraceSourceOptions(JaegerUrl) { Services = services };
        return new JaegerTraceSource(new HttpClient(handler), options);
    }

    private static string Tag(string key, string value)
        => "{ \"key\": \"" + key + "\", \"type\": \"string\", \"value\": \"" + value + "\" }";

    private static string Trace(string traceId, string service, string topic, string status,
        string correlationId = "", long startMicros = 1500000000000000, string parentSpanId = "")
    {
        var tags = Tag("benzene.topic", topic) + ", " + Tag("benzene.version", "v1") + ", " + Tag("benzene.status", status)
            + ", " + Tag("benzene.exception.type", "System.TimeoutException");
        if (correlationId.Length > 0) tags += ", " + Tag("benzene.correlation-id", correlationId);
        var references = parentSpanId.Length == 0
            ? "[]"
            : "[ { \"refType\": \"CHILD_OF\", \"spanID\": \"" + parentSpanId + "\" } ]";
        var benzeneSpan = "{ \"spanID\": \"span-" + traceId + "\", \"processID\": \"p1\", \"references\": " + references
            + ", \"startTime\": " + startMicros + ", \"duration\": 400000, \"tags\": [ " + tags + " ] }";
        var otherSpan = "{ \"spanID\": \"other\", \"processID\": \"p1\", \"startTime\": " + (startMicros + 100000)
            + ", \"duration\": 50000, \"tags\": [ " + Tag("http.method", "POST") + " ] }";
        return "{ \"traceID\": \"" + traceId + "\", \"spans\": [ " + benzeneSpan + ", " + otherSpan
            + " ], \"processes\": { \"p1\": { \"serviceName\": \"" + service + "\" } } }";
    }

    private static string Data(params string[] traces) => "{ \"data\": [ " + string.Join(", ", traces) + " ] }";

    private static string ServiceOf(string path)
    {
        var marker = "service=";
        var i = path.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = path.Substring(i + marker.Length);
        var amp = rest.IndexOf('&');
        return Uri.UnescapeDataString(amp < 0 ? rest : rest.Substring(0, amp));
    }

    [Fact]
    public async Task GetTraceAsync_MapsBenzeneSpans_FromTheJaegerModel()
    {
        var source = Source(
            _ => (HttpStatusCode.OK, Data(Trace("trace-1", "orders-api", "orders:create", "ok", parentSpanId: "parent-1"))),
            TwoServices, out _);

        var view = await source.GetTraceAsync("trace-1");

        Assert.NotNull(view);
        Assert.Equal("trace-1", view!.TraceId);
        var evt = Assert.Single(view.Events); // the benzene span only, not the http.method span
        Assert.Equal("orders:create", evt.Topic);
        Assert.Equal("v1", evt.TopicVersion);
        Assert.Equal("System.TimeoutException", evt.ExceptionType); // the failure's WHY (spec §3), read when present
        Assert.Equal("ok", evt.Status);
        Assert.Equal("orders-api", evt.Service);            // from processes[p1].serviceName
        Assert.Equal("span-trace-1", evt.SpanId);
        Assert.Equal("parent-1", evt.ParentSpanId);         // from the CHILD_OF reference
        Assert.Equal(400, evt.DurationMs, 3);               // 400000 µs → ms
    }

    [Fact]
    public async Task GetTraceAsync_PrefersBenzeneServiceTag_OverProcessServiceName()
    {
        // benzene.service on the span wins over Jaeger's processes[].serviceName (an infra name here) — the
        // mesh's own namespace stays authoritative and uniform across the trace-plane mappers.
        var tags = Tag("benzene.topic", "orders:create") + ", " + Tag("benzene.service", "orders-api");
        var span = "{ \"spanID\": \"s1\", \"processID\": \"p1\", \"references\": [], \"startTime\": 1500000000000000, \"duration\": 400000, \"tags\": [ " + tags + " ] }";
        var trace = "{ \"traceID\": \"trace-1\", \"spans\": [ " + span + " ], \"processes\": { \"p1\": { \"serviceName\": \"aws-lambda\" } } }";
        var source = Source(_ => (HttpStatusCode.OK, Data(trace)), TwoServices, out _);

        var view = await source.GetTraceAsync("trace-1");

        Assert.Equal("orders-api", Assert.Single(view!.Events).Service); // not "aws-lambda"
    }

    [Fact]
    public async Task GetTraceAsync_UnknownTrace_ReturnsNull()
    {
        var source = Source(_ => (HttpStatusCode.NotFound, ""), TwoServices, out _);
        Assert.Null(await source.GetTraceAsync("nope"));
    }

    [Fact]
    public async Task GetCorrelationAsync_FansOutAcrossServices_AndDedupesByTraceId()
    {
        const string correlationId = "ticket-42";
        // t-a starts before t-b; billing-api's search also returns t-a (a cross-service trace) → dedupe.
        var source = Source(path =>
        {
            Assert.Contains("benzene.correlation-id", Uri.UnescapeDataString(path));
            return ServiceOf(path) switch
            {
                "orders-api" => (HttpStatusCode.OK, Data(Trace("t-a", "orders-api", "orders:create", "ok", correlationId, 1500000000000000))),
                "billing-api" => (HttpStatusCode.OK, Data(
                    Trace("t-b", "billing-api", "billing:charge", "ok", correlationId, 1500000100000000),
                    Trace("t-a", "orders-api", "orders:create", "ok", correlationId, 1500000000000000))),
                _ => (HttpStatusCode.OK, Data())
            };
        }, TwoServices, out _);

        var view = await source.GetCorrelationAsync(correlationId);

        Assert.NotNull(view);
        Assert.Equal(correlationId, view!.CorrelationId);
        Assert.Equal(2, view.Traces.Count);                // t-a deduped despite appearing in both searches
        Assert.Equal("t-a", view.Traces[0].TraceId);       // earliest-first
        Assert.Equal("t-b", view.Traces[1].TraceId);
    }

    [Fact]
    public async Task GetCorrelationAsync_NoMatches_ReturnsNull()
    {
        var source = Source(_ => (HttpStatusCode.OK, Data()), TwoServices, out _);
        Assert.Null(await source.GetCorrelationAsync("nobody"));
    }

    [Fact]
    public async Task GetRecentFlowsAsync_MapsFullTraces_WithEventCountAndFailure_NewestFirst()
    {
        var source = Source(path => ServiceOf(path) switch
        {
            "orders-api" => (HttpStatusCode.OK, Data(Trace("t-a", "orders-api", "orders:create", "ok", startMicros: 1500000000000000))),
            "billing-api" => (HttpStatusCode.OK, Data(Trace("t-b", "billing-api", "billing:charge", "not-found", startMicros: 1500000100000000))),
            _ => (HttpStatusCode.OK, Data())
        }, TwoServices, out _);

        var flows = await source.GetRecentFlowsAsync(20);

        Assert.Equal(2, flows.Count);
        Assert.Equal("t-b", flows[0].TraceId);              // newest first
        Assert.True(flows[0].Failed);                       // status not-found → not the success class
        Assert.Equal(1, flows[0].Events);                   // Jaeger returns full traces → real span count
        Assert.Equal("billing-api", Assert.Single(flows[0].Services));
        Assert.Equal("billing:charge", flows[0].Topic);     // the flow's entry topic (earliest event's)
        Assert.Equal("t-a", flows[1].TraceId);
        Assert.False(flows[1].Failed);                      // status ok
    }

    [Fact]
    public async Task GetRecentFlowsAsync_DiscoversServices_WhenNoneConfigured()
    {
        var source = Source(path =>
        {
            if (path.StartsWith("/api/services")) return (HttpStatusCode.OK, """{ "data": [ "orders-api" ] }""");
            return ServiceOf(path) == "orders-api"
                ? (HttpStatusCode.OK, Data(Trace("t-a", "orders-api", "orders:create", "ok")))
                : (HttpStatusCode.OK, Data());
        }, services: null, out var handler);

        var flows = await source.GetRecentFlowsAsync(20);

        Assert.Equal(1, handler.ServiceListCalls);          // discovery was used
        Assert.Equal("t-a", Assert.Single(flows).TraceId);
    }

    // #79: every per-service search request blocks until ALL of them have arrived, then all unblock
    // together. This is only satisfiable if the fan-out issues the requests concurrently (BoundedFanOut /
    // Task.WhenAll) - a sequential `foreach await` would issue the second request only after the first
    // completes, which never happens here, so a regression to sequential deadlocks this test instead of
    // passing it. The outer Task.WhenAny/Delay is a deadlock guard, not a timing assertion - a passing run
    // is fast regardless of machine load; only a genuine sequential regression makes it hang.
    private sealed class GatedConcurrencyHandler : HttpMessageHandler
    {
        private readonly int _expectedConcurrentRequests;
        private int _arrived;
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedConcurrencyHandler(int expectedConcurrentRequests) => _expectedConcurrentRequests = expectedConcurrentRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/services"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "data": [] }""") };
            }

            if (Interlocked.Increment(ref _arrived) == _expectedConcurrentRequests)
            {
                _allArrived.TrySetResult();
            }

            await _allArrived.Task; // only resolves once every expected request has arrived

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Data()) };
        }
    }

    [Fact]
    public async Task GetRecentFlowsAsync_QueriesServicesConcurrently_NotSequentially()
    {
        var services = new[] { "svc-a", "svc-b", "svc-c" };
        var handler = new GatedConcurrencyHandler(services.Length);
        var options = new JaegerTraceSourceOptions(JaegerUrl) { Services = services }; // SearchConcurrency default (8) >= 3
        var source = new JaegerTraceSource(new HttpClient(handler), options);

        var task = source.GetRecentFlowsAsync(20);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(task, completed); // did not deadlock - all 3 requests were in flight together
        Assert.Empty(await task);
    }

    // A minimal ILogger that records formatted messages, for asserting on the failed-service warning log
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

    // #189: a faulted per-service search must not discard the other services' already-fetched results via
    // Task.WhenAll's fault semantics. Before the fix, billing-api's connection failure propagated out of
    // BoundedFanOut.WhenAllAsync and faulted the whole fan-out, losing orders-api's successful result too.
    [Fact]
    public async Task GetCorrelationAsync_IsolatesAFaultedServiceSearch_AndReturnsTheHealthyServicesResults()
    {
        const string correlationId = "ticket-1";
        var source = Source(path =>
        {
            Assert.Contains("benzene.correlation-id", Uri.UnescapeDataString(path));
            return ServiceOf(path) switch
            {
                "orders-api" => (HttpStatusCode.OK, Data(Trace("t-a", "orders-api", "orders:create", "ok", correlationId))),
                "billing-api" => throw new HttpRequestException("simulated transient connection failure"),
                _ => (HttpStatusCode.OK, Data())
            };
        }, TwoServices, out _);

        var view = await source.GetCorrelationAsync(correlationId);

        Assert.NotNull(view);
        Assert.Equal("t-a", Assert.Single(view!.Traces).TraceId); // orders-api's result survived billing-api's failure
    }

    // #252: a per-service HttpClient-level timeout throws TaskCanceledException (an OperationCanceledException
    // subclass) completely independent of the caller's own cancellationToken - the isolation catch must not
    // treat that the same as genuine host cancellation and let it fault the whole fan-out.
    [Fact]
    public async Task GetCorrelationAsync_IsolatesAPerServiceTimeout_NotTiedToTheCallersToken()
    {
        const string correlationId = "ticket-1";
        var source = Source(path =>
        {
            Assert.Contains("benzene.correlation-id", Uri.UnescapeDataString(path));
            return ServiceOf(path) switch
            {
                "orders-api" => (HttpStatusCode.OK, Data(Trace("t-a", "orders-api", "orders:create", "ok", correlationId))),
                "billing-api" => throw new TaskCanceledException(
                    "simulated HttpClient.Timeout for this one service, unrelated to the caller's token"),
                _ => (HttpStatusCode.OK, Data())
            };
        }, TwoServices, out _);

        // The caller's own cancellationToken is CancellationToken.None throughout - nothing the caller did
        // requested cancellation, so billing-api's per-request timeout must be isolated exactly like the
        // adjacent HttpRequestException test, not treated as genuine host cancellation.
        var view = await source.GetCorrelationAsync(correlationId);

        Assert.NotNull(view);
        Assert.Equal("t-a", Assert.Single(view!.Traces).TraceId); // orders-api's result survived billing-api's timeout
    }

    // The complement of the above: when the CALLER's own token really is cancelled, the exception must
    // propagate rather than being isolated away - matching MessageHandler.cs's token-verified convention.
    // The token is cancelled from WITHIN billing-api's handler (not before the call starts) so this
    // exercises the per-service catch's `when` filter directly, rather than a semaphore/gate short-circuit.
    [Fact]
    public async Task GetCorrelationAsync_PropagatesGenuineCancellation_WhenTheCallersOwnTokenIsCancelled()
    {
        const string correlationId = "ticket-1";
        using var cts = new CancellationTokenSource();
        var source = Source(path => ServiceOf(path) switch
        {
            "orders-api" => (HttpStatusCode.OK, Data(Trace("t-a", "orders-api", "orders:create", "ok", correlationId))),
            "billing-api" => throw SimulateGenuineHostCancellation(cts),
            _ => (HttpStatusCode.OK, Data())
        }, TwoServices, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetCorrelationAsync(correlationId, null, cts.Token));
    }

    // Cancels the ambient token AND throws the exception the fetch would see once it observes that
    // cancellation - i.e. a genuine host shutdown/drain, not an unrelated per-request timeout.
    private static Exception SimulateGenuineHostCancellation(CancellationTokenSource cts)
    {
        cts.Cancel();
        return new TaskCanceledException("simulated genuine host cancellation", null, cts.Token);
    }

    [Fact]
    public async Task GetCorrelationAsync_LogsTheFailedService_WhenIsolatingAFaultedSearch()
    {
        const string correlationId = "ticket-1";
        var handler = new RoutingHandler(path => ServiceOf(path) switch
        {
            "orders-api" => (HttpStatusCode.OK, Data(Trace("t-a", "orders-api", "orders:create", "ok", correlationId))),
            "billing-api" => throw new HttpRequestException("simulated transient connection failure"),
            _ => (HttpStatusCode.OK, Data())
        });
        var logger = new RecordingLogger();
        var options = new JaegerTraceSourceOptions(JaegerUrl) { Services = TwoServices };
        var source = new JaegerTraceSource(new HttpClient(handler), options, logger);

        await source.GetCorrelationAsync(correlationId);

        Assert.Contains(logger.Messages, m => m.Contains("billing-api"));
    }
}
