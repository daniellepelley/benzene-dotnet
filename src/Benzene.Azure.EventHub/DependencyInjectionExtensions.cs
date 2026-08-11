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

namespace Benzene.Azure.EventHub;

/// <summary>
/// Provides extension methods for registering the standalone (non-Azure-Functions) Event Hub
/// consumer's services.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers the services required to process consumed events: message extraction and result
    /// recording.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container for method chaining.</returns>
    /// <remarks>
    /// Called automatically by <see cref="Extensions.UseEventHub"/>; you don't normally need to
    /// call this directly.
    /// </remarks>
    public static IBenzeneServiceContainer AddEventHubConsumer(this IBenzeneServiceContainer services)
        => services.AddEventHubConsumer(EventHubConsumerMessageTopicGetter.DefaultTopicProperty);

    /// <summary>
    /// Registers the standalone Event Hub consumer's services, with the topic getter reading the given
    /// event-property key (see <see cref="EventHubConsumerMessageTopicGetter.DefaultTopicProperty"/>).
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <param name="topicPropertyKey">The event property the topic is read from.</param>
    /// <returns>The service container for method chaining.</returns>
    public static IBenzeneServiceContainer AddEventHubConsumer(this IBenzeneServiceContainer services, string topicPropertyKey)
    {
        services.TryAddScoped<JsonSerializer>();
        services.TryAddScoped<PresetTopicHolder>();

        // TryAdd: a user registration made earlier (ConfigureServices runs before Configure, where
        // UseEventHub calls this) wins over these per-context defaults.
        services.TryAddScoped<IMessageTopicGetter<EventHubConsumerContext>>(resolver =>
            new PresetTopicMessageTopicGetter<EventHubConsumerContext>(
                new EventHubConsumerMessageTopicGetter(ResolveTopicPropertyKey(resolver, topicPropertyKey)),
                resolver.GetService<PresetTopicHolder>()));
        services.TryAddHeaderMessageVersionGetter<EventHubConsumerContext>();
        services.TryAddScoped<IMessageHeadersGetter<EventHubConsumerContext>, EventHubConsumerMessageHeadersGetter>();
        services.TryAddScoped<IMessageBodyGetter<EventHubConsumerContext>, EventHubConsumerMessageBodyGetter>();
        services.TryAddScoped<IMessageHandlerResultSetter<EventHubConsumerContext>, EventHubConsumerMessageHandlerResultSetter>();
        services.AddMediaFormatNegotiation<EventHubConsumerContext>();
        services.TryAddScoped<IRequestMapper<EventHubConsumerContext>, MultiSerializerOptionsRequestMapper<EventHubConsumerContext>>();

        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.EventHub));

        return services;
    }

    /// <summary>
    /// Resolves the effective topic-property key: if the caller left <paramref name="topicPropertyKey"/>
    /// at this transport's own default, a DI-registered <see cref="Benzene.Abstractions.IBenzeneWireNames"/>
    /// (see <c>docs/specification/wire-contracts.md</c> §2) can still override it. An explicit
    /// non-default <paramref name="topicPropertyKey"/> always wins - resolved lazily (this factory
    /// runs at first use), so a later <see cref="Benzene.Abstractions.IBenzeneWireNames"/> registration
    /// is still picked up regardless of registration order.
    /// </summary>
    private static string ResolveTopicPropertyKey(Benzene.Abstractions.DI.IServiceResolver resolver, string topicPropertyKey)
    {
        return topicPropertyKey == EventHubConsumerMessageTopicGetter.DefaultTopicProperty
            ? resolver.TryGetService<Benzene.Abstractions.IBenzeneWireNames>()?.Topic ?? topicPropertyKey
            : topicPropertyKey;
    }
}
