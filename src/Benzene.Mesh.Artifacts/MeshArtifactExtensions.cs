using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Http;
using Benzene.Http.Cors;
using Benzene.Mesh.Aggregator;

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
}
