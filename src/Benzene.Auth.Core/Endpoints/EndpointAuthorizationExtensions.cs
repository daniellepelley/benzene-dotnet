using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;

namespace Benzene.Auth.Core.Endpoints;

/// <summary><c>UseEndpointAuthorization</c>: authorization per handler.</summary>
public static class EndpointAuthorizationExtensions
{
    /// <summary>
    /// Authorizes each message against its handler's requirements - <c>[RequireScope]</c>,
    /// <c>[RequireRole]</c>, <c>[RequirePolicy]</c>, <c>[AllowAnonymous]</c>, or
    /// <c>options.ForTopic(...)</c> - before the request body is read. By default a handler with no
    /// requirement still needs an authenticated caller, and Benzene's reserved topics (spec, ping,
    /// health) are anonymous. Place it after the authenticators (<c>UseOAuth2Bearer</c>,
    /// <c>UseApiGatewayAuthorizerIdentity</c>) and before <c>UseMessageHandlers</c>.
    /// </summary>
    public static IMiddlewarePipelineBuilder<TContext> UseEndpointAuthorization<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, Action<EndpointAuthorizationOptions>? configure = null)
    {
        var options = new EndpointAuthorizationOptions();
        configure?.Invoke(options);

        app.Register(x => x.TryAddScoped<AuthenticationHolder>());
        return app.Use(resolver => new EndpointAuthorizationMiddleware<TContext>(options, resolver));
    }
}
