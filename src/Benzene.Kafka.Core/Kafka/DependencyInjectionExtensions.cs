using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Kafka.Core.Kafka;

/// <summary>DI registration for the outbound Kafka send-pipeline getters.</summary>
public static class DependencyInjectionExtensions
{
    /// <summary>Registers the default outbound Kafka message getters used by <see cref="KafkaMessageContextConverter{TContext}"/>.</summary>
    /// <param name="services">The service container to register on.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddSendKafka(this IBenzeneServiceContainer services)
    {
        services.AddScoped<IMessageTopicGetter<KafkaSendMessageContext>, KafkaSendMessageTopicGetter>();
        services.AddScoped<IMessageHeadersGetter<KafkaSendMessageContext>, KafkaSendMessageHeadersGetter>();
        services.AddScoped<IMessageBodyGetter<KafkaSendMessageContext>, KafkaSendMessageBodyGetter>();

        return services;
    }
}