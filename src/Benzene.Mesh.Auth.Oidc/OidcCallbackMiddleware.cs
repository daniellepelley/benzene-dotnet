using System;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// <c>GET {BasePath}/callback</c>: receives <c>code</c>/<c>state</c> from the provider, validates
/// <c>state</c> against the cookie <see cref="OidcLoginMiddleware{TContext}"/> set, exchanges
/// <c>code</c> for an ID token, verifies it (<see cref="OidcIdTokenValidator"/>), checks the verified
/// email against the allowlist, and - only if every step passes - issues a session cookie and redirects
/// to the validated <c>returnTo</c>. Any failure at any step denies the login (no session cookie is
/// issued) with a generic response; the real reason is logged server-side only, never returned to the
/// browser (see this package's <c>CLAUDE.md</c> "no detail leakage" section, matching
/// <c>Benzene.Auth.OAuth2</c>'s existing convention). Any other request passes through to <c>next()</c>.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
public class OidcCallbackMiddleware<TContext> : IMiddleware<TContext>, ITerminalMiddleware where TContext : IHttpContext
{
    private const string LoggerCategory = "Benzene.Mesh.Auth.Oidc";

    private readonly MeshOidcOptions _options;
    private readonly byte[] _signingKey;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly OidcTokenExchangeClient _tokenExchangeClient;
    private readonly OidcIdTokenValidator _idTokenValidator;
    private readonly IHttpRequestAdapter<TContext> _httpRequestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly IOidcQueryStringReader<TContext> _queryStringReader;
    private readonly ILogger _logger;
    private readonly string _callbackPath;

    /// <summary>Gets the name of the middleware.</summary>
    public string Name => "OidcCallback";

    /// <remarks>Internal, not public: two of its parameters (<see cref="OidcTokenExchangeClient"/>,
    /// <see cref="OidcIdTokenValidator"/>) are this package's own internal collaborators, built once by
    /// <see cref="Extensions.UseMeshOidcAuth{TContext}"/> - this type is never constructed directly by a
    /// consumer.</remarks>
    internal OidcCallbackMiddleware(
        MeshOidcOptions options, byte[] signingKey, ConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        OidcTokenExchangeClient tokenExchangeClient, OidcIdTokenValidator idTokenValidator,
        IHttpRequestAdapter<TContext> httpRequestAdapter, IBenzeneResponseAdapter<TContext> responseAdapter,
        IOidcQueryStringReader<TContext> queryStringReader, ILogger logger)
    {
        _options = options;
        _signingKey = signingKey;
        _configurationManager = configurationManager;
        _tokenExchangeClient = tokenExchangeClient;
        _idTokenValidator = idTokenValidator;
        _httpRequestAdapter = httpRequestAdapter;
        _responseAdapter = responseAdapter;
        _queryStringReader = queryStringReader;
        _logger = logger;
        _callbackPath = OidcPaths.Normalize(options.BasePath + "/callback");
    }

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _httpRequestAdapter.Map(context).AsLowerCase();

        if (request.Method != "get" || OidcPaths.Normalize(request.Path) != _callbackPath)
        {
            await next();
            return;
        }

        var queryParameters = _queryStringReader.GetQueryParameters(context);
        queryParameters.TryGetValue("state", out var stateFromQuery);
        queryParameters.TryGetValue("code", out var code);

        var cookies = CookieHeader.Parse(request.Headers.TryGetValue("cookie", out var cookieHeader) ? cookieHeader : null);
        cookies.TryGetValue(OidcCookies.StateCookieName, out var stateFromCookie);

        if (!OidcStateToken.TryValidate(_signingKey, stateFromQuery, stateFromCookie, out var returnTo))
        {
            _logger.LogWarning("OIDC callback rejected: state parameter missing, mismatched, or expired");
            await DenyAsync(context);
            return;
        }

        if (!ReturnToValidator.IsSafe(returnTo))
        {
            // Defense in depth - Create() only ever embeds an already-validated returnTo, so this
            // should be unreachable unless the signing key itself is compromised, in which case the
            // signature check above would already have failed first.
            returnTo = "/";
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            _logger.LogWarning("OIDC callback rejected: no authorization code");
            await DenyAsync(context);
            return;
        }

        string idToken;
        try
        {
            var configuration = await _configurationManager.GetConfigurationAsync();
            var redirectUri = RequestUrl.BuildBaseUrl(request, _options) + _options.BasePath + "/callback";
            idToken = await _tokenExchangeClient.ExchangeCodeForIdTokenAsync(
                configuration.TokenEndpoint, _options.ClientId, _options.ClientSecret, code, redirectUri);
        }
        catch (OidcTokenExchangeException ex)
        {
            _logger.LogWarning(ex, "OIDC callback rejected: token exchange failed");
            await DenyAsync(context);
            return;
        }

        var validation = await _idTokenValidator.ValidateAsync(idToken);
        if (!validation.IsValid)
        {
            _logger.LogWarning("OIDC callback rejected: ID token failed validation ({Reason})", validation.FailureReason);
            await DenyAsync(context);
            return;
        }

        var email = validation.Email!;
        if (!EmailAllowlist.IsAllowed(_options.AllowedEmails, email))
        {
            _logger.LogWarning("OIDC callback rejected: {Email} is not on the allowlist", email);
            await DenyAsync(context);
            return;
        }

        var session = OidcSessionToken.Create(_signingKey, email, _options.SessionDuration);
        _responseAdapter.SetResponseHeader(context, "Set-Cookie",
            CookieHeader.Build(OidcCookies.SessionCookieName, session, _options.CookiePath, _options.SessionDuration));
        _responseAdapter.SetStatusCode(context, "302");
        _responseAdapter.SetResponseHeader(context, "Location", returnTo);
        _responseAdapter.SetBody(context, string.Empty);
        await _responseAdapter.FinalizeAsync(context);
    }

    /// <summary>Denies the login with a generic, detail-free response, and clears the (single-use)
    /// state cookie so a captured/replayed callback URL can't be tried again against a still-live
    /// cookie. Never a redirect back into the authorization flow (that would risk a loop) and never the
    /// real failure reason (logged instead, above, at the single call site for each failure path). Sets
    /// exactly one <c>Set-Cookie</c> header - see this package's <c>CLAUDE.md</c> on why the success
    /// path deliberately does not ALSO clear the state cookie in the same response (some
    /// <see cref="IBenzeneResponseAdapter{TContext}"/> implementations only carry one value per header
    /// name, so two <c>Set-Cookie</c> writes in one response is not safe to rely on).</summary>
    private async Task DenyAsync(TContext context)
    {
        _responseAdapter.SetResponseHeader(context, "Set-Cookie",
            CookieHeader.BuildExpired(OidcCookies.StateCookieName, _options.BasePath));
        _responseAdapter.SetStatusCode(context, "401");
        _responseAdapter.SetContentType(context, "text/html; charset=utf-8");
        _responseAdapter.SetBody(context, DeniedPage);
        await _responseAdapter.FinalizeAsync(context);
    }

    private const string DeniedPage =
        "<!doctype html><html><head><title>Access denied</title></head><body>" +
        "<h1>Access denied</h1><p>Login failed or this account is not authorized.</p></body></html>";
}
