using System.Globalization;
using Amazon.XRay;
using Amazon.XRay.Model;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Wire;
using Microsoft.Extensions.Logging;
// Both namespaces declare a TraceSummary; the alias resolves the bare name to the mesh one (what this
// adapter produces) while X-Ray's own summary type stays reachable by its full name.
using TraceSummary = Benzene.Mesh.Collector.TraceSummary;

namespace Benzene.Mesh.Fleet.Aws.XRay;

/// <summary>
/// An <see cref="IMeshTraceSource"/> that answers <c>mesh:query:trace</c> and
/// <c>mesh:query:correlation</c> from AWS X-Ray: it fetches a trace's segments with
/// <c>BatchGetTraces</c> and maps the topic-bearing spans into a <see cref="TraceView"/> (see
/// <see cref="XRaySegmentMapper"/>), and finds a business correlation id's flows with
/// <c>GetTraceSummaries</c> filtered on the correlation-id annotation. This is the AWS realisation of the
/// trace-backed fleet reader scoped in <c>work/otel-fleet-adapter-scope.md</c> - the fleet UI's trace
/// waterfall and correlation triage over an existing observability backend, no push collector required.
/// </summary>
/// <remarks>
/// Trace stats and service health are deliberately not sourced here (see <see cref="IMeshTraceSource"/>);
/// X-Ray traces are sampled, so counts would be biased, and X-Ray has no heartbeat feed. Those compose
/// from an <c>IMeshUsageSource</c> (CloudWatch) and the heartbeat plane in later increments.
/// </remarks>
public class XRayTraceSource : IMeshTraceSource
{
    // X-Ray's BatchGetTraces accepts at most 5 trace ids per call.
    private const int BatchGetTracesMax = 5;

    /// <summary>Conservative upper bound on the time range passed to a single <c>GetTraceSummaries</c>
    /// call. AWS does not publish a fixed per-call time-range cap the way it does for
    /// <c>BatchGetTraces</c>' 5-id batch limit, but a window much wider than this is commonly reported to
    /// degrade (throttling/timeouts) on high-volume accounts. This is a structural safety bound, not a
    /// verified API limit — VERIFY against live AWS docs/account before relying on the exact threshold;
    /// chunking a window that didn't strictly need it costs one extra API call, not a correctness bug, so
    /// this errs generous rather than skip chunking. <see cref="XRayTraceSourceOptions.CorrelationLookback"/>
    /// defaults to 24h, well past this bound, so correlation search always chunks under defaults.</summary>
    private static readonly TimeSpan MaxTraceSummariesWindow = TimeSpan.FromHours(6);

    /// <summary>Hard pagination cap for <see cref="GetRecentFlowsAsync"/>, as a multiple of the requested
    /// <c>limit</c>: paging continues until the window is exhausted (no more <c>NextToken</c>) OR this many
    /// summaries have been collected, whichever comes first. Replaces a prior early-stop heuristic that
    /// assumed <c>GetTraceSummaries</c> pages come back newest-first (unconfirmed) — this bound is honest
    /// regardless of actual page order: paging over a much larger, less-biased sample before picking the
    /// newest N client-side. A generous multiple, not the exact limit, because it only trims pathological
    /// volume, never normal traffic.</summary>
    private const int RecentFlowsHardCapMultiplier = 20;

    private readonly IAmazonXRay _xray;
    private readonly XRayTraceSourceOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Creates the source over an X-Ray client (region/credentials come from the client).</summary>
    public XRayTraceSource(IAmazonXRay xray) : this(xray, new XRayTraceSourceOptions())
    {
    }

    /// <summary>Creates the source over an X-Ray client with explicit tuning (correlation lookback).</summary>
    public XRayTraceSource(IAmazonXRay xray, XRayTraceSourceOptions options, ILogger? logger = null)
    {
        _xray = xray;
        _options = options;
        _logger = logger;
    }

    /// <summary>Fetches the trace's segments from X-Ray and maps its topic-bearing spans into a
    /// <see cref="TraceView"/>, or null when X-Ray has no such trace or it carried no Benzene spans.</summary>
    public async Task<TraceView?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceId))
        {
            return null;
        }

        var events = await FetchEventsAsync(new[] { traceId }, traceId, cancellationToken);

        // A null (rather than empty) view when X-Ray has no such trace, or a real trace that carried no
        // Benzene topic-bearing span - so the query handler answers NotFound, not an empty waterfall.
        return events.Count == 0 ? null : new TraceView { TraceId = traceId, Events = events };
    }

    /// <summary>Finds every X-Ray trace carrying the correlation-id annotation over the configured
    /// lookback window, maps each to a <see cref="TraceView"/>, and groups them into a
    /// <see cref="CorrelationView"/> (traces ordered by earliest start), or null when none matched.</summary>
    public async Task<CorrelationView?> GetCorrelationAsync(string correlationId, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return null;
        }

        var (start, end) = ResolveWindow(range, _options.CorrelationLookback);
        // benzene.correlation-id lands in X-Ray as the underscore-sanitised annotation key; only
        // annotations are filterable (see work/otel-fleet-adapter-scope.md §6b).
        var filter = $"annotation.benzene_correlation_id = \"{Escape(correlationId)}\"";

        // The window is chunked (MaxTraceSummariesWindow) and each chunk paged to exhaustion - a
        // correlation search must not miss matches, so there's no hard cap here (unlike recent-flows).
        var summaries = await FetchTraceSummariesAsync(start, end, filter, hardCap: null, "GetCorrelationAsync", cancellationToken);
        var traceIds = summaries.Select(s => s.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();

        if (traceIds.Count == 0)
        {
            return null;
        }

        var traces = new List<TraceView>();
        foreach (var batch in Chunk(traceIds, BatchGetTracesMax))
        {
            var response = await _xray.BatchGetTracesAsync(
                new BatchGetTracesRequest { TraceIds = batch }, cancellationToken);

            foreach (var trace in response.Traces ?? new List<Trace>())
            {
                if (trace.Segments is not { Count: > 0 } segments || string.IsNullOrEmpty(trace.Id))
                {
                    continue;
                }

                var events = XRaySegmentMapper.Map(trace.Id, segments.Select(s => s.Document));
                if (events.Count > 0)
                {
                    traces.Add(new TraceView { TraceId = trace.Id, Events = events });
                }
            }
        }

        if (traces.Count == 0)
        {
            return null;
        }

        // Earliest-first, the same ordering the in-memory collector's correlation view uses so the UI
        // renders both identically.
        traces.Sort((a, b) => EarliestStart(a).CompareTo(EarliestStart(b)));
        return new CorrelationView { CorrelationId = correlationId, Traces = traces };
    }

    /// <summary>Lists the most recent flows for the fleet view: one <c>GetTraceSummaries</c> over the
    /// recent-flows window (no filter), the top-N (by trace-id epoch) enriched with real span data via a
    /// bounded <c>BatchGetTraces</c> (≤4 calls for 20 rows — see
    /// <see cref="XRayTraceSourceOptions.RecentFlowsServiceEnrichmentMax"/>), mapped to
    /// <see cref="TraceSummary"/> rows and ordered newest-first. Enrichment reads each row's service names
    /// from the pipeline-stamped <c>benzene.service</c> (not X-Ray's <c>ServiceIds</c>, which on Lambda are
    /// infra/handler names), its real millisecond start (not the second-granularity id epoch), and its real
    /// span count. A row whose trace can't be fetched or that carries no Benzene span falls back to the
    /// summary plane per-row (fetch-isolation). Set the option to 0 to skip enrichment entirely.</summary>
    public async Task<IReadOnlyList<TraceSummary>> GetRecentFlowsAsync(
        int limit = 20, MeshTimeRange? range = null, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<TraceSummary>();
        }

        var (start, end) = ResolveWindow(range, _options.RecentFlowsLookback);

        // Page to window exhaustion (chunked - MaxTraceSummariesWindow) or a generous hard cap, whichever
        // comes first - NOT a small early-stop multiple of limit. GetTraceSummaries' page ordering isn't
        // documented/confirmed, so an early stop biased toward whatever order the pages happen to arrive in
        // could silently surface stale traces as "recent" under high volume; this samples over a much wider,
        // order-agnostic set before the client-side newest-first Take(limit) below. A hit on the cap is
        // logged (not silent) since it means the sample may not cover the full requested window.
        var summaries = await FetchTraceSummariesAsync(
            start, end, filter: null, hardCap: limit * RecentFlowsHardCapMultiplier, "GetRecentFlowsAsync", cancellationToken);

        // Select the newest N by the trace-id epoch (second-granularity, but enough to pick the right ~20),
        // then enrich those rows below. Ordering within a second is refined by the enriched millisecond
        // start; the final sort applies a stable trace-id tiebreaker so both planes are deterministic.
        var top = summaries
            .OrderByDescending(s => ParseTraceStart(s.Id))
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var rows = await EnrichRecentFlowsAsync(top, limit, cancellationToken);

        return rows
            .OrderByDescending(t => t.StartedAt)
            .ThenByDescending(t => t.TraceId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Enrich the chosen summaries with real span data by batching <see cref="BatchGetTracesMax"/>
    /// ids per <c>BatchGetTraces</c> call (parallel, per-batch fetch-isolation). A trace that maps to Benzene
    /// events becomes an enriched row (benzene.service names, ms start, real span count); a trace with no
    /// Benzene span or a failed batch keeps its summary-plane row. Skips all fetches when enrichment is off
    /// (<see cref="XRayTraceSourceOptions.RecentFlowsServiceEnrichmentMax"/> = 0).</summary>
    private async Task<List<TraceSummary>> EnrichRecentFlowsAsync(
        IReadOnlyList<Amazon.XRay.Model.TraceSummary> summaries, int limit, CancellationToken cancellationToken)
    {
        var enrichMax = Math.Min(Math.Max(_options.RecentFlowsServiceEnrichmentMax, 0), limit);
        if (enrichMax == 0)
        {
            return summaries.Select(ToSummaryPlaneRow).ToList();
        }

        // Which trace ids to enrich (the first enrichMax that have an id); the rest stay summary-plane.
        var toEnrich = summaries
            .Where(s => !string.IsNullOrEmpty(s.Id))
            .Take(enrichMax)
            .Select(s => s.Id!)
            .ToList();

        var mapped = new Dictionary<string, List<MeshTraceEvent>>(StringComparer.Ordinal);
        var batches = await Task.WhenAll(Chunk(toEnrich, BatchGetTracesMax).Select(FetchBatchAsync));
        foreach (var batch in batches)
        {
            foreach (var kvp in batch)
            {
                mapped[kvp.Key] = kvp.Value;
            }
        }

        // Enriched row where the trace mapped to at least one Benzene event; summary-plane fallback otherwise.
        return summaries
            .Select(s => s.Id is { } id && mapped.TryGetValue(id, out var events) && events.Count > 0
                ? EnrichedRow(s, events)
                : ToSummaryPlaneRow(s))
            .ToList();

        async Task<Dictionary<string, List<MeshTraceEvent>>> FetchBatchAsync(List<string> ids)
        {
            var result = new Dictionary<string, List<MeshTraceEvent>>(StringComparer.Ordinal);
            try
            {
                var response = await _xray.BatchGetTracesAsync(
                    new BatchGetTracesRequest { TraceIds = ids }, cancellationToken);

                foreach (var trace in response.Traces ?? new List<Trace>())
                {
                    if (trace.Segments is not { Count: > 0 } segments || string.IsNullOrEmpty(trace.Id))
                    {
                        continue;
                    }

                    result[trace.Id] = XRaySegmentMapper.Map(trace.Id, segments.Select(s => s.Document));
                }
            }
            catch
            {
                // Fetch isolation: a failed batch leaves its ≤5 rows on the summary plane, never the whole list.
            }

            return result;
        }
    }

    /// <summary>An enriched recent-flows row: real service names (benzene.service, from the mapped events),
    /// the earliest event's millisecond start, and the real span count. Keeps the summary's authoritative
    /// error flag (<c>HasError</c>/<c>HasFault</c>) and duration.</summary>
    private static TraceSummary EnrichedRow(Amazon.XRay.Model.TraceSummary summary, List<MeshTraceEvent> events) => new()
    {
        TraceId = summary.Id ?? string.Empty,
        // X-Ray Duration is in seconds; the mesh carries milliseconds.
        DurationMs = summary.Duration * 1000,
        Failed = summary.HasError || summary.HasFault,
        Services = events
            .Select(e => e.Service)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList(),
        StartedAt = events.Min(e => e.StartedAt),
        Events = events.Count,
        // The flow's entry topic: the earliest mapped event's (Map returns events in start order).
        Topic = events[0].Topic
    };

    private static TraceSummary ToSummaryPlaneRow(Amazon.XRay.Model.TraceSummary summary) => new()
    {
        TraceId = summary.Id ?? string.Empty,
        // X-Ray Duration is in seconds; the mesh carries milliseconds.
        DurationMs = summary.Duration * 1000,
        Failed = summary.HasError || summary.HasFault,
        Services = summary.ServiceIds?
            .Select(s => s.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct()
            .ToList() ?? new List<string>(),
        StartedAt = ParseTraceStart(summary.Id),
        Events = 0 // X-Ray summaries carry no span count; the drill-in trace has it.
    };

    /// <summary>An X-Ray trace id is <c>1-{8 hex epoch seconds}-{24 hex}</c>; the middle group is the
    /// trace's start time. Falls back to <see cref="DateTimeOffset.MinValue"/> for an unparseable id.</summary>
    private static DateTimeOffset ParseTraceStart(string? traceId)
    {
        if (traceId is not null)
        {
            var parts = traceId.Split('-');
            if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var epoch))
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
        }

        return DateTimeOffset.MinValue;
    }

    /// <summary>Fetch the given trace ids from X-Ray and map the returned segments to events under one
    /// mesh trace id (a single trace's lookup).</summary>
    private async Task<List<MeshTraceEvent>> FetchEventsAsync(
        IReadOnlyList<string> traceIds, string meshTraceId, CancellationToken cancellationToken)
    {
        var response = await _xray.BatchGetTracesAsync(
            new BatchGetTracesRequest { TraceIds = traceIds.ToList() }, cancellationToken);

        // X-Ray returns an unknown id in UnprocessedTraceIds (not Traces), so an unknown trace yields no
        // segments here → an empty event list → a null view upstream.
        var segments = (response.Traces ?? new List<Trace>())
            .Where(t => t.Segments is { Count: > 0 })
            .SelectMany(t => t.Segments)
            .Select(s => s.Document);

        return XRaySegmentMapper.Map(meshTraceId, segments);
    }

    /// <summary>Resolve a requested <see cref="MeshTimeRange"/> to X-Ray's <c>[start,end]</c> DateTimes,
    /// falling back to <c>now - <paramref name="fallback"/></c> .. <c>now</c> when no window was requested
    /// (today's behavior). X-Ray's <c>GetTraceSummaries</c> needs a bounded range either way.</summary>
    private static (DateTime Start, DateTime End) ResolveWindow(MeshTimeRange? range, TimeSpan fallback)
    {
        var resolved = MeshTimeRangeResolver.Resolve(range, DateTimeOffset.UtcNow);
        if (resolved != null)
        {
            return (resolved.Value.From.UtcDateTime, resolved.Value.To.UtcDateTime);
        }

        var end = DateTime.UtcNow;
        return (end - fallback, end);
    }

    /// <summary>Runs <c>GetTraceSummaries</c> over <c>[start,end]</c>, chunking the window into
    /// <see cref="MaxTraceSummariesWindow"/>-sized sub-queries (each paged via <c>NextToken</c> before
    /// moving to the next chunk) so no single call is asked to scan a window wider than the conservative
    /// bound - mirroring the chunking <see cref="BatchGetTracesMax"/> already applies to id batches, just
    /// on the time axis instead of the id axis. When <paramref name="hardCap"/> is set, paging stops early
    /// once that many summaries have been collected (a truncation is logged, never silent) rather than
    /// exhausting every chunk; null means "always exhaust the full window" (correlation search must not
    /// miss a match).</summary>
    private async Task<List<Amazon.XRay.Model.TraceSummary>> FetchTraceSummariesAsync(
        DateTime start, DateTime end, string? filter, int? hardCap, string operation, CancellationToken cancellationToken)
    {
        var all = new List<Amazon.XRay.Model.TraceSummary>();
        var chunks = ChunkWindow(start, end, MaxTraceSummariesWindow).ToList();

        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            var (chunkStart, chunkEnd) = chunks[chunkIndex];
            string? nextToken = null;
            do
            {
                var response = await _xray.GetTraceSummariesAsync(new GetTraceSummariesRequest
                {
                    StartTime = chunkStart,
                    EndTime = chunkEnd,
                    FilterExpression = filter,
                    NextToken = nextToken
                }, cancellationToken);

                if (response.TraceSummaries != null)
                {
                    all.AddRange(response.TraceSummaries);
                }

                nextToken = string.IsNullOrEmpty(response.NextToken) ? null : response.NextToken;

                if (hardCap.HasValue && all.Count >= hardCap.Value)
                {
                    var moreRemaining = nextToken != null || chunkIndex < chunks.Count - 1;
                    if (moreRemaining)
                    {
                        _logger?.LogWarning(
                            "XRayTraceSource.{Operation} stopped at its hard pagination cap ({Cap} rows) with more GetTraceSummaries pages available; the result may not cover the full requested window.",
                            operation, hardCap.Value);
                    }

                    return all;
                }
            }
            while (nextToken != null);
        }

        return all;
    }

    /// <summary>Splits <c>[start,end]</c> into sequential sub-windows of at most <paramref name="maxSpan"/>,
    /// covering the whole range with no gaps or overlaps. Yields the whole range unchanged (as one chunk)
    /// when it already fits, or when <paramref name="end"/> doesn't come after <paramref name="start"/>.</summary>
    private static IEnumerable<(DateTime Start, DateTime End)> ChunkWindow(DateTime start, DateTime end, TimeSpan maxSpan)
    {
        if (end <= start)
        {
            yield return (start, end);
            yield break;
        }

        var chunkStart = start;
        while (chunkStart < end)
        {
            var chunkEnd = chunkStart + maxSpan < end ? chunkStart + maxSpan : end;
            yield return (chunkStart, chunkEnd);
            chunkStart = chunkEnd;
        }
    }

    private static DateTimeOffset EarliestStart(TraceView trace)
        => trace.Events.Count == 0 ? DateTimeOffset.MaxValue : trace.Events.Min(e => e.StartedAt);

    // X-Ray filter-expression string literals are double-quoted; escape backslashes and quotes so a
    // correlation id can't break out of the literal.
    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.Skip(i).Take(size).ToList();
        }
    }
}
