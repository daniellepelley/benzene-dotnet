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

namespace Benzene.Aws.Lambda.EventBridge;

/// <summary>
/// Provides extension methods for registering EventBridge message handling services.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers the services required to route EventBridge events to message handlers.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container, for method chaining.</returns>
    public static IBenzeneServiceContainer AddEventBridge(this IBenzeneServiceContainer services)
    {
        services.TryAddScoped<JsonSerializer>();
        services.TryAddScoped<PresetTopicHolder>();

        // TryAdd: a user registration made earlier (ConfigureServices runs before Configure, where
        // UseEventBridge calls this) wins over these per-context defaults, matching Benzene.Aws.Lambda.Sns.
        services.TryAddScoped<IMessageTopicGetter<EventBridgeContext>>(resolver =>
            new PresetTopicMessageTopicGetter<EventBridgeContext>(new EventBridgeMessageTopicGetter(), resolver.GetService<PresetTopicHolder>()));
        services.TryAddHeaderMessageVersionGetter<EventBridgeContext>();
        services.TryAddScoped<IMessageHeadersGetter<EventBridgeContext>, EventBridgeMessageHeadersGetter>();
        services.TryAddScoped<IMessageBodyGetter<EventBridgeContext>, EventBridgeMessageBodyGetter>();
        services.TryAddScoped<IMessageBodySetter<EventBridgeContext>, EventBridgeMessageBodySetter>();
        services.TryAddScoped<IMessageHandlerResultSetter<EventBridgeContext>, EventBridgeMessageHandlerResultSetter>();
        services.AddMediaFormatNegotiation<EventBridgeContext>();
        services
            .TryAddScoped<IRequestMapper<EventBridgeContext>,
                MultiSerializerOptionsRequestMapper<EventBridgeContext>>();

        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.EventBridge));

        return services;
    }
}
