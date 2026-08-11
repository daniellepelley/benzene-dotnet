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

namespace Benzene.Azure.ServiceBus;

/// <summary>
/// Provides extension methods for registering the standalone (non-Azure-Functions) Service Bus
/// consumer's services.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers the services required to process consumed Service Bus messages: message extraction
    /// and result recording.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container for method chaining.</returns>
    /// <remarks>
    /// Called automatically by <see cref="Extensions.UseServiceBus"/>; you don't normally need to
    /// call this directly.
    /// </remarks>
    public static IBenzeneServiceContainer AddServiceBusConsumer(this IBenzeneServiceContainer services)
        => services.AddServiceBusConsumer(ServiceBusConsumerMessageTopicGetter.DefaultTopicProperty);

    /// <summary>
    /// Registers the standalone Service Bus consumer's services, with the topic getter reading the
    /// given application-property key (see <see cref="ServiceBusConsumerMessageTopicGetter.DefaultTopicProperty"/>).
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <param name="topicPropertyKey">The application property the topic is read from.</param>
    /// <returns>The service container for method chaining.</returns>
    public static IBenzeneServiceContainer AddServiceBusConsumer(this IBenzeneServiceContainer services, string topicPropertyKey)
    {
        services.TryAddScoped<JsonSerializer>();
        services.TryAddScoped<PresetTopicHolder>();
        // Scoped per message, so a handler can request an explicit settlement (dead-letter/defer);
        // the worker reads it after the pipeline. Default (no override) unless the handler sets it.
        services.TryAddScoped<ServiceBusSettlementHolder>();

        // TryAdd: a user registration made earlier (ConfigureServices runs before Configure, where
        // UseServiceBus calls this) wins over these per-context defaults.
        services.TryAddScoped<IMessageTopicGetter<ServiceBusConsumerContext>>(resolver =>
            new PresetTopicMessageTopicGetter<ServiceBusConsumerContext>(
                new ServiceBusConsumerMessageTopicGetter(ResolveTopicPropertyKey(resolver, topicPropertyKey)),
                resolver.GetService<PresetTopicHolder>()));
        services.TryAddHeaderMessageVersionGetter<ServiceBusConsumerContext>();
        services.TryAddScoped<IMessageHeadersGetter<ServiceBusConsumerContext>, ServiceBusConsumerMessageHeadersGetter>();
        services.TryAddScoped<IMessageBodyGetter<ServiceBusConsumerContext>, ServiceBusConsumerMessageBodyGetter>();
        services.TryAddScoped<IMessageHandlerResultSetter<ServiceBusConsumerContext>, ServiceBusConsumerMessageHandlerResultSetter>();
        services.AddMediaFormatNegotiation<ServiceBusConsumerContext>();
        services.TryAddScoped<IRequestMapper<ServiceBusConsumerContext>, MultiSerializerOptionsRequestMapper<ServiceBusConsumerContext>>();

        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.ServiceBus));

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
        return topicPropertyKey == ServiceBusConsumerMessageTopicGetter.DefaultTopicProperty
            ? resolver.TryGetService<Benzene.Abstractions.IBenzeneWireNames>()?.Topic ?? topicPropertyKey
            : topicPropertyKey;
    }
}
