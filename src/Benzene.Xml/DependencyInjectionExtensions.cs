using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.MediaFormats;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.MediaFormats;

namespace Benzene.Xml;

/// <summary>
/// DI registration extensions for XML content negotiation support.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers <see cref="XmlMediaFormat{TContext}"/> as an <see cref="IMediaFormat{TContext}"/> for
    /// every context type (open generic).
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <param name="configure">Optional configuration of <see cref="XmlOptions"/> (e.g. <see cref="XmlOptions.MaxDepth"/>).</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer AddXml(this IBenzeneServiceContainer services, Action<XmlOptions>? configure = null)
    {
        services.TryAddSingleton(BuildSerializer(configure));
        services.AddSingleton(typeof(IMediaFormat<>), typeof(XmlMediaFormat<>));
        return services;
    }

    /// <summary>
    /// Registers <see cref="XmlMediaFormat{TContext}"/> as an <see cref="IMediaFormat{TContext}"/> for
    /// <typeparamref name="TContext"/> only.
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <param name="configure">Optional configuration of <see cref="XmlOptions"/> (e.g. <see cref="XmlOptions.MaxDepth"/>).</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer AddXml<TContext>(this IBenzeneServiceContainer services, Action<XmlOptions>? configure = null) where TContext : class
    {
        services.TryAddSingleton(BuildSerializer(configure));
        services.AddSingleton<IMediaFormat<TContext>, XmlMediaFormat<TContext>>();
        return services;
    }

    /// <summary>
    /// Registers XML support for <typeparamref name="TContext"/> onto a middleware pipeline builder.
    /// </summary>
    /// <param name="source">The pipeline builder to register XML support onto.</param>
    /// <param name="configure">Optional configuration of <see cref="XmlOptions"/> (e.g. <see cref="XmlOptions.MaxDepth"/>).</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseXml<TContext>(this IMiddlewarePipelineBuilder<TContext> source,
        Action<XmlOptions>? configure = null)
        where TContext : class
    {
        source.Register(x => x.AddXml<TContext>(configure));
        return source;
    }

    private static XmlSerializer BuildSerializer(Action<XmlOptions>? configure)
    {
        var options = new XmlOptions();
        configure?.Invoke(options);
        return new XmlSerializer(options);
    }
}
