using System;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// <c>POST {BasePath}/logout</c>: clears the session cookie and answers <c>{"redirect":null}</c> (200,
/// JSON), which <see cref="OidcSessionGateMiddleware{TContext}"/> then redirects the caller's NEXT
/// request on to <c>{BasePath}/login</c> since no session remains. Any other request passes through to
/// <c>next()</c>, EXCEPT a request whose path matches but whose method isn't <c>POST</c> - that gets a
/// terminal <c>405</c> (see <see cref="HandleAsync"/>'s remarks on why GET is refused rather than just
/// falling through).
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
public class OidcLogoutMiddleware<TContext> : IMiddleware<TContext>, ITerminalMiddleware where TContext : IHttpContext
{
    /// <summary>
    /// #175 / round 1's #4: the CSRF header <c>POST {BasePath}/logout</c> requires, matching the exact
    /// same custom-header convention and name
    /// <see href="deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.cs">MeshAuthGate.LogoutHeaderName</see> and
    /// <c>Benzene.Mesh.Artifacts</c>'s <c>MeshRefreshGuardMiddleware</c>/<c>MeshDispatchGuardMiddleware</c>
    /// already use: a cross-site <c>&lt;form method="post"&gt;</c> cannot set a custom header at all, and
    /// a cross-origin <c>fetch()</c> that tries to triggers a CORS preflight this pipeline never
    /// approves - so only a genuine same-origin caller can ever supply it. The value sent is not
    /// inspected, exactly like its siblings - what defends against CSRF is that a cross-site caller
    /// cannot set the header at all, not what it would have set it to.
    /// </summary>
    public const string LogoutHeaderName = "X-Benzene-Logout";

    private readonly MeshOidcOptions _options;
    private readonly IHttpRequestAdapter<TContext> _httpRequestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly string _logoutPath;

    /// <summary>Gets the name of the middleware.</summary>
    public string Name => "OidcLogout";

    /// <summary>Initializes a new instance of the <see cref="OidcLogoutMiddleware{TContext}"/> class.</summary>
    /// <param name="options">The auth configuration.</param>
    /// <param name="httpRequestAdapter">Adapter used to read the request method/path/headers.</param>
    /// <param name="responseAdapter">Adapter used to clear the session cookie and write the response.</param>
    public OidcLogoutMiddleware(
        MeshOidcOptions options, IHttpRequestAdapter<TContext> httpRequestAdapter, IBenzeneResponseAdapter<TContext> responseAdapter)
    {
        _options = options;
        _httpRequestAdapter = httpRequestAdapter;
        _responseAdapter = responseAdapter;
        _logoutPath = OidcPaths.Normalize(options.BasePath + "/logout");
    }

    /// <inheritdoc />
    /// <remarks>
    /// #175 / round 1's #4: this used to accept a bare <c>GET</c>, which is a CSRF hazard in its own
    /// right - <c>SameSite=Lax</c> (the flag this package's own cookies already carry) still sends a
    /// cookie along on a top-level GET navigation, so a cross-site page needs nothing more exotic than
    /// <c>&lt;img src="{BasePath}/logout"&gt;</c> to sign a visiting victim out. This directly
    /// contradicted round 1's #4 ruling for the exact same hazard, which
    /// <c>deploy/Mesh/Benzene.Mesh.Host/MeshAuthGate.HandleLogoutAsync</c> already implements correctly -
    /// mirrored here exactly: a request whose path matches but whose method isn't <c>POST</c> gets a
    /// terminal <c>405</c>; a <c>POST</c> missing <see cref="LogoutHeaderName"/> gets a terminal
    /// <c>403</c>; only a <c>POST</c> carrying the header actually signs out. The response is JSON
    /// (<c>{"redirect":null}</c>), not a redirect - this package's shared <c>Benzene.Mesh.Ui</c> Sign-out
    /// control already <c>fetch()</c>es this endpoint expecting exactly that shape (it was written
    /// against <c>MeshAuthGate</c>'s contract first); a <c>302</c> response here would be followed
    /// transparently by <c>fetch()</c> and its non-JSON body would fail parsing instead. This package
    /// does not (yet) resolve an IdP <c>end_session_endpoint</c> the way <c>MeshAuthGate</c> does - the
    /// redirect is always <c>null</c>, meaning "local sign-out only, the caller should reload" - see this
    /// package's <c>CLAUDE.md</c> "Stateless logout" section.
    /// </remarks>
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _httpRequestAdapter.Map(context).AsLowerCase();

        if (OidcPaths.Normalize(request.Path) != _logoutPath)
        {
            await next();
            return;
        }

        if (!string.Equals(request.Method, "post", StringComparison.OrdinalIgnoreCase))
        {
            _responseAdapter.SetStatusCode(context, "405");
            _responseAdapter.SetContentType(context, "text/plain");
            _responseAdapter.SetBody(context, "Method not allowed - logout must be POSTed (a GET-triggered logout is a CSRF hazard).");
            await _responseAdapter.FinalizeAsync(context);
            return;
        }

        // Case-insensitive header lookup by direct dictionary access is safe here: `request` came
        // through AsLowerCase() above, which already lowercases every header NAME (not value).
        if (!request.Headers.TryGetValue(LogoutHeaderName.ToLowerInvariant(), out var header) || string.IsNullOrWhiteSpace(header))
        {
            _responseAdapter.SetStatusCode(context, "403");
            _responseAdapter.SetContentType(context, "application/json");
            _responseAdapter.SetBody(context, "{\"error\":\"forbidden\"}");
            await _responseAdapter.FinalizeAsync(context);
            return;
        }

        _responseAdapter.SetResponseHeader(context, "Set-Cookie",
            CookieHeader.BuildExpired(OidcCookies.SessionCookieName, _options.CookiePath));
        _responseAdapter.SetStatusCode(context, "200");
        _responseAdapter.SetContentType(context, "application/json");
        _responseAdapter.SetBody(context, "{\"redirect\":null}");
        await _responseAdapter.FinalizeAsync(context);
    }
}
