using System;
using System.Text;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// <c>GET {BasePath}/login</c>: starts the OAuth2 Authorization Code flow by redirecting (302) to the
/// provider's authorization endpoint (from OIDC discovery - see <see cref="OidcConfigurationManagerFactory"/>)
/// with <c>client_id</c>/<c>redirect_uri</c>/<c>response_type=code</c>/<c>scope</c>/<c>state</c>. The
/// <c>state</c> value is also set as a short-lived, <c>HttpOnly</c>/<c>Secure</c>/<c>SameSite=Lax</c>
/// cookie (see <see cref="OidcStateToken"/>) so <see cref="OidcCallbackMiddleware{TContext}"/> can
/// validate the callback is genuinely a response to THIS login attempt. Any other request passes
/// through to <c>next()</c>.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
public class OidcLoginMiddleware<TContext> : IMiddleware<TContext>, ITerminalMiddleware where TContext : IHttpContext
{
    private readonly MeshOidcOptions _options;
    private readonly byte[] _signingKey;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly IHttpRequestAdapter<TContext> _httpRequestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly IOidcQueryStringReader<TContext> _queryStringReader;
    private readonly ILogger? _logger;
    private readonly string _loginPath;

    /// <summary>Gets the name of the middleware.</summary>
    public string Name => "OidcLogin";

    /// <summary>Initializes a new instance of the <see cref="OidcLoginMiddleware{TContext}"/> class.</summary>
    /// <param name="options">The auth configuration.</param>
    /// <param name="signingKey">The HMAC-SHA256 key used to sign the state token.</param>
    /// <param name="configurationManager">The shared, wire-up-time-built OIDC discovery configuration manager.</param>
    /// <param name="httpRequestAdapter">Adapter used to read the request method/path/headers.</param>
    /// <param name="responseAdapter">Adapter used to write the redirect response and state cookie.</param>
    /// <param name="queryStringReader">Reads the <c>returnTo</c> query parameter.</param>
    /// <param name="logger">
    /// Optional logger for a discovery failure (see <see cref="HandleAsync"/>) - the real reason is
    /// logged server-side only, never echoed to the browser, matching
    /// <see cref="OidcCallbackMiddleware{TContext}"/>'s convention. Nullable/optional so a direct,
    /// logger-free construction (tests) still works.
    /// </param>
    public OidcLoginMiddleware(
        MeshOidcOptions options, byte[] signingKey, ConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        IHttpRequestAdapter<TContext> httpRequestAdapter, IBenzeneResponseAdapter<TContext> responseAdapter,
        IOidcQueryStringReader<TContext> queryStringReader, ILogger? logger = null)
    {
        _options = options;
        _signingKey = signingKey;
        _configurationManager = configurationManager;
        _httpRequestAdapter = httpRequestAdapter;
        _responseAdapter = responseAdapter;
        _queryStringReader = queryStringReader;
        _logger = logger;
        _loginPath = OidcPaths.Normalize(options.BasePath + "/login");
    }

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _httpRequestAdapter.Map(context).AsLowerCase();

        if (request.Method != "get" || OidcPaths.Normalize(request.Path) != _loginPath)
        {
            await next();
            return;
        }

        var queryParameters = _queryStringReader.GetQueryParameters(context);
        var requestedReturnTo = queryParameters.TryGetValue("returnTo", out var raw) ? raw : null;
        // Falls back to the host's own landing page, not a hardcoded "/" - a direct visit to /login
        // (no returnTo) must land somewhere the host actually serves.
        var returnTo = ReturnToValidator.IsSafe(requestedReturnTo) ? requestedReturnTo! : _options.HomePath;

        OpenIdConnectConfiguration configuration;
        try
        {
            // #173 / round 1's #20: this is the exact request-time crash MeshOidcOptions.Validate() now
            // rejects at wire-up time for a non-https issuer - but validation can only ever prove the
            // issuer LOOKS safe, never that it is actually reachable and serving a real discovery
            // document. A misconfigured issuer, or a transient IdP outage, must still deny the login
            // cleanly here rather than surface as an unhandled 500.
            configuration = await _configurationManager.GetConfigurationAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OIDC login denied: could not fetch discovery metadata for issuer {Issuer}", _options.Issuer);
            await OidcDiscoveryFailureResponse.WriteAsync(_responseAdapter, context);
            return;
        }

        var redirectUri = RequestUrl.BuildBaseUrl(request, _options) + _options.BasePath + "/callback";
        var state = OidcStateToken.Create(_signingKey, returnTo);

        var authorizationUrl = BuildAuthorizationUrl(configuration.AuthorizationEndpoint, redirectUri, state);

        _responseAdapter.SetResponseHeader(context, "Set-Cookie",
            CookieHeader.Build(OidcCookies.StateCookieName, state, _options.BasePath, TimeSpan.FromMinutes(10)));
        _responseAdapter.SetStatusCode(context, "302");
        _responseAdapter.SetResponseHeader(context, "Location", authorizationUrl);
        _responseAdapter.SetBody(context, string.Empty);
        await _responseAdapter.FinalizeAsync(context);
    }

    private string BuildAuthorizationUrl(string authorizationEndpoint, string redirectUri, string state)
    {
        var sb = new StringBuilder(authorizationEndpoint);
        sb.Append(authorizationEndpoint.Contains('?') ? '&' : '?');
        sb.Append("client_id=").Append(Uri.EscapeDataString(_options.ClientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&response_type=code");
        sb.Append("&scope=").Append(Uri.EscapeDataString(_options.Scope));
        sb.Append("&state=").Append(Uri.EscapeDataString(state));
        return sb.ToString();
    }
}
