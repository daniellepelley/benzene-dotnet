using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Kafka.Core.KafkaMessage;

namespace Benzene.Kafka.Core;

/// <summary>DI registration for the inbound Kafka consumer pipeline getters/setters.</summary>
public static class DependencyInjectionExtensions
{
    /// <summary>Registers the default inbound Kafka message getters/setter and declares the Kafka transport.</summary>
    /// <param name="services">The service container to register on.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddKafka<TKey, TValue>(this IBenzeneServiceContainer services)
    {
        // TryAdd: a user registration made earlier (ConfigureServices runs before Configure, where
        // UseKafka calls this) wins over these per-context defaults - e.g. a custom topic getter that
        // routes on a header instead of the broker topic.
        services.TryAddScoped<IMessageTopicGetter<KafkaRecordContext<TKey, TValue>>, KafkaMessageTopicGetter<TKey, TValue>>();
        services.TryAddHeaderMessageVersionGetter<KafkaRecordContext<TKey, TValue>>();
        services.TryAddScoped<IMessageHeadersGetter<KafkaRecordContext<TKey, TValue>>, KafkaMessageHeadersGetter<TKey, TValue>>();
        services.TryAddScoped<IMessageBodyGetter<KafkaRecordContext<TKey, TValue>>, KafkaMessageBodyGetter<TKey, TValue>>();
        services.TryAddScoped<IMessageHandlerResultSetter<KafkaRecordContext<TKey, TValue>>, KafkaMessageHandlerResultSetter<TKey, TValue>>();

        // Declare this transport for ITransportsInfo (the same name the per-invocation current-transport
        // records), matching the AWS/Azure Kafka adapters - it was previously omitted here.
        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.Kafka));

        return services;
    }
}