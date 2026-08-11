using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.MessageHandlers.MediaFormats;
using Benzene.Core.MessageHandlers.Request;
using Benzene.Core.MessageHandlers.Serialization;
using Benzene.RabbitMq.RabbitMqMessage;

namespace Benzene.RabbitMq;

/// <summary>
/// Registers the services required to consume RabbitMQ deliveries through a Benzene pipeline.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers everything <c>.UseMessageHandlers()</c> resolves per <see cref="RabbitMqContext"/>:
    /// the topic/version/headers/body getters, the result setter, media-format negotiation, and the
    /// request mapper. Called automatically by <see cref="Extensions.UseRabbitMq"/>.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container for method chaining.</returns>
    public static IBenzeneServiceContainer AddRabbitMq(this IBenzeneServiceContainer services)
        => services.AddRabbitMq(RabbitMqConstants.DefaultTopicHeader);

    /// <summary>
    /// Registers everything <c>.UseMessageHandlers()</c> resolves per <see cref="RabbitMqContext"/>,
    /// with the topic getter reading the given header key.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <param name="topicHeaderKey">
    /// The message-property header the topic is read from (see <see cref="RabbitMqConfig.TopicHeaderKey"/>).
    /// </param>
    /// <returns>The service container for method chaining.</returns>
    public static IBenzeneServiceContainer AddRabbitMq(this IBenzeneServiceContainer services, string topicHeaderKey)
    {
        services.TryAddScoped<JsonSerializer>();
        services.TryAddScoped<PresetTopicHolder>();

        // TryAdd (was plain Add - missed by the DI TryAdd conversion pass that covered the other
        // transports): a user registration made earlier (ConfigureServices runs before Configure,
        // where UseRabbitMq calls this) now wins over these per-context defaults, instead of being
        // silently shadowed by this later plain-Add registration.
        services.TryAddScoped<IMessageTopicGetter<RabbitMqContext>>(resolver =>
            new PresetTopicMessageTopicGetter<RabbitMqContext>(
                new RabbitMqMessageTopicGetter(ResolveTopicHeaderKey(resolver, topicHeaderKey)),
                resolver.GetService<PresetTopicHolder>()));
        services.TryAddHeaderMessageVersionGetter<RabbitMqContext>();
        services.TryAddScoped<IMessageHeadersGetter<RabbitMqContext>, RabbitMqMessageHeadersGetter>();
        services.TryAddScoped<IMessageBodyGetter<RabbitMqContext>, RabbitMqMessageBodyGetter>();
        services.TryAddScoped<IMessageHandlerResultSetter<RabbitMqContext>, RabbitMqMessageHandlerResultSetter>();
        services.AddMediaFormatNegotiation<RabbitMqContext>();
        services.TryAddScoped<IRequestMapper<RabbitMqContext>, MultiSerializerOptionsRequestMapper<RabbitMqContext>>();

        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.RabbitMq));

        return services;
    }

    /// <summary>
    /// Resolves the effective topic-header key: if the caller left <paramref name="topicHeaderKey"/>
    /// at this transport's own default, a DI-registered <see cref="Benzene.Abstractions.IBenzeneWireNames"/>
    /// (see <c>docs/specification/wire-contracts.md</c> §2) can still override it. An explicit
    /// non-default <paramref name="topicHeaderKey"/> always wins - resolved lazily (this factory runs
    /// at first use), so a later <see cref="Benzene.Abstractions.IBenzeneWireNames"/> registration is
    /// still picked up regardless of registration order.
    /// </summary>
    private static string ResolveTopicHeaderKey(Benzene.Abstractions.DI.IServiceResolver resolver, string topicHeaderKey)
    {
        return topicHeaderKey == RabbitMqConstants.DefaultTopicHeader
            ? resolver.TryGetService<Benzene.Abstractions.IBenzeneWireNames>()?.Topic ?? topicHeaderKey
            : topicHeaderKey;
    }
}
