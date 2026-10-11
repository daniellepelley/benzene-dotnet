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
        // routes on a different header (the default reads the `topic` header, falling back to the broker topic).
        // The topic header key follows a registered IBenzeneWireNames (as the other standalone consumers do), and
        // the headers getter leaves out the same key the topic getter consumes (headersExclude).
        services.TryAddScoped<IMessageTopicGetter<KafkaRecordContext<TKey, TValue>>>(resolver =>
            new KafkaMessageTopicGetter<TKey, TValue>(ResolveTopicHeaderKey(resolver)));
        services.TryAddHeaderMessageVersionGetter<KafkaRecordContext<TKey, TValue>>();
        services.TryAddScoped<IMessageHeadersGetter<KafkaRecordContext<TKey, TValue>>>(resolver =>
            new KafkaMessageHeadersGetter<TKey, TValue>(ResolveTopicHeaderKey(resolver)));
        services.TryAddScoped<IMessageBodyGetter<KafkaRecordContext<TKey, TValue>>, KafkaMessageBodyGetter<TKey, TValue>>();
        services.TryAddScoped<IMessageHandlerResultSetter<KafkaRecordContext<TKey, TValue>>, KafkaMessageHandlerResultSetter<TKey, TValue>>();

        // Declare this transport for ITransportsInfo (the same name the per-invocation current-transport
        // records), matching the AWS/Azure Kafka adapters - it was previously omitted here.
        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.Kafka));

        return services;
    }

    private static string ResolveTopicHeaderKey(Benzene.Abstractions.DI.IServiceResolver resolver)
    {
        return resolver.TryGetService<Benzene.Abstractions.IBenzeneWireNames>()?.Topic ?? Benzene.Abstractions.BenzeneWireNames.DefaultTopic;
    }
}