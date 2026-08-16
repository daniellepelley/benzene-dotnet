using System;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// <c>GET {BasePath}/logout</c>: clears the session cookie and redirects to the mesh root, which
/// <see cref="OidcSessionGateMiddleware{TContext}"/> then redirects on to <c>{BasePath}/login</c> since
/// no session remains. Any other request passes through to <c>next()</c>.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
public class OidcLogoutMiddleware<TContext> : IMiddleware<TContext>, ITerminalMiddleware where TContext : IHttpContext
{
    private readonly MeshOidcOptions _options;
    private readonly IHttpRequestAdapter<TContext> _httpRequestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly string _logoutPath;

    /// <summary>Gets the name of the middleware.</summary>
    public string Name => "OidcLogout";

    /// <summary>Initializes a new instance of the <see cref="OidcLogoutMiddleware{TContext}"/> class.</summary>
    /// <param name="options">The auth configuration.</param>
    /// <param name="httpRequestAdapter">Adapter used to read the request method/path.</param>
    /// <param name="responseAdapter">Adapter used to clear the session cookie and write the redirect.</param>
    public OidcLogoutMiddleware(
        MeshOidcOptions options, IHttpRequestAdapter<TContext> httpRequestAdapter, IBenzeneResponseAdapter<TContext> responseAdapter)
    {
        _options = options;
        _httpRequestAdapter = httpRequestAdapter;
        _responseAdapter = responseAdapter;
        _logoutPath = OidcPaths.Normalize(options.BasePath + "/logout");
    }

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _httpRequestAdapter.Map(context).AsLowerCase();

        if (request.Method != "get" || OidcPaths.Normalize(request.Path) != _logoutPath)
        {
            await next();
            return;
        }

        _responseAdapter.SetResponseHeader(context, "Set-Cookie",
            CookieHeader.BuildExpired(OidcCookies.SessionCookieName, _options.CookiePath));
        _responseAdapter.SetStatusCode(context, "302");
        // MeshOidcOptions.HomePath, not a hardcoded "/": a host whose landing page isn't at the root
        // would otherwise redirect the just-signed-out user to a route it doesn't serve.
        _responseAdapter.SetResponseHeader(context, "Location", _options.HomePath);
        _responseAdapter.SetBody(context, string.Empty);
        await _responseAdapter.FinalizeAsync(context);
    }
}
