using System.Globalization;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;
using Benzene.Http.Routing;
using Microsoft.Extensions.Logging;

using Benzene.Mesh.Dispatch;

namespace Benzene.Mesh.Artifacts;

/// <summary>
/// Guards the HTTP endpoint that dispatches a caller-supplied payload into a service's real handler.
/// </summary>
/// <remarks>
/// Lives here rather than in <c>Benzene.Mesh.Dispatch</c> for the same reason the refresh guard lives
/// here rather than in the aggregator: this is an HTTP concern about a mesh endpoint, and the package
/// it protects is transport-agnostic. Dispatch keeps its options, limiter and identity — none of which
/// know what HTTP is — and only the middleware needs the web.
/// </remarks>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
/// <remarks>
/// <para>
/// Deliberately shaped like <c>MeshRefreshGuardMiddleware</c>, because it guards the same kind of
/// thing: a state-changing POST sitting behind a session cookie. The checks run in this order and
/// short-circuit on each — cheapest and most certain first, so a caller who fails the header check
/// costs a string comparison rather than a parse:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>CSRF</b> — the request must carry <see cref="MeshDispatchGuardOptions.HeaderName"/>. A
/// cross-site form cannot set a custom header, and a cross-origin fetch that sets one is preflighted.
/// </description></item>
/// <item><description>
/// <b>Identity</b> — established upstream by the session gate. <b>Fails closed</b>: no identity below
/// an auth gate is an invariant violation, and a dispatch nobody can be attributed to must not run,
/// because the audit record would be blind.
/// </description></item>
/// <item><description>
/// <b>Size</b> — <c>Content-Length</c> against <see cref="MeshDispatchGuardOptions.MaxRequestBytes"/>,
/// before anything is deserialized.
/// </description></item>
/// <item><description>
/// <b>Rate</b> — per identity, per minute. The per-<em>target</em> limit is not here: the target
/// service is inside the body, which this layer deliberately does not parse, so the handler applies
/// it where the parsed request already exists.
/// </description></item>
/// </list>
/// <para>
/// <b>What the rate limit is and is not</b> — see <see cref="MeshDispatchRateLimiter"/>. It bounds a
/// stuck loop or one compromised session; the hard flood guarantee belongs at the edge, where API
/// Gateway counts across every instance and refuses before the invoke is billed.
/// </para>
/// <para>
/// <b>Refusals are shaped for their reader.</b> The rate-limit refusal is written as a Benzene
/// <em>envelope</em> with status <c>too-many-requests</c>, not as a bare HTTP 429, because the mesh UI
/// reads the envelope's status and renders its message; a bare 429 falls into its generic
/// failure path and reads as "something broke" rather than "you are going too fast". The CSRF and
/// no-identity refusals stay bare 403s with a fixed body — those callers are attackers or bugs, and
/// get the refresh guard's no-detail treatment.
/// </para>
/// </remarks>
public class MeshDispatchGuardMiddleware<TContext> : IMiddleware<TContext>
    where TContext : IHttpContext
{
    private readonly MeshDispatchGuardOptions _options;
    private readonly MeshDispatchIdentity _identity;
    private readonly MeshDispatchRateLimiter _limiter;
    private readonly IHttpRequestAdapter<TContext> _requestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly IRouteFinder? _routeFinder;
    private readonly ILogger? _logger;
    private readonly string _guardedPath;

    /// <summary>Initializes a new instance of the <see cref="MeshDispatchGuardMiddleware{TContext}"/> class.</summary>
    public MeshDispatchGuardMiddleware(
        MeshDispatchGuardOptions options,
        MeshDispatchIdentity identity,
        MeshDispatchRateLimiter limiter,
        IHttpRequestAdapter<TContext> requestAdapter,
        IBenzeneResponseAdapter<TContext> responseAdapter,
        IRouteFinder? routeFinder = null,
        ILogger? logger = null)
    {
        _options = options;
        _identity = identity;
        _limiter = limiter;
        _requestAdapter = requestAdapter;
        _responseAdapter = responseAdapter;
        _routeFinder = routeFinder;
        _logger = logger;
        _guardedPath = Canonicalize(options.Path);
    }

    /// <summary>Gets the name of the middleware.</summary>
    public string Name => "MeshDispatchGuard";

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _requestAdapter.Map(context);

        if (!IsGuarded(request))
        {
            await next();
            return;
        }

        if (!HasHeader(request, _options.HeaderName))
        {
            _logger?.LogWarning("Mesh dispatch refused: required header {header} was absent", _options.HeaderName);
            await DenyAsync(context, "403", "forbidden");
            return;
        }

        // FAIL CLOSED. Reaching here without an identity means the session gate is missing or was
        // mounted after this guard - a wiring error, and one that would silently produce unattributable
        // dispatches. Refusing is the only safe reading.
        if (string.IsNullOrWhiteSpace(_identity.Email))
        {
            _logger?.LogWarning(
                "Mesh dispatch refused: no identity was established. Is this guard mounted above the session gate?");
            await DenyAsync(context, "403", "forbidden");
            return;
        }

        if (ContentLength(request) > _options.MaxRequestBytes)
        {
            _logger?.LogWarning("Mesh dispatch refused for {email}: payload over {max} bytes",
                _identity.Email, _options.MaxRequestBytes);
            await DenyEnvelopeAsync(context, "413", "bad-request",
                $"That payload is larger than this mesh accepts ({_options.MaxRequestBytes:N0} bytes).");
            return;
        }

        _limiter.Prune();
        if (!_limiter.TryAcquire($"identity:{_identity.Email}", _options.MaxPerMinutePerIdentity, out var retryAfter))
        {
            _logger?.LogInformation("Mesh dispatch throttled for {email}", _identity.Email);
            _responseAdapter.SetResponseHeader(context, "Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture));
            await DenyEnvelopeAsync(context, "429", "too-many-requests",
                $"You have reached this mesh's dispatch limit of {_options.MaxPerMinutePerIdentity} a minute. "
                + $"Try again in {retryAfter}s.");
            return;
        }

        await next();
    }

    /// <summary>
    /// Path match, plus a topic match through the route finder, so a route alias that reaches the
    /// handler cannot reach it around this guard. The same two-way matching the refresh guard uses.
    /// </summary>
    private bool IsGuarded(HttpRequest request)
    {
        if (Canonicalize(request.Path) == _guardedPath)
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

    private static long ContentLength(HttpRequest request)
    {
        if (request.Headers == null)
        {
            return 0;
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "content-length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(header.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            {
                return length;
            }
        }

        // An absent Content-Length is not evidence of a small body, but it is also not something this
        // layer can measure without buffering. The edge (API Gateway) caps the request regardless, and
        // the handler sees the parsed payload - so this check bounds what it can and does not pretend
        // to bound what it cannot.
        return 0;
    }

    private static bool HasHeader(HttpRequest request, string name)
    {
        if (request.Headers == null)
        {
            return false;
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(header.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Normalizes a path exactly as the router does, so guard and router agree on every spelling.</summary>
    internal static string Canonicalize(string? path)
    {
        var beforeQuery = (path ?? string.Empty).Split('?', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var segments = (beforeQuery ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return ("/" + string.Join("/", segments)).ToLowerInvariant();
    }

    /// <summary>A refusal for a caller who should not be told anything: fixed body, no detail.</summary>
    private async Task DenyAsync(TContext context, string statusCode, string error)
    {
        _responseAdapter.SetStatusCode(context, statusCode);
        _responseAdapter.SetContentType(context, "application/json");
        _responseAdapter.SetBody(context, "{\"error\":\"" + error + "\"}");
        await _responseAdapter.FinalizeAsync(context);
    }

    /// <summary>
    /// A refusal for the mesh UI: a Benzene envelope, because the page reads the envelope's status and
    /// renders its message. A bare HTTP status here would render as an unexplained failure.
    /// </summary>
    private async Task DenyEnvelopeAsync(TContext context, string httpStatus, string benzeneStatus, string message)
    {
        _responseAdapter.SetStatusCode(context, httpStatus);
        _responseAdapter.SetContentType(context, "application/json");
        _responseAdapter.SetBody(context,
            "{\"statusCode\":\"" + benzeneStatus + "\",\"headers\":{},\"body\":"
            + System.Text.Json.JsonSerializer.Serialize(message) + "}");
        await _responseAdapter.FinalizeAsync(context);
    }
}
