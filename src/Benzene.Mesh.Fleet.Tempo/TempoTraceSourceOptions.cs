namespace Benzene.Mesh.Fleet.Tempo;

/// <summary>
/// Where and over what windows <see cref="TempoTraceSource"/> queries Grafana Tempo's trace API. A
/// trace lookup is by id (no window); Tempo's search API (<c>GET /api/search</c>) needs a time range,
/// so correlation and recent-flows scan a bounded window.
/// </summary>
public class TempoTraceSourceOptions
{
    /// <summary>Creates the options for a Tempo base URL (e.g. <c>http://tempo:3200</c>).</summary>
    /// <param name="tempoUrl">Tempo's HTTP API base URL, without a trailing slash.</param>
    public TempoTraceSourceOptions(string tempoUrl)
    {
        TempoUrl = tempoUrl.TrimEnd('/');
    }

    /// <summary>Tempo's HTTP API base URL (the query-frontend), e.g. <c>http://tempo:3200</c>.</summary>
    public string TempoUrl { get; }

    /// <summary>How far back a <c>mesh:query:correlation</c> search scans Tempo. Default 24 hours — a
    /// business correlation id (a ticket/log id) is typically chased soon after the event.</summary>
    public TimeSpan CorrelationLookback { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How far back the fleet view's recent-flows search scans Tempo. Default 1 hour — the fleet
    /// view wants the latest activity, a shorter horizon than a correlation chase.</summary>
    public TimeSpan RecentFlowsLookback { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The <c>limit</c> passed to <c>GET /api/search</c> for a <c>mesh:query:correlation</c>
    /// search — the max matching traces Tempo returns before per-trace fetch. Default 100 (preserves the
    /// prior hardcoded behavior). A search that hits this limit logs a warning (the result may not cover
    /// every matching trace) rather than truncating silently — the same posture as Jaeger's
    /// <c>SearchLimitPerService</c> and X-Ray's hard-pagination-cap warning.</summary>
    public int SearchLimit { get; init; } = 100;

    /// <summary>How many per-trace <c>GET /api/traces/{id}</c> fetches a <c>mesh:query:correlation</c>
    /// search runs concurrently once <c>/api/search</c> has returned the matching trace ids. Default 8 —
    /// matches Jaeger's own <c>SearchConcurrency</c> default: high enough that a typical correlation match
    /// set still fetches in roughly one request's worth of latency, low enough not to open one HTTP
    /// request per matched trace simultaneously against a shared Tempo query-frontend. Set to 0 or a
    /// negative value for unbounded (every trace fetched at once), or a higher value for a
    /// higher-throughput Tempo deployment.</summary>
    public int SearchConcurrency { get; init; } = 8;
}
