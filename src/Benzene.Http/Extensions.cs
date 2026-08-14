using System;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Http.Routing;

namespace Benzene.Http;

/// <summary>
/// Provides extension methods for configuring HTTP services and utilities in Benzene.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers HTTP message handler services, routing, and related infrastructure in the service container.
    /// </summary>
    /// <param name="services">The Benzene service container.</param>
    /// <returns>The service container for method chaining.</returns>
    /// <remarks>
    /// This method registers the following services:
    /// <list type="bullet">
    /// <item><description>Endpoint finders: Reflection, List, Dependency, and Composite finders with caching</description></item>
    /// <item><description>Route finder for matching HTTP requests to endpoints</description></item>
    /// <item><description>HTTP status code mapper with default REST conventions</description></item>
    /// <item><description>HTTP header mappings with default empty mappings</description></item>
    /// </list>
    /// The composite endpoint finder combines reflection-based, list-based, and dependency-based
    /// discovery strategies, with caching applied to the reflection finder for performance.
    /// </remarks>
    public static IBenzeneServiceContainer AddHttpMessageHandlers(this IBenzeneServiceContainer services)
    {
        // Compiles the route table at start-up, so UnroutedHttpEndpointCheck's error arrives at INIT
        // rather than on the first request that would have used the missing route.
        services.TryAddSingletonImplementation<Benzene.Abstractions.StartUpChecks.IStartUpCheck, Routing.HttpRouteStartUpCheck>();
        services.TryAddSingleton<ReflectionHttpEndpointFinder>();
        services.TryAddSingleton<ListHttpEndpointFinder>();
        services.TryAddSingleton<DependencyHttpEndpointFinder>();
        services.TryAddSingleton<IListHttpEndpointFinder, ListHttpEndpointFinder>();
        // The UnroutedHttpEndpointCheck runs first: it contributes no endpoints but throws when a
        // scanned handler carries [HttpEndpoint] without [Message] (and no explicit registration),
        // turning the silently-missing-route failure mode into a clear error when routes are built.
        // Registered as the concrete CompositeHttpEndpointFinder too, so AddHttpVersioning() can wrap it.
        services.TryAddSingleton<CompositeHttpEndpointFinder>(x =>
            new CompositeHttpEndpointFinder(
            new UnroutedHttpEndpointCheck(
            x.GetServices<Core.MessageHandlers.MessageHandlerCandidateTypes>(),
            x.GetService<Abstractions.MessageHandlers.IMessageHandlersFinder>()),
            new CacheHttpEndpointFinder(
            x.GetService<ReflectionHttpEndpointFinder>()),
            x.GetService<ListHttpEndpointFinder>(),
            x.GetService<DependencyHttpEndpointFinder>()));
        services.TryAddSingleton<IHttpEndpointFinder>(x => x.GetService<CompositeHttpEndpointFinder>());
        // The route table is compiled once in the singleton RouteFinder; a scoped MemoizingRouteFinder
        // wraps it so the topic getter, version getter, and enricher that each resolve the route for
        // one request share a single match instead of re-running it 2-3x per request.
        services.TryAddSingleton<RouteFinder>();
        services.TryAddScoped<IRouteFinder>(x => new MemoizingRouteFinder(x.GetService<RouteFinder>()));
        
        services.TryAddScoped<IHttpStatusCodeMapper, DefaultHttpStatusCodeMapper>();
        services.TryAddScoped<IHttpHeaderMappings, DefaultHttpHeaderMappings>();
        return services;
    }

    /// <summary>
    /// Wraps <typeparamref name="TContext"/>'s response payload mapper with
    /// <see cref="HttpProblemDetailsResponsePayloadMapper{TContext}"/>, so a failed result's problem
    /// document carries the numeric HTTP <c>status</c> member (work/problem-details-plan.md Phase 4;
    /// docs/specification/wire-contracts.md §2.1/§2.3). Call this for every context an HTTP-facing
    /// transport serves requests on (self-host ASP.NET Core, API Gateway v1/v2, Azure Functions
    /// ASP.NET, ...) - a transport that only carries a <c>BenzeneMessage</c> envelope (no real HTTP
    /// response line) must NOT call this for its envelope context, since the envelope's inner problem
    /// body is transport-neutral and must never carry a fabricated HTTP status.
    /// </summary>
    /// <remarks>
    /// Registered via <c>TryAddScoped</c> on the closed <see cref="IResponsePayloadMapper{TContext}"/>
    /// service type, so an application's own earlier registration for this exact context (made before
    /// the transport's <c>Add*MessageHandlers</c>/<c>AddApiGateway</c>/etc. call) wins outright - the
    /// same "TryAdd defaults, first registration wins" convention every other seam in this transport
    /// layer follows. The wrapped inner mapper is self-registered under its own concrete
    /// <see cref="DefaultResponsePayloadMapper{TContext}"/> type (mirroring
    /// <c>Benzene.Core.Versioning.PayloadVersionCastingExtensions.UsePayloadVersionCasting</c>'s same
    /// decorator-over-DI shape) so the decorator can resolve and wrap it without asking the container
    /// to resolve the interface it is itself about to be registered under.
    /// </remarks>
    /// <typeparam name="TContext">The transport-specific context type to fill in the numeric HTTP status for.</typeparam>
    /// <param name="services">The Benzene service container.</param>
    /// <returns>The service container for method chaining.</returns>
    public static IBenzeneServiceContainer UseHttpProblemDetailsStatus<TContext>(this IBenzeneServiceContainer services)
        where TContext : class
    {
        services.TryAddScoped<DefaultResponsePayloadMapper<TContext>>();
        services.TryAddScoped<IResponsePayloadMapper<TContext>>(resolver =>
            new HttpProblemDetailsResponsePayloadMapper<TContext>(
                resolver.GetService<DefaultResponsePayloadMapper<TContext>>(),
                resolver.GetService<IHttpStatusCodeMapper>()));
        return services;
    }

    /// <summary>
    /// Opts this app into path-based HTTP versioning: every <see cref="HttpEndpointAttribute"/> route is
    /// additionally exposed under a version segment (default <c>/v{version}/…</c>), so <c>/v1/orders</c> and
    /// <c>/v2/orders</c> both reach the same topic and the matched <c>version</c> route parameter drives
    /// version dispatch / payload upcasting. Off unless this is called. Call <b>after</b>
    /// <see cref="AddHttpMessageHandlers"/> (it replaces the endpoint finder it registers).
    /// </summary>
    /// <param name="services">The Benzene service container.</param>
    /// <param name="configure">Optional policy tweaks (segment template, whether to keep the unversioned route).</param>
    /// <returns>The service container for method chaining.</returns>
    public static IBenzeneServiceContainer AddHttpVersioning(this IBenzeneServiceContainer services, Action<HttpVersioningOptions>? configure = null)
    {
        var options = new HttpVersioningOptions();
        configure?.Invoke(options);

        services.AddSingleton(_ => options);
        // Replace the endpoint finder with the versioning decorator wrapping the composite the base
        // registration built - last registration wins, and RouteFinder/spec both read IHttpEndpointFinder.
        services.AddSingleton<IHttpEndpointFinder>(x =>
            new VersionedHttpEndpointFinder(x.GetService<CompositeHttpEndpointFinder>(), x.GetService<HttpVersioningOptions>()));
        return services;
    }
    
    /// <summary>
    /// Converts an HTTP request to lowercase for case-insensitive processing.
    /// </summary>
    /// <param name="source">The HTTP request to convert.</param>
    /// <returns>
    /// A new <see cref="HttpRequest"/> with the path, method, and header names converted to lowercase.
    /// </returns>
    /// <remarks>
    /// This method is useful for normalizing HTTP requests for case-insensitive comparison,
    /// particularly for routing and header matching. Header values are preserved as-is; only
    /// header names are converted to lowercase.
    /// </remarks>
    public static HttpRequest AsLowerCase(this HttpRequest source)
    {
        return new HttpRequest
        {
            Path = source.Path.ToLowerInvariant(),
            Method = source.Method.ToLowerInvariant(),
            Headers = source.Headers.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value)
        };
    }
}
