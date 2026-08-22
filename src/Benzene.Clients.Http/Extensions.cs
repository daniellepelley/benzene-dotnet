using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.HealthChecks.Core;
using Microsoft.Extensions.Logging;

namespace Benzene.Clients.Http;

/// <summary>Pipeline-builder and DI extensions for the outbound plain-HTTP and BenzeneMessage-over-HTTP send paths.</summary>
public static class Extensions
{
    /// <summary>
    /// Registers a scoped <see cref="HttpBenzeneMessageClient"/> (as itself and as
    /// <see cref="IBenzeneMessageClient"/>) that POSTs the BenzeneMessage envelope to <paramref name="url"/>
    /// — the target service's BenzeneMessage endpoint. Resolves the <see cref="HttpClient"/> from DI (you
    /// must register one, e.g. via <c>AddHttpClient()</c>/<c>AddSingleton&lt;HttpClient&gt;()</c>), along with
    /// an optional logger and the ambient cancellation-token accessor.
    /// </summary>
    /// <param name="services">The service container to register on.</param>
    /// <param name="url">The target BenzeneMessage endpoint URL (verbatim request URI; absolute or relative to the client's <c>BaseAddress</c>).</param>
    /// <param name="healthCheck">
    /// When <c>true</c> (the default) a non-destructive reachability check for <paramref name="url"/> is
    /// auto-registered on the deep <c>healthcheck</c> layer (never a Kubernetes probe — see
    /// <see cref="IDependencyHealthCheck"/>). Pass <c>false</c> to opt out. Deduped by <c>"HttpBenzeneMessage:{url}"</c>.
    /// </param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddHttpBenzeneMessageClient(this IBenzeneServiceContainer services, string url, bool healthCheck = true)
    {
        services.AddScoped<IBenzeneMessageClient>(resolver => new HttpBenzeneMessageClient(
            resolver.GetService<HttpClient>(), url,
            resolver.TryGetService<ILogger<HttpBenzeneMessageClient>>(),
            resolver.TryGetService<ICancellationTokenAccessor>()));

        if (healthCheck)
        {
            services.AddDependencyHealthCheck(
                resolver => new HttpBenzeneMessageHealthCheck(resolver.GetService<HttpClient>(), url,
                    cancellation: resolver.TryGetService<ICancellationTokenAccessor>()),
                $"HttpBenzeneMessage:{url}");
        }

        return services;
    }

    /// <summary>
    /// Adds an explicit <see cref="HttpBenzeneMessageHealthCheck"/> for <paramref name="url"/> — a
    /// non-destructive POST of a <paramref name="healthCheckTopic"/> envelope to the target's BenzeneMessage
    /// endpoint. Use this to health-check a target you call through some other wiring; the
    /// <c>AddHttpBenzeneMessageClient</c> default already auto-wires one.
    /// </summary>
    /// <param name="builder">The health check builder to add the check to.</param>
    /// <param name="url">The target BenzeneMessage endpoint URL to probe.</param>
    /// <param name="healthCheckTopic">The topic to POST (defaults to <c>benzene:healthcheck</c>).</param>
    /// <returns>The health check builder, for chaining.</returns>
    public static IHealthCheckBuilder AddHttpBenzeneMessageHealthCheck(this IHealthCheckBuilder builder, string url, string healthCheckTopic = Benzene.Abstractions.BenzeneTopic.HealthCheck)
    {
        return builder.AddHealthCheck(resolver => new HttpBenzeneMessageHealthCheck(
            resolver.GetService<HttpClient>(), url, healthCheckTopic, resolver.TryGetService<ICancellationTokenAccessor>()));
    }

    /// <summary>Adds an <see cref="HttpClientMiddleware"/> built from the given client to the pipeline.</summary>
    /// <param name="app">The pipeline builder to add the middleware to.</param>
    /// <param name="httpClient">The <see cref="HttpClient"/> to send with.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<HttpSendMessageContext> UseHttpClient(
        this IMiddlewarePipelineBuilder<HttpSendMessageContext> app, HttpClient httpClient)
    {
        return app.Use(_ => new HttpClientMiddleware(httpClient));
    }

    /// <summary>Adds an <see cref="HttpClientMiddleware"/> resolved from the service container to the pipeline.</summary>
    /// <param name="app">The pipeline builder to add the middleware to.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<HttpSendMessageContext> UseHttpClient(
        this IMiddlewarePipelineBuilder<HttpSendMessageContext> app)
    {
        app.Register(x => x.AddScoped<HttpClientMiddleware>());
        return app.Use<HttpSendMessageContext, HttpClientMiddleware>();
    }

    /// <summary>Converts <typeparamref name="TContext"/> to <typeparamref name="TContextOut"/> and runs the given pipeline.</summary>
    /// <param name="app">The pipeline builder to convert.</param>
    /// <param name="converter">Converts the context and maps the response back.</param>
    /// <param name="middlewarePipeline">The already-built pipeline the converted context runs through.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> Convert<TContext, TContextOut>(this IMiddlewarePipelineBuilder<TContext> app,
        IContextConverter<TContext, TContextOut> converter, IMiddlewarePipeline<TContextOut> middlewarePipeline)
    {
        return app.Use(serviceResolver => new ContextConverterMiddleware<TContext, TContextOut>(converter, middlewarePipeline, serviceResolver));
    }

    /// <summary>Converts <typeparamref name="TContext"/> to <typeparamref name="TContextOut"/> and builds the inner pipeline from <paramref name="action"/>.</summary>
    /// <param name="app">The pipeline builder to convert.</param>
    /// <param name="converter">Converts the context and maps the response back.</param>
    /// <param name="action">Configures the inner pipeline the converted context runs through.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> Convert<TContext, TContextOut>(this IMiddlewarePipelineBuilder<TContext> app,
        IContextConverter<TContext, TContextOut> converter, Action<IMiddlewarePipelineBuilder<TContextOut>> action)
    {
        var middlewarePipeline = app.CreateMiddlewarePipeline(action);

        return app.Use(serviceResolver => new ContextConverterMiddleware<TContext, TContextOut>(converter, middlewarePipeline, serviceResolver));
    }

    /// <summary>Converts an <see cref="IBenzeneClientContext{TRequest,TResponse}"/> pipeline to send plain HTTP, using a custom inner pipeline.</summary>
    /// <param name="app">The client pipeline builder to convert.</param>
    /// <param name="verb">The HTTP verb to send.</param>
    /// <param name="path">The request path (or full URL).</param>
    /// <param name="action">Configures the inner HTTP send pipeline.</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<IBenzeneClientContext<TRequest, TResponse>> UseHttp<TRequest, TResponse>(this IMiddlewarePipelineBuilder<IBenzeneClientContext<TRequest, TResponse>> app,
        string verb, string path, Action<IMiddlewarePipelineBuilder<HttpSendMessageContext>> action)
    {
        return Convert(app, new HttpContextConverter<TRequest, TResponse>(verb, path), action);
    }

    /// <summary>Converts an <see cref="IBenzeneClientContext{TRequest,TResponse}"/> pipeline to send plain HTTP, using the default <see cref="HttpClientMiddleware"/> configuration.</summary>
    /// <param name="app">The client pipeline builder to convert.</param>
    /// <param name="verb">The HTTP verb to send.</param>
    /// <param name="path">The request path (or full URL).</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<IBenzeneClientContext<TRequest, TResponse>> UseHttp<TRequest, TResponse>(this IMiddlewarePipelineBuilder<IBenzeneClientContext<TRequest, TResponse>> app,
        string verb, string path)
    {
        return app.Convert(new HttpContextConverter<TRequest, TResponse>(verb, path), builder => builder.UseHttpClient());
    }

    /// <summary>
    /// Converts an outbound route pipeline (<c>OutboundRoutingBuilder.Route</c>) to POST a BenzeneMessage
    /// envelope (<c>{ topic, headers, body }</c>) to another Benzene service's BenzeneMessage endpoint,
    /// using a custom middleware configuration - the HTTP counterpart of <c>UseInProcess</c> and of the
    /// AWS Lambda invoke path, and the shape a service uses to call another service over plain HTTP.
    /// </summary>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <param name="url">The target BenzeneMessage endpoint URL (verbatim request URI; absolute, or relative to the <c>HttpClient</c>'s <c>BaseAddress</c>).</param>
    /// <param name="action">Configures the inner HTTP send pipeline (e.g. <c>builder =&gt; builder.UseHttpClient(myHttpClient)</c>).</param>
    /// <returns>The pipeline builder, for chaining.</returns>
    /// <remarks>
    /// This is a shorthand over the explicit form, which you can write yourself from public API:
    /// <c>app.Convert(new OutboundBenzeneMessageHttpContextConverter(url), action)</c>. Drop to that when
    /// you want to supply your own <c>ISerializer</c>.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseBenzeneMessageOverHttp(
        this IMiddlewarePipelineBuilder<OutboundContext> app, string url,
        Action<IMiddlewarePipelineBuilder<HttpSendMessageContext>> action)
    {
        return app.Convert(new OutboundBenzeneMessageHttpContextConverter(url), action);
    }

    /// <summary>
    /// Converts an outbound route pipeline (<c>OutboundRoutingBuilder.Route</c>) to POST a BenzeneMessage
    /// envelope to <paramref name="url"/>, using the default <see cref="HttpClientMiddleware"/>
    /// configuration (which resolves its <see cref="HttpClient"/> from the container - register one, e.g.
    /// via <c>AddHttpClient()</c>/<c>AddSingleton&lt;HttpClient&gt;()</c>).
    /// </summary>
    /// <param name="app">The outbound pipeline builder to convert.</param>
    /// <param name="url">The target BenzeneMessage endpoint URL.</param>
    /// <param name="healthCheck">
    /// When <c>true</c> (the default) a non-destructive reachability check for <paramref name="url"/> is
    /// auto-registered on the deep <c>healthcheck</c> layer (never a Kubernetes probe - see
    /// <see cref="IDependencyHealthCheck"/>). Pass <c>false</c> to opt out. Deduped by
    /// <c>"HttpBenzeneMessage:{url}"</c>, the same key <see cref="AddHttpBenzeneMessageClient"/> uses.
    /// </param>
    /// <returns>The pipeline builder, for chaining.</returns>
    /// <remarks>
    /// One rung of shorthand over
    /// <see cref="UseBenzeneMessageOverHttp(IMiddlewarePipelineBuilder{OutboundContext}, string, Action{IMiddlewarePipelineBuilder{HttpSendMessageContext}})"/>:
    /// it is exactly <c>app.UseBenzeneMessageOverHttp(url, builder =&gt; builder.UseHttpClient())</c> plus
    /// the health-check registration. Drop one level to that to pass an explicit <see cref="HttpClient"/>
    /// or add middleware around the send.
    /// </remarks>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseBenzeneMessageOverHttp(
        this IMiddlewarePipelineBuilder<OutboundContext> app, string url, bool healthCheck = true)
    {
        if (healthCheck)
        {
            app.Register(services => services.AddDependencyHealthCheck(
                resolver => new HttpBenzeneMessageHealthCheck(resolver.GetService<HttpClient>(), url,
                    cancellation: resolver.TryGetService<ICancellationTokenAccessor>()),
                $"HttpBenzeneMessage:{url}"));
        }

        return app.UseBenzeneMessageOverHttp(url, builder => builder.UseHttpClient());
    }
}
