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

namespace Benzene.Aws.Lambda.S3;

/// <summary>
/// Provides extension methods for registering S3 event notification services.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers the services required to process S3 event notifications: topic/body/header
    /// extraction, request mapping, and transport info, so S3 records can be routed to message
    /// handlers by their event name.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container for method chaining.</returns>
    /// <remarks>
    /// Called automatically by <see cref="Extensions.UseS3"/>; you don't normally need to call this directly.
    /// </remarks>
    public static IBenzeneServiceContainer AddS3(this IBenzeneServiceContainer services)
    {
        services.TryAddScoped<JsonSerializer>();
        services.TryAddScoped<PresetTopicHolder>();

        // TryAdd: a user registration made earlier (ConfigureServices runs before Configure, where
        // UseS3 calls this) wins over these per-context defaults, matching Benzene.Aws.Lambda.Sns.
        services.TryAddScoped<IMessageTopicGetter<S3RecordContext>>(resolver =>
            new PresetTopicMessageTopicGetter<S3RecordContext>(new S3MessageTopicGetter(), resolver.GetService<PresetTopicHolder>()));
        services.TryAddHeaderMessageVersionGetter<S3RecordContext>();
        services.TryAddScoped<IMessageHeadersGetter<S3RecordContext>, S3MessageHeadersGetter>();
        services.TryAddScoped<IMessageBodyGetter<S3RecordContext>, S3MessageBodyGetter>();
        services.TryAddScoped<IMessageHandlerResultSetter<S3RecordContext>, S3MessageHandlerResultSetter>();
        services.AddMediaFormatNegotiation<S3RecordContext>();
        services
            .TryAddScoped<IRequestMapper<S3RecordContext>,
                MultiSerializerOptionsRequestMapper<S3RecordContext>>();

        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.S3));

        return services;
    }
}

