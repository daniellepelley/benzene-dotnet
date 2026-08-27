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

    /// <summary>How many traces a <c>mesh:query:correlation</c> search matches before per-trace fetching
    /// stops (the <c>limit</c> passed to <c>/api/search</c>). Default 100, preserving the source's original
    /// hardcoded behavior. Unlike X-Ray's hard pagination cap (<c>XRayTraceSourceOptions</c>'s paging
    /// bound), this is Tempo's own search-result limit — there is no further paging beyond it — so hitting
    /// it means the search may have missed matches, not merely truncated a page; a warning is logged
    /// (<see cref="TempoTraceSource"/>'s optional <c>ILogger</c>) whenever the search returns exactly this
    /// many matches, mirroring X-Ray's #77 at-limit warning.</summary>
    public int CorrelationSearchLimit { get; init; } = 100;

    /// <summary>How many per-trace fetches (<c>GET /api/traces/{id}</c>) <see cref="TempoTraceSource"/> runs
    /// concurrently when hydrating a correlation search's matched trace ids. Default 8, matching
    /// <c>JaegerTraceSourceOptions.SearchConcurrency</c>'s default and rationale — high enough that a
    /// typical correlation match set (a handful to a few dozen traces) still hydrates in roughly one
    /// request's worth of latency, low enough not to open one HTTP request per match simultaneously against
    /// a shared Tempo query-frontend. Set to 0 or a negative value for unbounded (every trace fetched at
    /// once).</summary>
    public int SearchConcurrency { get; init; } = 8;
}
