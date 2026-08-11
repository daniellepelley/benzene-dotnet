using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;
using Benzene.Http.Cors;
using Benzene.Mesh.Aggregator;

namespace Benzene.Mesh.Artifacts;

/// <summary>
/// Serves the mesh aggregator's generated catalog artifacts (<c>manifest.json</c>, <c>topology.json</c>,
/// <c>services/*.json</c>, <c>usage.json</c>, <c>annotations.json</c>, <c>asyncapi.json</c>) over HTTP by
/// reading them from the registered <see cref="IMeshArtifactStore"/> (filesystem, S3, Blob Storage,
/// GCS - whichever adapter is wired), so the Mesh UI - a static page that fetches <c>manifest.json</c>
/// relatively - has something to load regardless of which store backs the deployment. Mirrors
/// <c>Benzene.Spec.Ui</c>'s middleware pattern (drives the response adapter directly).
/// </summary>
/// <remarks>
/// When a <see cref="CorsSettings"/> is supplied it also stamps CORS headers on the artifact
/// responses so a cross-origin reader (e.g. the AsyncAPI Studio deep-link that fetches
/// <c>asyncapi.json</c> from <c>studio.asyncapi.com</c>) isn't blocked. It dogfoods Benzene's own
/// CORS support (<see cref="CorsSettings"/> + <see cref="CorsOriginChecker"/>) rather than
/// hand-rolling the <c>Access-Control-*</c> headers. The framework's <see cref="CorsMiddleware{T}"/>
/// can't cover these responses itself: it only fires on paths a registered
/// <c>IHttpEndpointFinder</c> knows about, whereas the artifacts are served by this
/// short-circuiting middleware and have no routed endpoint.
/// </remarks>
public class MeshArtifactMiddleware<TContext> : IMiddleware<TContext> where TContext : IHttpContext
{
    private readonly IMeshArtifactStore _store;
    private readonly IHttpRequestAdapter<TContext> _requestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly CorsSettings? _corsSettings;
    private readonly CorsOriginChecker _corsOriginChecker = new();

    public MeshArtifactMiddleware(
        IMeshArtifactStore store,
        IHttpRequestAdapter<TContext> requestAdapter,
        IBenzeneResponseAdapter<TContext> responseAdapter,
        CorsSettings? corsSettings = null)
    {
        _store = store;
        _requestAdapter = requestAdapter;
        _responseAdapter = responseAdapter;
        _corsSettings = corsSettings;
    }

    public string Name => "MeshArtifacts";

    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _requestAdapter.Map(context);
        var method = request.Method?.ToLowerInvariant();
        var key = (request.Path ?? "/").TrimStart('/');

        if (!IsArtifact(key))
        {
            await next();
            return;
        }

        // Answer the CORS preflight (OPTIONS) for the artifact paths so a browser fetch that does
        // trigger one still gets through — a plain GET fetch (the Studio case) never preflights.
        if (method == "options")
        {
            ApplyCorsHeaders(context, request);
            _responseAdapter.SetStatusCode(context, "204");
            await _responseAdapter.FinalizeAsync(context);
            return;
        }

        if (method == "get" || method == "head")
        {
            var content = await _store.TryReadAsync(key);
            ApplyCorsHeaders(context, request);
            _responseAdapter.SetStatusCode(context, content == null ? "404" : "200");
            _responseAdapter.SetContentType(context, "application/json");
            _responseAdapter.SetBody(context, content ?? "{\"error\":\"not found\"}");
            await _responseAdapter.FinalizeAsync(context);
            return;
        }

        await next();
    }

    // Dogfoods Benzene.Http's CORS support: reuse the same CorsOriginChecker/CorsSettings the
    // framework's CorsMiddleware uses to validate and echo the Origin, rather than hand-rolling
    // Access-Control-* header strings. No-op unless a CorsSettings with allowed domains was wired.
    private void ApplyCorsHeaders(TContext context, Benzene.Http.HttpRequest request)
    {
        if (_corsSettings?.AllowedDomains is not { Length: > 0 })
        {
            return;
        }

        // CorsOriginChecker reads the "origin" header lower-cased, exactly as CorsMiddleware does.
        var origin = _corsOriginChecker.MatchOrigin(_corsSettings.AllowedDomains, request.AsLowerCase());
        if (origin == null)
        {
            return;
        }

        // The response varies by Origin (echoed vs. absent), so front caches must key on it.
        _responseAdapter.SetResponseHeader(context, "Vary", "Origin");
        _responseAdapter.SetResponseHeader(context, "Access-Control-Allow-Origin", origin);
        _responseAdapter.SetResponseHeader(context, "Access-Control-Allow-Methods", "GET,HEAD,OPTIONS");
    }

    // The full artifact set the aggregator can produce - every adapter that serves it must offer all
    // of these regardless of which IMeshArtifactStore backs the deployment (2026-08-11: a K8sMesh copy
    // of this list was found missing usage.json/annotations.json before this package existed; keep the
    // set complete here so it can't drift again per hosting model).
    private static bool IsArtifact(string key)
        => key is "manifest.json" or "topology.json" or "topics.json" or "registry.json" or "asyncapi.json"
                  or "usage.json" or "annotations.json"
           || (key.StartsWith("services/", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal));
}
