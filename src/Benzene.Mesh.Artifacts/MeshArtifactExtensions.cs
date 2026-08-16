using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Http;
using Benzene.Http.Cors;
using Benzene.Http.Routing;
using Benzene.Mesh.Aggregator;
using Microsoft.Extensions.Logging;

namespace Benzene.Mesh.Artifacts;

/// <summary>Pipeline wiring for <see cref="MeshArtifactMiddleware{TContext}"/>.</summary>
public static class MeshArtifactExtensions
{
    /// <summary>
    /// Serves the mesh catalog artifacts from the registered <see cref="IMeshArtifactStore"/>.
    /// Pass a <paramref name="corsSettings"/> to also stamp CORS headers on the artifact responses
    /// (e.g. so the AsyncAPI Studio deep-link can fetch <c>asyncapi.json</c> cross-origin).
    /// </summary>
    /// <typeparam name="TContext">The HTTP context type.</typeparam>
    /// <param name="app">The middleware pipeline builder.</param>
    /// <param name="corsSettings">
    /// When supplied, stamps <c>Access-Control-*</c> headers on the artifact responses for the
    /// origins it allows. Omit to serve the artifacts with no CORS headers.
    /// </param>
    /// <returns>The middleware pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseMeshArtifacts<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, CorsSettings? corsSettings = null)
        where TContext : IHttpContext
    {
        app.Register(x =>
            x.AddSingleton(resolver => new MeshArtifactMiddleware<TContext>(
                resolver.GetService<IMeshArtifactStore>(),
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>(),
                corsSettings
            )));

        return app.Use<TContext, MeshArtifactMiddleware<TContext>>();
    }

    /// <summary>
    /// Guards the mesh's refresh endpoint (<c>POST /mesh/refresh</c> by default) with a custom-header
    /// CSRF check and a manifest-age throttle - see <see cref="MeshRefreshGuardMiddleware{TContext}"/>
    /// for exactly what each check does and, importantly, what the throttle is <em>not</em> (it is a
    /// rate limiter, not a distributed lock).
    /// </summary>
    /// <typeparam name="TContext">The HTTP context type.</typeparam>
    /// <param name="app">The middleware pipeline builder.</param>
    /// <param name="options">
    /// The guard's configuration. Omit for the defaults (<c>/mesh/refresh</c>, the
    /// <c>X-Benzene-Refresh</c> header, a 30-second window).
    /// </param>
    /// <returns>The middleware pipeline builder, for chaining.</returns>
    /// <remarks>
    /// Wire this <b>after</b> whatever authenticates the pipeline and <b>before</b> the message-handler
    /// middleware that dispatches the refresh handler. Order matters in both directions: in front of
    /// authentication it would answer <c>403</c> to anonymous callers that should get <c>401</c>, and
    /// behind the handler router it would never run at all. Registered scoped (not singleton) because it
    /// resolves the request-scoped <see cref="IRouteFinder"/>.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<TContext> UseMeshRefreshGuard<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, MeshRefreshGuardOptions? options = null)
        where TContext : IHttpContext
    {
        var guardOptions = options ?? new MeshRefreshGuardOptions();

        app.Register(x =>
            x.AddScoped(resolver => new MeshRefreshGuardMiddleware<TContext>(
                guardOptions,
                resolver.GetService<IMeshArtifactStore>(),
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>(),
                resolver.TryGetService<IRouteFinder>(),
                resolver.TryGetService<ILogger<MeshRefreshGuardMiddleware<TContext>>>()
            )));

        return app.Use<TContext, MeshRefreshGuardMiddleware<TContext>>();
    }
}
