using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Middleware;

namespace Benzene.Http.RequestUrl;

/// <summary>Pipeline extensions for base-path stripping and <see cref="IRequestUrl"/>.</summary>
public static class RequestUrlExtensions
{
    /// <summary>
    /// Strips <paramref name="basePath"/> (e.g. <c>/tasks</c>) from every request before routing, so
    /// routes are declared without it, and answers <c>not-found</c> for a request outside it. Also makes
    /// <see cref="IRequestUrl"/> injectable. Place it first in the HTTP pipeline, before CORS and
    /// <c>UseMessageHandlers</c>.
    /// </summary>
    public static IMiddlewarePipelineBuilder<TContext> UseBasePath<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, string basePath) where TContext : IHttpContext
    {
        return app.UseRequestUrl(options => options.BasePath = basePath);
    }

    /// <summary>
    /// Makes <see cref="IRequestUrl"/> injectable (for absolute links), optionally with a base path, a
    /// public base URL, or trust in <c>X-Forwarded-Host</c>; see <see cref="RequestUrlOptions"/>. Place
    /// it first in the HTTP pipeline.
    /// </summary>
    public static IMiddlewarePipelineBuilder<TContext> UseRequestUrl<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, Action<RequestUrlOptions>? configure = null) where TContext : IHttpContext
    {
        var options = new RequestUrlOptions();
        configure?.Invoke(options);

        app.Register(x =>
        {
            x.TryAddScoped<RequestUrlHolder>();
            x.TryAddScoped<IRequestUrl>(resolver => resolver.GetService<RequestUrlHolder>());
        });

        return app.Use(resolver => new BasePathMiddleware<TContext>(
            options,
            resolver.GetService<IHttpRequestLocation<TContext>>(),
            resolver.GetService<IHttpRequestAdapter<TContext>>(),
            resolver.GetService<RequestUrlHolder>(),
            resolver.TryGetService<IMessageHandlerResultSetter<TContext>>()));
    }
}
