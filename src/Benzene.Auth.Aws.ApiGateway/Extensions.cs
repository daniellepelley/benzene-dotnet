using System.Security.Claims;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Auth.Core;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Core.Middleware;

namespace Benzene.Auth.Aws.ApiGateway;

/// <summary>
/// <c>UseApiGatewayAuthorizerIdentity</c>: the caller from the API Gateway authorizer's context, for REST
/// APIs (<see cref="ApiGatewayContext"/>) and HTTP APIs (<see cref="ApiGatewayV2Context"/>).
/// </summary>
/// <remarks>
/// <b>This trusts API Gateway.</b> API Gateway has already authenticated the caller; this reads what it
/// says. Anything that can invoke the function another way - a Lambda function URL, a direct
/// <c>Invoke</c>, another trigger - can send a forged authorizer context. Allow only API Gateway to invoke
/// the function (its resource policy), and don't give it a function URL.
/// </remarks>
public static class Extensions
{
    /// <summary>
    /// Sets the caller (<see cref="AuthenticationHolder.Principal"/>) from a REST API event's authorizer
    /// context - Cognito claims, or a Lambda authorizer's context and <c>principalId</c> - so
    /// <c>RequireScope</c>, <c>RequireRole</c> and <c>RequirePolicy</c> after it see the caller. An event
    /// with no authorizer context sets no caller (those checks then answer 401), and a caller another
    /// authenticator already set is kept. Place it before the checks.
    /// </summary>
    public static IMiddlewarePipelineBuilder<ApiGatewayContext> UseApiGatewayAuthorizerIdentity(
        this IMiddlewarePipelineBuilder<ApiGatewayContext> app, Action<ApiGatewayAuthorizerIdentityOptions>? configure = null)
    {
        var options = Configure(configure);
        return UseAuthorizerIdentity(app, "ApiGatewayAuthorizerIdentity",
            context => ApiGatewayAuthorizerClaims.FromV1(context.ApiGatewayProxyRequest, options));
    }

    /// <summary>
    /// Sets the caller (<see cref="AuthenticationHolder.Principal"/>) from an HTTP API event's authorizer
    /// context - a JWT authorizer's claims and scopes, a Lambda authorizer's context, or IAM - so
    /// <c>RequireScope</c>, <c>RequireRole</c> and <c>RequirePolicy</c> after it see the caller. An event
    /// with no authorizer context sets no caller (those checks then answer 401), and a caller another
    /// authenticator already set is kept. Place it before the checks.
    /// </summary>
    public static IMiddlewarePipelineBuilder<ApiGatewayV2Context> UseApiGatewayAuthorizerIdentity(
        this IMiddlewarePipelineBuilder<ApiGatewayV2Context> app, Action<ApiGatewayAuthorizerIdentityOptions>? configure = null)
    {
        var options = Configure(configure);
        return UseAuthorizerIdentity(app, "ApiGatewayAuthorizerIdentity",
            context => ApiGatewayAuthorizerClaims.FromV2(context.ApiGatewayProxyRequest, options));
    }

    private static ApiGatewayAuthorizerIdentityOptions Configure(Action<ApiGatewayAuthorizerIdentityOptions>? configure)
    {
        var options = new ApiGatewayAuthorizerIdentityOptions();
        configure?.Invoke(options);
        return options;
    }

    private static IMiddlewarePipelineBuilder<TContext> UseAuthorizerIdentity<TContext>(
        IMiddlewarePipelineBuilder<TContext> app, string name, Func<TContext, ClaimsPrincipal?> principalFor)
    {
        app.Register(x => x.TryAddScoped<AuthenticationHolder>());
        return app.Use(resolver => new FuncWrapperMiddleware<TContext>(name, async (context, next) =>
        {
            var holder = resolver.GetService<AuthenticationHolder>();

            // The first authenticator wins: a caller another one already set is never replaced.
            if (holder.Principal == null)
            {
                holder.Principal = principalFor(context);
            }

            await next();
        }));
    }
}
