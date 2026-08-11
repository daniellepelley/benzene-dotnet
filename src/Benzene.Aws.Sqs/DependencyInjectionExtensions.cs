using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Aws.Sqs.Consumer;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Core.MessageHandlers.MediaFormats;
using Benzene.Core.MessageHandlers.Request;
using Benzene.Core.MessageHandlers.Serialization;

namespace Benzene.Aws.Sqs;

/// <summary>
/// Provides extension methods for registering the standalone (non-Lambda) SQS polling consumer's services.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers the services required to poll and process SQS messages: the SQS client factory,
    /// message extraction, and request mapping.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container for method chaining.</returns>
    /// <remarks>
    /// Called automatically by <see cref="Extensions.UseSqs"/>; you don't normally need to call this directly.
    /// </remarks>
    public static IBenzeneServiceContainer AddSqsConsumer(this IBenzeneServiceContainer services)
        => services.AddSqsConsumer(SqsConsumerMessageTopicGetter.DefaultTopicAttribute);

    /// <summary>
    /// Registers the standalone SQS polling consumer's services, with the topic getter reading the
    /// given message-attribute key (see <see cref="SqsConsumerMessageTopicGetter.DefaultTopicAttribute"/>).
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <param name="topicAttributeKey">The message attribute the topic is read from.</param>
    /// <returns>The service container for method chaining.</returns>
    public static IBenzeneServiceContainer AddSqsConsumer(this IBenzeneServiceContainer services, string topicAttributeKey)
    {
        services.TryAddScoped<JsonSerializer>();
        services.TryAddScoped<PresetTopicHolder>();

        services.AddScoped<ISqsClientFactory, SqsClientFactory>();
        // TryAdd: a user registration made earlier (ConfigureServices runs before Configure, where
        // UseSqs calls this) wins over these per-context defaults.
        services.TryAddScoped<IMessageTopicGetter<SqsConsumerMessageContext>>(resolver =>
            new PresetTopicMessageTopicGetter<SqsConsumerMessageContext>(
                new SqsConsumerMessageTopicGetter(ResolveTopicAttributeKey(resolver, topicAttributeKey)),
                resolver.GetService<PresetTopicHolder>()));
        services.TryAddHeaderMessageVersionGetter<SqsConsumerMessageContext>();
        services.TryAddScoped<IMessageHeadersGetter<SqsConsumerMessageContext>, SqsConsumerMessageHeadersGetter>();
        services.TryAddScoped<IMessageBodyGetter<SqsConsumerMessageContext>, SqsConsumerMessageBodyGetter>();
        services.TryAddScoped<IMessageHandlerResultSetter<SqsConsumerMessageContext>, SqsConsumerMessageHandlerResultSetter>();
        services.AddMediaFormatNegotiation<SqsConsumerMessageContext>();
        services
            .TryAddScoped<IRequestMapper<SqsConsumerMessageContext>,
                MultiSerializerOptionsRequestMapper<SqsConsumerMessageContext>>();

        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.Sqs));

        return services;
    }

    /// <summary>
    /// Resolves the effective topic-attribute key: if the caller left <paramref name="topicAttributeKey"/>
    /// at this transport's own default, a DI-registered <see cref="Benzene.Abstractions.IBenzeneWireNames"/>
    /// (see <c>docs/specification/wire-contracts.md</c> §2) can still override it - so replacing
    /// <see cref="Benzene.Abstractions.IBenzeneWireNames"/> changes the topic key here too, without
    /// every transport needing its own separate override. An explicit non-default
    /// <paramref name="topicAttributeKey"/> always wins - resolved at first use (this factory runs
    /// lazily), not at registration time, so a later <see cref="Benzene.Abstractions.IBenzeneWireNames"/>
    /// registration is still picked up regardless of registration order.
    /// </summary>
    private static string ResolveTopicAttributeKey(Benzene.Abstractions.DI.IServiceResolver resolver, string topicAttributeKey)
    {
        return topicAttributeKey == SqsConsumerMessageTopicGetter.DefaultTopicAttribute
            ? resolver.TryGetService<Benzene.Abstractions.IBenzeneWireNames>()?.Topic ?? topicAttributeKey
            : topicAttributeKey;
    }
}
