using System.Globalization;
using System.Text.Json;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;
using Benzene.Http.Routing;
using Benzene.Mesh.Aggregator;
using Microsoft.Extensions.Logging;

namespace Benzene.Mesh.Artifacts;

/// <summary>
/// Guards the HTTP endpoint that triggers a mesh discovery/aggregation pass. A pass is expensive - it
/// enumerates the platform's services, interrogates every one of them, and rewrites the whole catalog -
/// so the endpoint needs more in front of it than authentication alone. This adds two checks, in this
/// order, and short-circuits on either:
/// <list type="number">
/// <item><description>
/// <b>CSRF</b> - the request must carry the custom header <see cref="MeshRefreshGuardOptions.HeaderName"/>
/// (<c>X-Benzene-Refresh</c>). A cross-site <c>&lt;form method="post"&gt;</c> cannot set a custom header at
/// all, and a cross-origin <c>fetch()</c> that sets one triggers a CORS preflight this pipeline never
/// approves - so only a genuine same-origin caller gets through. Missing header: <c>403</c>, generic body,
/// <b>no I/O of any kind</b>. This deliberately runs before the throttle's store read, so an attacker
/// who can't set the header can neither trigger a pass nor learn anything about the catalog's state.
/// </description></item>
/// <item><description>
/// <b>Throttle</b> - the last pass's <c>generatedAtUtc</c> is read back from the artifact store's
/// <c>manifest.json</c>; if it is more recent than <see cref="MeshRefreshGuardOptions.MinimumInterval"/>
/// the request is answered <c>429</c> (with <c>Retry-After</c>) and the pass is skipped.
/// </description></item>
/// </list>
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
/// <remarks>
/// <para>
/// <b>The throttle is a rate limiter, not a distributed lock - and must not be described as one.</b> It
/// reads a timestamp, decides, and only then runs the pass; two requests that arrive close enough
/// together both read the same stale timestamp and both proceed. What it bounds is <em>sustained</em>
/// abuse (roughly one pass per window over any period longer than the window), which is the cost- and
/// load-shaped threat; it does not guarantee single-flight, so concurrent passes can still race on the
/// same artifact keys. The deliberate trade is that it needs no new infrastructure whatsoever - no lock
/// table, no Redis, no lease - because it reuses state the aggregator already writes on every run. If a
/// deployment genuinely needs single-flight, that is a different mechanism (e.g. an S3 conditional write
/// / <c>If-None-Match</c> lease, or a DynamoDB conditional put), not a smaller number here.
/// </para>
/// <para>
/// <b>It fails open.</b> A missing, unreadable, or unparseable manifest allows the pass. This is
/// required for correctness, not laziness: the very first refresh after a deploy has no manifest to read
/// (and that first pass is exactly what CI triggers), so failing closed would brick a fresh deployment.
/// The security cost is bounded and worth naming: an attacker who can delete or corrupt
/// <c>manifest.json</c> disables the throttle - but that attacker already has write access to the mesh's
/// artifact store, which is a strictly worse compromise than an unthrottled refresh. Edge-level
/// throttling (API Gateway rate/burst limits) is what still bounds cost in that case.
/// </para>
/// <para>
/// <b>Matching.</b> A request is guarded when its path normalizes to
/// <see cref="MeshRefreshGuardOptions.Path"/> <em>or</em> the registered
/// <see cref="IRouteFinder"/> resolves it to <see cref="MeshRefreshGuardOptions.Topic"/>. The first
/// applies <see cref="MeshPathCanonicalizer.Canonicalize"/> - the exact normalization
/// <c>Benzene.Http.Routing.RouteFinder</c> itself applies (query string dropped, empty segments
/// removed, case-insensitive), shared with <see cref="MeshDispatchGuardMiddleware{TContext}"/> and
/// <c>MeshAuthGate</c> so none of the three can silently drift apart on what counts as the same path -
/// so no spelling that routes to the handler -
/// <c>/mesh/refresh/</c>, <c>//mesh//refresh</c>, <c>/MESH/REFRESH</c>, <c>/mesh/refresh?x=1</c> - can
/// slip past the guard while still reaching the handler. The second closes the class of bypass the first
/// cannot see: a second <c>[HttpEndpoint]</c> alias on the same handler, or a versioned route alias
/// added by <c>AddHttpVersioning()</c>, both of which reach the same topic by a different path. Matching
/// is deliberately <b>not</b> restricted to POST: the router only maps POST today, but guarding every
/// method means adding a GET alias later cannot silently open a CSRF hole (a top-level GET navigation is
/// the one cross-site request <c>SameSite=Lax</c> still sends cookies on).
/// </para>
/// </remarks>
public class MeshRefreshGuardMiddleware<TContext> : IMiddleware<TContext> where TContext : IHttpContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly MeshRefreshGuardOptions _options;
    private readonly string _guardedPath;
    private readonly IMeshArtifactStore _store;
    private readonly IHttpRequestAdapter<TContext> _requestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly IRouteFinder? _routeFinder;
    private readonly ILogger? _logger;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Initializes a new instance of the <see cref="MeshRefreshGuardMiddleware{TContext}"/> class.</summary>
    /// <param name="options">The guard's configuration.</param>
    /// <param name="store">The artifact store the last pass's <c>manifest.json</c> timestamp is read from.</param>
    /// <param name="requestAdapter">Adapter used to read the request method, path, and headers.</param>
    /// <param name="responseAdapter">Adapter used to write the 403/429 responses.</param>
    /// <param name="routeFinder">
    /// Optional. When available, a request the router resolves to <see cref="MeshRefreshGuardOptions.Topic"/>
    /// is guarded even if its path isn't <see cref="MeshRefreshGuardOptions.Path"/> (route aliases,
    /// version-prefixed routes). Null simply narrows matching to the path.
    /// </param>
    /// <param name="logger">Optional logger; denials are logged server-side only, never echoed to the caller.</param>
    /// <param name="clock">Optional clock, for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public MeshRefreshGuardMiddleware(
        MeshRefreshGuardOptions options,
        IMeshArtifactStore store,
        IHttpRequestAdapter<TContext> requestAdapter,
        IBenzeneResponseAdapter<TContext> responseAdapter,
        IRouteFinder? routeFinder = null,
        ILogger? logger = null,
        Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _guardedPath = MeshPathCanonicalizer.Canonicalize(options.Path);
        _store = store;
        _requestAdapter = requestAdapter;
        _responseAdapter = responseAdapter;
        _routeFinder = routeFinder;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Gets the name of the middleware.</summary>
    public string Name => "MeshRefreshGuard";

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _requestAdapter.Map(context);

        if (!IsGuarded(request))
        {
            await next();
            return;
        }

        // Check 1 - CSRF. Cheapest possible check, zero I/O, and first: nothing below this line runs
        // for a caller that couldn't set a custom header.
        if (!HasHeader(request, _options.HeaderName))
        {
            _logger?.LogWarning("Mesh refresh refused: required header {header} was absent", _options.HeaderName);
            await DenyAsync(context, "403", "forbidden");
            return;
        }

        // Check 2 - throttle. One small read of an artifact the mesh already publishes.
        if (_options.MinimumInterval > TimeSpan.Zero)
        {
            var lastPassUtc = await TryReadLastPassAsync();
            if (lastPassUtc.HasValue)
            {
                var elapsed = _clock() - lastPassUtc.Value;
                // A manifest timestamped in the future (clock skew, or a hand-edited artifact) yields a
                // negative elapsed, which is < MinimumInterval and so throttles. That is the safe
                // direction: it declines work rather than granting an unbounded allowance.
                if (elapsed < _options.MinimumInterval)
                {
                    var retryAfter = Math.Max(1, (int)Math.Ceiling((_options.MinimumInterval - elapsed).TotalSeconds));
                    _logger?.LogInformation(
                        "Mesh refresh throttled: last pass was {elapsed} ago, minimum interval is {interval}",
                        elapsed, _options.MinimumInterval);
                    _responseAdapter.SetResponseHeader(context, "Retry-After",
                        retryAfter.ToString(CultureInfo.InvariantCulture));
                    await DenyAsync(context, "429", "throttled");
                    return;
                }
            }
        }

        await next();
    }

    private bool IsGuarded(HttpRequest request)
    {
        if (MeshPathCanonicalizer.Canonicalize(request.Path) == _guardedPath)
        {
            return true;
        }

        if (string.IsNullOrEmpty(_options.Topic) || _routeFinder == null)
        {
            return false;
        }

        var topic = _routeFinder.Find(request.Method ?? string.Empty, request.Path ?? string.Empty)?.Topic;
        return topic != null && string.Equals(topic, _options.Topic, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the last pass's timestamp from the manifest. Returns null - meaning "allow" - for every
    /// failure mode (absent, unreadable, not JSON, no <c>generatedAtUtc</c>); see the type's remarks on
    /// why this fails open.
    /// </summary>
    private async Task<DateTimeOffset?> TryReadLastPassAsync()
    {
        try
        {
            var manifest = await _store.TryReadAsync(_options.ManifestKey);
            if (string.IsNullOrWhiteSpace(manifest))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ManifestTimestamp>(manifest, JsonOptions)?.GeneratedAtUtc;
        }
        catch (Exception exception)
        {
            // Never let a store hiccup turn into a 500 on the refresh endpoint - log it and allow.
            _logger?.LogWarning(exception,
                "Mesh refresh throttle could not read {key}; allowing the pass", _options.ManifestKey);
            return null;
        }
    }

    /// <summary>
    /// Case-insensitive header lookup done by scan rather than via <c>HttpRequest.AsLowerCase()</c>: that
    /// helper rebuilds the whole header dictionary (and throws on two header names differing only in
    /// case), neither of which this one-key check should risk.
    /// </summary>
    private static bool HasHeader(HttpRequest request, string name)
    {
        if (request.Headers == null)
        {
            return false;
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(header.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes a denial. Both denials share one minimal, fixed body shape (matching
    /// <c>Benzene.Mesh.Auth.Oidc</c>'s "no detail leakage" convention): no reason text, no timings, no
    /// hint about whether a catalog exists.
    /// </summary>
    private async Task DenyAsync(TContext context, string statusCode, string error)
    {
        _responseAdapter.SetStatusCode(context, statusCode);
        _responseAdapter.SetContentType(context, "application/json");
        _responseAdapter.SetBody(context, "{\"error\":\"" + error + "\"}");
        await _responseAdapter.FinalizeAsync(context);
    }

    /// <summary>
    /// The one field of <c>manifest.json</c> this guard reads. A dedicated shape rather than
    /// <c>MeshManifest</c> so the throttle never has to deserialize the whole service array (and can't
    /// be made to fail by an entry it doesn't understand).
    /// </summary>
    private sealed class ManifestTimestamp
    {
        public DateTimeOffset? GeneratedAtUtc { get; set; }
    }
}
