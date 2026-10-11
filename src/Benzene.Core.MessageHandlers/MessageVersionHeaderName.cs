using Benzene.Abstractions;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Registration helpers for the one version header (wire-contracts.md §2, versioning.md §2.1).
/// </summary>
/// <remarks>
/// <para>
/// The version header is a reserved name like the topic key, so it lives where every reserved name
/// lives: <see cref="IBenzeneWireNames.Version"/>, the single injectable value <c>AddBenzene()</c>
/// registers with the spec's defaults. Every transport's version getter reads it when a message is
/// handled, so the name is configured in one place rather than per transport, and registration order
/// relative to the transports does not matter.
/// </para>
/// <para>
/// One name, not a list: an application whose producers already use a different name configures that
/// name instead, and it <i>replaces</i> <c>benzene-version</c> rather than joining it. A
/// <c>version</c>/<c>x-version</c> header a producer sends for its own purposes is an ordinary header.
/// </para>
/// </remarks>
public static class MessageVersionHeaderNameExtensions
{
    /// <summary>
    /// Renames the version header, application-wide: registers an <see cref="IBenzeneWireNames"/> whose
    /// <see cref="IBenzeneWireNames.Version"/> is <paramref name="headerName"/> (and whose topic key is the
    /// default). Every transport's version getter reads it. An application that also renames the topic key
    /// registers one <see cref="BenzeneWireNames"/> carrying both names instead.
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <param name="headerName">The one header the version is read from.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer AddMessageVersionHeaderName(this IBenzeneServiceContainer services, string headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            throw new ArgumentException("The version header needs a name.", nameof(headerName));
        }

        services.AddSingleton<IBenzeneWireNames>(_ => new BenzeneWireNames(version: headerName));
        return services;
    }

    /// <summary>
    /// Registers <see cref="HeaderMessageVersionGetter{TContext}"/> as the
    /// <see cref="IMessageVersionGetter{TContext}"/>, reading the header named by the registered
    /// <see cref="IBenzeneWireNames.Version"/> (<c>benzene-version</c> by default). Transports call this
    /// instead of registering <see cref="HeaderMessageVersionGetter{TContext}"/> directly, so a renamed
    /// header reaches every transport uniformly.
    /// </summary>
    /// <typeparam name="TContext">The transport-specific context type.</typeparam>
    /// <param name="services">The service container to register into.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer AddHeaderMessageVersionGetter<TContext>(this IBenzeneServiceContainer services)
    {
        services.AddScoped<IMessageVersionGetter<TContext>>(resolver =>
            new HeaderMessageVersionGetter<TContext>(
                resolver.GetService<IMessageHeadersGetter<TContext>>(),
                resolver.TryGetService<IBenzeneWireNames>()?.Version));
        return services;
    }

    /// <summary>
    /// The <c>TryAddScoped</c> counterpart of <see cref="AddHeaderMessageVersionGetter{TContext}"/>, for
    /// callers that register the version getter conditionally.
    /// </summary>
    /// <typeparam name="TContext">The transport-specific context type.</typeparam>
    /// <param name="services">The service container to register into.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer TryAddHeaderMessageVersionGetter<TContext>(this IBenzeneServiceContainer services)
    {
        services.TryAddScoped<IMessageVersionGetter<TContext>>(resolver =>
            new HeaderMessageVersionGetter<TContext>(
                resolver.GetService<IMessageHeadersGetter<TContext>>(),
                resolver.TryGetService<IBenzeneWireNames>()?.Version));
        return services;
    }
}
