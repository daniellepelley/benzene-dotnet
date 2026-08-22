using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.MediaFormats;
using Benzene.Abstractions.Middleware;

namespace Benzene.NewtonsoftJson;

/// <summary>
/// DI registration extensions for Json.NET-backed content negotiation support.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers <see cref="NewtonsoftJsonMediaFormat{TContext}"/> as an <see cref="IMediaFormat{TContext}"/>
    /// for every context type (open generic).
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer AddNewtonsoftJson(this IBenzeneServiceContainer services)
    {
        services.TryAddSingleton<JsonSerializer>();
        services.AddSingleton(typeof(IMediaFormat<>), typeof(NewtonsoftJsonMediaFormat<>));
        return services;
    }

    /// <summary>
    /// Registers <see cref="NewtonsoftJsonMediaFormat{TContext}"/> as an <see cref="IMediaFormat{TContext}"/>
    /// for <typeparamref name="TContext"/> only.
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer AddNewtonsoftJson<TContext>(this IBenzeneServiceContainer services) where TContext : class
    {
        services.TryAddSingleton<JsonSerializer>();
        services.AddSingleton<IMediaFormat<TContext>, NewtonsoftJsonMediaFormat<TContext>>();
        return services;
    }

    /// <summary>
    /// Registers Json.NET-backed JSON support for <typeparamref name="TContext"/> onto a middleware
    /// pipeline builder.
    /// </summary>
    /// <param name="source">The pipeline builder to register Json.NET support onto.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseNewtonsoftJson<TContext>(this IMiddlewarePipelineBuilder<TContext> source)
        where TContext : class
    {
        source.Register(x => x.AddNewtonsoftJson<TContext>());
        return source;
    }
}
