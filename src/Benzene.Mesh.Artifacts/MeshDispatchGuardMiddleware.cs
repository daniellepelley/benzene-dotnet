using System.Globalization;
using System.Text;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;
using Benzene.Http.RequestBody;
using Benzene.Http.Routing;
using Microsoft.Extensions.Logging;

using Benzene.Mesh.Dispatch;

namespace Benzene.Mesh.Artifacts;

/// <summary>
/// Canonicalizes an HTTP path the same way the router does, so every path-scoped check in the mesh
/// host agrees on what counts as the same path.
/// </summary>
public static class MeshPathCanonicalizer
{
    /// <summary>
    /// Normalizes a path exactly as the router does (query string stripped, empty segments collapsed
    /// — including a trailing slash — lower-invariant), so every path-scoped check in the mesh host
    /// agrees with the router on what counts as the same path. Non-generic and public specifically so
    /// <c>MeshAuthGate</c> — a different assembly, whose own path-scoped checks (the
    /// <c>dispatchRole</c>/<c>ingestion.mode</c> gate) must never disagree with
    /// <see cref="MeshDispatchGuardMiddleware{TContext}"/> on this — can canonicalize through the
    /// identical rule rather than hand-rolling a second one that could silently drift from it. A
    /// trailing-slash mismatch between an exact-match <c>PathString.Equals</c> here and this
    /// normalization was exactly this class of bug (corrected 2026-08-22): a request to
    /// <c>/mesh/dispatch/</c> or <c>/mesh/report/</c> (one added slash) missed the raw exact-match
    /// check entirely while the router still normalized the slash away and delivered the request to
    /// the real handler — a full bypass of the dispatch-role and ingestion-secret checks.
    /// </summary>
    public static string Canonicalize(string? path)
    {
        var beforeQuery = (path ?? string.Empty).Split('?', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var segments = (beforeQuery ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return ("/" + string.Join("/", segments)).ToLowerInvariant();
    }

    /// <summary>
    /// The shared path-OR-topic predicate: true when <paramref name="requestPath"/> canonicalizes to
    /// <paramref name="guardedCanonicalPath"/>, OR (when both a <paramref name="topic"/> and a
    /// <paramref name="routeFinder"/> are available) when the route finder resolves
    /// <paramref name="requestMethod"/>/<paramref name="requestPath"/> to that same topic — a route
    /// alias that reaches the guarded topic under a different literal path cannot slip past either
    /// check this way.
    /// </summary>
    /// <remarks>
    /// #287: <see cref="MeshDispatchGuardMiddleware{TContext}"/>'s own <c>IsGuarded</c> below calls
    /// this. <c>MeshAuthGate</c> (a different assembly, in <c>deploy/Mesh/Benzene.Mesh.Host</c>) calls
    /// it too for its <c>dispatchRole</c> check, which used to compare only the literal
    /// <c>DispatchPath</c> — a route alias mapping to the same <c>benzene:mesh:dispatch</c> topic under
    /// a different path would have reached the real handler with the role requirement never evaluated,
    /// even though this guard's own CSRF/identity/rate-limit checks would still have caught it. Routing
    /// both callers through this one predicate means they can never drift apart on what counts as "the
    /// guarded endpoint" again.
    /// </remarks>
    public static bool IsPathOrTopicMatch(
        string? requestMethod,
        string? requestPath,
        string guardedCanonicalPath,
        string? topic,
        IRouteFinder? routeFinder)
    {
        if (Canonicalize(requestPath) == guardedCanonicalPath)
        {
            return true;
        }

        if (string.IsNullOrEmpty(topic) || routeFinder == null)
        {
            return false;
        }

        var matchedTopic = routeFinder.Find(requestMethod ?? string.Empty, requestPath ?? string.Empty)?.Topic;
        return matchedTopic != null && string.Equals(matchedTopic, topic, StringComparison.OrdinalIgnoreCase);
    }
}

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
/// <b>Size</b> — the request body's actual byte count (not the caller-supplied <c>Content-Length</c>
/// header, which a chunked <c>Transfer-Encoding</c> request omits entirely - see
/// <see cref="RequestBodyBytes"/>) against <see cref="MeshDispatchGuardOptions.MaxRequestBytes"/>,
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
    private readonly HttpRequestBodyBuffer? _bodyBuffer;
    private readonly string _guardedPath;

    /// <summary>Initializes a new instance of the <see cref="MeshDispatchGuardMiddleware{TContext}"/> class.</summary>
    /// <param name="bodyBuffer">
    /// Optional. When the transport buffers its request body up front (every ASP.NET Core host wired
    /// through <c>Benzene.AspNet.Core.BenzeneExtensions.UseHttp</c> - see
    /// <see cref="Benzene.Http.RequestBody.BufferRequestBodyMiddleware{TContext}"/>), this is how the
    /// size check measures the request's ACTUAL byte count instead of trusting the caller-supplied
    /// <c>Content-Length</c> header - see <see cref="RequestBodyBytes"/>. Null on a transport that
    /// doesn't buffer (e.g. AWS API Gateway, where the whole body already arrives pre-materialized and
    /// <c>Content-Length</c> is trustworthy), which falls back to the header check.
    /// </param>
    public MeshDispatchGuardMiddleware(
        MeshDispatchGuardOptions options,
        MeshDispatchIdentity identity,
        MeshDispatchRateLimiter limiter,
        IHttpRequestAdapter<TContext> requestAdapter,
        IBenzeneResponseAdapter<TContext> responseAdapter,
        IRouteFinder? routeFinder = null,
        ILogger? logger = null,
        HttpRequestBodyBuffer? bodyBuffer = null)
    {
        _options = options;
        _identity = identity;
        _limiter = limiter;
        _requestAdapter = requestAdapter;
        _responseAdapter = responseAdapter;
        _routeFinder = routeFinder;
        _logger = logger;
        _bodyBuffer = bodyBuffer;
        _guardedPath = MeshPathCanonicalizer.Canonicalize(options.Path);
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

        if (RequestBodyBytes(request) > _options.MaxRequestBytes)
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
    /// handler cannot reach it around this guard. The same two-way matching the refresh guard uses -
    /// via the shared <see cref="MeshPathCanonicalizer.IsPathOrTopicMatch"/> predicate (#287), so this
    /// and <c>MeshAuthGate</c>'s <c>dispatchRole</c> check can never drift on what counts as "the
    /// dispatch endpoint".
    /// </summary>
    private bool IsGuarded(HttpRequest request) =>
        MeshPathCanonicalizer.IsPathOrTopicMatch(request.Method, request.Path, _guardedPath, _options.Topic, _routeFinder);

    /// <summary>
    /// Measures the size check should bound against: the ACTUAL body byte count when the transport has
    /// already read it (see <see cref="_bodyBuffer"/>), falling back to <see cref="ContentLength"/> only
    /// when nothing buffered the body. This closes #35 (live-verified, security-relevant, P9): a chunked
    /// <c>Transfer-Encoding</c> request carries no <c>Content-Length</c> header at all - <c>ContentLength</c>
    /// below returns 0 for "absent", not "empty" - which let an oversized chunked body sail straight past
    /// a header-only check into the dispatch handler on the bare-Kestrel host, defeating the guard's own
    /// threat model (a compromised session). <c>Benzene.AspNet.Core.BenzeneExtensions.UseHttp</c> always
    /// wires <c>BufferRequestBodyMiddleware</c> ahead of every custom middleware (including this one), so
    /// on every ASP.NET Core host the real byte count - Kestrel-decoded, chunked or not - is already sitting
    /// in <see cref="_bodyBuffer"/> by the time this check runs; measuring it instead of the header is not
    /// an extra read, just trusting what already happened over what the caller merely claimed. Kestrel's
    /// own <c>MaxRequestBodySize</c> (set in <c>deploy/Mesh/Benzene.Mesh.Host/Program.cs</c>) is the
    /// defence-in-depth layer that stops the buffering itself from being unbounded.
    /// </summary>
    private long RequestBodyBytes(HttpRequest request)
    {
        if (_bodyBuffer is { IsBuffered: true })
        {
            if (_bodyBuffer.IsBytesBuffered)
            {
                return _bodyBuffer.BodyBytes.Length;
            }

            return _bodyBuffer.Body == null ? 0 : Encoding.UTF8.GetByteCount(_bodyBuffer.Body);
        }

        return ContentLength(request);
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
        // layer can measure without buffering. Only reached on a transport with no HttpRequestBodyBuffer
        // (e.g. AWS API Gateway, where the whole body already arrives pre-materialized and Content-Length
        // is trustworthy) - see RequestBodyBytes, which prefers the actual buffered size wherever the
        // transport provides one.
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
