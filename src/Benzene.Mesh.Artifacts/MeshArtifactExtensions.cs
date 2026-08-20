using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Http;
using Benzene.Http.Cors;
using Benzene.Http.Routing;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Dispatch;
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

    /// <summary>
    /// Guards the mesh dispatch endpoint: a required CSRF header, an attributable identity, a payload
    /// bound, and a per-identity rate limit — with the per-target limit and the audit record applied
    /// by the handler, where the parsed target exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mount directly below the session gate and above the endpoint that routes
    /// <c>benzene:mesh:dispatch</c>. It fails closed on identity, so mounting it ABOVE the session gate
    /// refuses every request rather than allowing unattributed ones — a wiring mistake that announces
    /// itself instead of quietly disabling the audit trail.
    /// </para>
    /// <para>
    /// This is not the flood defence and must not be described as one: see
    /// <see cref="Benzene.Mesh.Dispatch.MeshDispatchRateLimiter"/> for what an in-process counter
    /// actually guarantees on a multi-instance host, and put the hard limit at the edge.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The HTTP context type.</typeparam>
    /// <param name="app">The middleware pipeline builder.</param>
    /// <param name="options">Guard configuration; defaults are sized for one human iterating on a payload.</param>
    /// <returns>The middleware pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseMeshDispatchGuard<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, MeshDispatchGuardOptions? options = null)
        where TContext : IHttpContext
    {
        var guardOptions = options ?? new MeshDispatchGuardOptions();

        app.Register(x =>
        {
            x.AddSingleton(guardOptions);
            // ONE limiter for the process: per-instance counters are the whole mechanism, so a
            // per-scope limiter would count to one and bound nothing.
            x.TryAddSingleton(_ => new MeshDispatchRateLimiter());
            // Scoped: it carries who is asking, for this request only.
            x.TryAddScoped(_ => new MeshDispatchIdentity());
            x.AddScoped(resolver => new MeshDispatchGuardMiddleware<TContext>(
                guardOptions,
                resolver.GetService<MeshDispatchIdentity>(),
                resolver.GetService<MeshDispatchRateLimiter>(),
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>(),
                resolver.TryGetService<IRouteFinder>(),
                resolver.TryGetService<ILogger<MeshDispatchGuardMiddleware<TContext>>>()
            ));
        });

        return app.Use<TContext, MeshDispatchGuardMiddleware<TContext>>();
    }
}
