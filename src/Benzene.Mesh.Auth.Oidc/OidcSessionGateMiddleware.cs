using System;
using System.Text;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// Gates every request that reaches it: requires a valid, unexpired, correctly-signed session cookie
/// whose email is (still - re-checked against the live allowlist, never just trusted from the cookie)
/// on <see cref="MeshOidcOptions.AllowedEmails"/>. Registered LAST of this package's four middlewares
/// (see <see cref="Extensions.UseMeshOidcAuth{TContext}"/>) so the login/callback/logout routes never
/// reach it - they've already short-circuited the pipeline themselves by the time a request would get
/// here. Not <see cref="ITerminalMiddleware"/>: on success it calls <c>next()</c> like any decorator;
/// it only short-circuits on failure.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
public class OidcSessionGateMiddleware<TContext> : IMiddleware<TContext> where TContext : IHttpContext
{
    private readonly IOidcSessionSink? _sessionSink;
    private readonly MeshOidcOptions _options;
    private readonly byte[] _signingKey;
    private readonly IHttpRequestAdapter<TContext> _httpRequestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly IOidcQueryStringReader<TContext> _queryStringReader;

    /// <summary>Gets the name of the middleware.</summary>
    public string Name => "OidcSessionGate";

    /// <summary>Initializes a new instance of the <see cref="OidcSessionGateMiddleware{TContext}"/> class.</summary>
    /// <param name="options">The auth configuration.</param>
    /// <param name="signingKey">The HMAC-SHA256 key used to verify the session cookie.</param>
    /// <param name="httpRequestAdapter">Adapter used to read the request method/path/headers.</param>
    /// <param name="responseAdapter">Adapter used to write a redirect or 401 response on denial.</param>
    /// <param name="queryStringReader">Reads the query string to preserve it in the login redirect's <c>returnTo</c>.</param>
    /// <param name="sessionSink">
    /// Optional. Receives the validated caller's identity on success - see <see cref="IOidcSessionSink"/>.
    /// Null (the default) simply means nothing downstream needs attribution; the gate's own allow/deny
    /// decision does not depend on it.
    /// </param>
    public OidcSessionGateMiddleware(
        MeshOidcOptions options, byte[] signingKey, IHttpRequestAdapter<TContext> httpRequestAdapter,
        IBenzeneResponseAdapter<TContext> responseAdapter, IOidcQueryStringReader<TContext> queryStringReader,
        IOidcSessionSink? sessionSink = null)
    {
        _sessionSink = sessionSink;
        _options = options;
        _signingKey = signingKey;
        _httpRequestAdapter = httpRequestAdapter;
        _responseAdapter = responseAdapter;
        _queryStringReader = queryStringReader;
    }

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        // #180: keep the ORIGINAL-cased request around too - AsLowerCase() lowercases Path as well as
        // header names (see its own remarks), which is exactly right for header lookups below (cookie/
        // accept) but wrong for a path fed into a post-login redirect: a case-sensitive deep link (an
        // S3 object key, a service-cased JSON route) would 404 after a successful login if BuildReturnTo
        // used the lowercased copy. rawRequest.Path is what BuildReturnTo actually uses.
        var rawRequest = _httpRequestAdapter.Map(context);
        var request = rawRequest.AsLowerCase();

        var cookies = CookieHeader.Parse(request.Headers.TryGetValue("cookie", out var cookieHeader) ? cookieHeader : null);
        cookies.TryGetValue(OidcCookies.SessionCookieName, out var sessionCookie);

        if (OidcSessionToken.TryValidate(_signingKey, sessionCookie, out var email) &&
            EmailAllowlist.IsAllowed(_options.AllowedEmails, email))
        {
            // Hand the identity on, for anything below that has to attribute what it does. Optional by
            // design: with no sink registered this is a null check, and the gate behaves exactly as
            // before. See IOidcSessionSink.
            _sessionSink?.Authenticated(email);
            await next();
            return;
        }

        // Distinguish a browser page navigation (redirect it back through login, landing it right
        // back where it was trying to go) from a programmatic caller - a fetch()/XHR/POST - for which
        // a redirect is useless and potentially confusing; that gets a plain, minimal 401 instead.
        var acceptsHtml = request.Headers.TryGetValue("accept", out var accept) &&
                           accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);

        if (acceptsHtml)
        {
            var returnTo = BuildReturnTo(rawRequest, context);
            var loginUrl = _options.BasePath + "/login?returnTo=" + Uri.EscapeDataString(returnTo);
            _responseAdapter.SetStatusCode(context, "302");
            _responseAdapter.SetResponseHeader(context, "Location", loginUrl);
            _responseAdapter.SetBody(context, string.Empty);
            await _responseAdapter.FinalizeAsync(context);
            return;
        }

        _responseAdapter.SetStatusCode(context, "401");
        _responseAdapter.SetContentType(context, "application/json");
        _responseAdapter.SetBody(context, "{\"error\":\"unauthorized\"}");
        await _responseAdapter.FinalizeAsync(context);
    }

    // #180: callers must pass the ORIGINAL-cased request (not the lowercased one HandleAsync otherwise
    // works from) - this feeds a post-login redirect target, and a case-sensitive deep link must come
    // back exactly as it was requested.
    private string BuildReturnTo(HttpRequest request, TContext context)
    {
        var path = string.IsNullOrWhiteSpace(request.Path) ? "/" : request.Path;
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var queryParameters = _queryStringReader.GetQueryParameters(context);
        if (queryParameters.Count == 0)
        {
            return path;
        }

        var sb = new StringBuilder(path).Append('?');
        var first = true;
        foreach (var pair in queryParameters)
        {
            if (!first)
            {
                sb.Append('&');
            }

            first = false;
            sb.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value));
        }

        var returnTo = sb.ToString();

        // ReturnToValidator.IsSafe would always accept this (it's built from our own path/query, never
        // an open redirect risk here), but a defensively-empty/odd Path from an unusual transport could
        // still fail the "/" check - fall back rather than emit something the login step would reject.
        return ReturnToValidator.IsSafe(returnTo) ? returnTo : "/";
    }
}
