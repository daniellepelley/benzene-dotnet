using Benzene.Mesh.Aggregator;

namespace Benzene.Mesh.Artifacts;

/// <summary>
/// Configuration for <see cref="MeshRefreshGuardMiddleware{TContext}"/> — the CSRF + throttle gate in
/// front of the HTTP endpoint that triggers a mesh discovery/aggregation pass.
/// </summary>
/// <remarks>
/// Every value has a working default, so the common case is <c>UseMeshRefreshGuard()</c> with no
/// arguments. The two a host is likely to set are <see cref="Path"/> (if its refresh route isn't the
/// default) and <see cref="MinimumInterval"/> (from configuration - see
/// <c>examples/AwsMesh/Mesh/Startup.cs</c>, which reads it from <c>MESH_REFRESH_MIN_INTERVAL_SECONDS</c>).
/// </remarks>
public class MeshRefreshGuardOptions
{
    /// <summary>
    /// The custom request header a refresh POST must carry (<c>X-Benzene-Refresh</c>). This is a fixed
    /// contract with the mesh UI, which sends it as <c>X-Benzene-Refresh: 1</c>; renaming it here
    /// without also changing the UI silently breaks the Refresh control.
    /// </summary>
    public const string DefaultHeaderName = "X-Benzene-Refresh";

    /// <summary>The default refresh path guarded (<c>/mesh/refresh</c>).</summary>
    public const string DefaultPath = "/mesh/refresh";

    /// <summary>
    /// The default minimum gap between two passes (30 seconds) - long enough to stop a held-down
    /// button or a stuck retry loop from driving a pass per request, short enough that a human who
    /// just changed something and clicks Refresh isn't made to wait.
    /// </summary>
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The path the refresh endpoint is served from. Matched with the exact same normalization
    /// <c>Benzene.Http.Routing.RouteFinder</c> applies (query string dropped, empty segments removed,
    /// case-insensitive), so every spelling that routes to the handler also hits this guard - see
    /// <see cref="MeshRefreshGuardMiddleware{TContext}"/>'s remarks.
    /// </summary>
    public string Path { get; set; } = DefaultPath;

    /// <summary>
    /// The topic the refresh handler is registered under. Used as a second, route-shape-independent way
    /// to recognise a refresh request (see <see cref="MeshRefreshGuardMiddleware{TContext}"/>'s remarks
    /// on why matching on the path alone is not enough once HTTP versioning or a second
    /// <c>[HttpEndpoint]</c> alias is in play). Set to null to match on <see cref="Path"/> only.
    /// </summary>
    public string? Topic { get; set; } = MeshAggregatorTopics.Aggregate;

    /// <summary>
    /// The header name required on a refresh POST. Defaults to <see cref="DefaultHeaderName"/>; the
    /// value sent is not inspected (any non-empty value passes), because what defends against CSRF is
    /// that a cross-site caller *cannot set a custom header at all* - not the value it would have set.
    /// </summary>
    public string HeaderName { get; set; } = DefaultHeaderName;

    /// <summary>
    /// The minimum time that must have elapsed since the last completed pass before another one is
    /// allowed. A request inside the window is answered <c>429</c> without running the pass. Defaults to
    /// <see cref="DefaultMinimumInterval"/>; set to <see cref="TimeSpan.Zero"/> to disable throttling
    /// entirely (e.g. in a test, or a deployment that bounds the endpoint some other way).
    /// </summary>
    public TimeSpan MinimumInterval { get; set; } = DefaultMinimumInterval;

    /// <summary>
    /// The artifact key the last pass's timestamp is read from - the aggregator's own
    /// <c>manifest.json</c>, whose <c>generatedAtUtc</c> field is written on every successful pass.
    /// Reusing it is what lets this throttle need no new infrastructure at all (no DynamoDB, no Redis):
    /// the state it reads is state the mesh already maintains.
    /// </summary>
    public string ManifestKey { get; set; } = "manifest.json";
}
