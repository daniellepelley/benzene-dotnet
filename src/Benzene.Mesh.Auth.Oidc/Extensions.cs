using System;
using System.Net.Http;
using System.Text;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>Provides <see cref="UseMeshOidcAuth{TContext}"/>: OIDC login gated by an explicit email
/// allowlist, wired as opt-in Benzene middleware.</summary>
public static class Extensions
{
    private const string LoggerCategory = "Benzene.Mesh.Auth.Oidc";

    /// <summary>
    /// Registers the full OIDC auth surface on the pipeline, in order: <c>{BasePath}/login</c>,
    /// <c>{BasePath}/callback</c>, <c>{BasePath}/logout</c> (each terminal-if-matched, pass-through
    /// otherwise - see their own doc comments), then <see cref="OidcSessionGateMiddleware{TContext}"/>,
    /// which requires a valid session for every request that reaches it. Add this <em>before</em>
    /// whatever the pipeline is meant to protect (e.g. <c>UseMeshUi</c>, <c>UseMeshArtifacts</c>,
    /// message handlers) - everything registered after this call is gated; everything before it is not.
    /// </summary>
    /// <typeparam name="TContext">The HTTP context type.</typeparam>
    /// <param name="app">The middleware pipeline builder.</param>
    /// <param name="options">
    /// The auth configuration - see <see cref="MeshOidcOptions"/>. Validated at wire-up time
    /// (<see cref="MeshOidcOptions.Validate"/>): <see cref="ArgumentException"/> is thrown immediately,
    /// not on the first request, for a missing issuer/client id/client secret/signing key or an
    /// insecurely short signing key.
    /// </param>
    /// <returns>The middleware pipeline builder, for chaining.</returns>
    /// <remarks>
    /// Requires an <see cref="IOidcQueryStringReader{TContext}"/> for <typeparamref name="TContext"/>
    /// already registered in DI (this package carries no transport SDK dependency, so it cannot supply
    /// one itself - see that interface's remarks and, for the AWS Lambda API Gateway binding, this
    /// repo's <c>examples/AwsMesh</c> wiring).
    /// </remarks>
    public static IMiddlewarePipelineBuilder<TContext> UseMeshOidcAuth<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, MeshOidcOptions options)
        where TContext : IHttpContext
    {
        options.Validate();

        var signingKey = Encoding.UTF8.GetBytes(options.SigningKey);
        var configurationManager = OidcConfigurationManagerFactory.Create(options);
        var tokenHandler = new JsonWebTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidIssuers = ValidIssuersFor(options.Issuer),
            ValidAudiences = new[] { options.ClientId },
            ValidAlgorithms = options.ValidAlgorithms,
            ClockSkew = TimeSpan.FromMinutes(2),
            ConfigurationManager = configurationManager,

            // Explicit, not left to library defaults - see Benzene.Auth.OAuth2/CLAUDE.md's identical
            // reasoning: every one of these must genuinely run for the allowlists above to mean anything.
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
        };
        var idTokenValidator = new OidcIdTokenValidator(tokenHandler, validationParameters);

        app.Register(x =>
        {
            x.TryAddSingleton(_ => new HttpClient());
            x.AddSingleton(resolver => new OidcTokenExchangeClient(resolver.GetService<HttpClient>()));

            x.AddSingleton(resolver => new OidcLoginMiddleware<TContext>(
                options, signingKey, configurationManager,
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>(),
                resolver.GetService<IOidcQueryStringReader<TContext>>()));

            x.AddSingleton(resolver => new OidcCallbackMiddleware<TContext>(
                options, signingKey, configurationManager,
                resolver.GetService<OidcTokenExchangeClient>(), idTokenValidator,
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>(),
                resolver.GetService<IOidcQueryStringReader<TContext>>(),
                resolver.GetService<ILoggerFactory>().CreateLogger(LoggerCategory)));

            x.AddSingleton(resolver => new OidcLogoutMiddleware<TContext>(
                options,
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>()));

            x.AddSingleton(resolver => new OidcSessionGateMiddleware<TContext>(
                options, signingKey,
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>(),
                resolver.GetService<IOidcQueryStringReader<TContext>>()));
        });

        return app
            .Use<TContext, OidcLoginMiddleware<TContext>>()
            .Use<TContext, OidcCallbackMiddleware<TContext>>()
            .Use<TContext, OidcLogoutMiddleware<TContext>>()
            .Use<TContext, OidcSessionGateMiddleware<TContext>>();
    }

    /// <summary>
    /// The set of <c>iss</c> values accepted for a given configured issuer. Exact match for every
    /// provider, with one documented exception: Google's discovery document advertises
    /// <c>https://accounts.google.com</c>, but some Google-issued ID tokens historically carry <c>iss</c>
    /// as the bare <c>accounts.google.com</c> (no scheme) - both are accepted ONLY when the configured
    /// issuer is exactly Google's. Every other provider gets a single exact-match entry; this package
    /// does not generalize the quirk beyond the one provider it's documented for.
    /// </summary>
    internal static string[] ValidIssuersFor(string issuer)
    {
        return issuer == "https://accounts.google.com"
            ? new[] { "https://accounts.google.com", "accounts.google.com" }
            : new[] { issuer };
    }
}
