using System.Collections.Concurrent;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Json.Schema;
using Json.Schema.Generation;

namespace Benzene.JsonSchema;

/// <summary>
/// Default schema provider that generates a JSON schema from the request type of the message handler
/// registered for the current message's topic. Generated schemas use camelCase property names, matching
/// the framework's default serializer, and are cached per request type.
/// </summary>
/// <typeparam name="TContext">The context type to resolve the topic from.</typeparam>
public class DefaultJsonSchemaProvider<TContext> : IJsonSchemaProvider<TContext>
{
    private static readonly ConcurrentDictionary<Type, Json.Schema.JsonSchema> Cache = new();

    private readonly IMessageTopicGetter<TContext> _messageTopicGetter;
    private readonly IMessageVersionGetter<TContext>? _messageVersionGetter;
    private readonly IMessageHandlerDefinitionLookUp _messageHandlerDefinitionLookUp;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultJsonSchemaProvider{TContext}"/> class.
    /// </summary>
    /// <param name="messageTopicGetter">Resolves the current message's topic from the context.</param>
    /// <param name="messageHandlerDefinitionLookUp">Looks up the handler (and its request type) registered for a topic.</param>
    /// <param name="messageVersionGetter">
    /// Extracts the message's own version signal, combined with the topic before handler lookup
    /// (<c>GetVersionedTopic</c>) so a topic with 2+ registered handler versions validates against the
    /// version the request actually declares, not <c>VersionSelector</c>'s unversioned max-by-ordinal
    /// fallback. Optional (defaults to <c>null</c>, meaning "no version augmentation") only so existing
    /// direct constructions of this class without a version getter keep compiling; every DI-resolved
    /// instance gets one via <see cref="DependencyInjectionExtensions.AddJsonSchema"/>.
    /// </param>
    public DefaultJsonSchemaProvider(IMessageTopicGetter<TContext> messageTopicGetter,
        IMessageHandlerDefinitionLookUp messageHandlerDefinitionLookUp,
        IMessageVersionGetter<TContext>? messageVersionGetter = null)
    {
        _messageTopicGetter = messageTopicGetter;
        _messageHandlerDefinitionLookUp = messageHandlerDefinitionLookUp;
        _messageVersionGetter = messageVersionGetter;
    }

    /// <inheritdoc />
    public Json.Schema.JsonSchema? Get(TContext context)
    {
        var topic = _messageTopicGetter.GetVersionedTopic(context, _messageVersionGetter);
        if (string.IsNullOrEmpty(topic?.Id))
        {
            return null;
        }

        var messageHandlerDefinition = _messageHandlerDefinitionLookUp.FindHandler(topic);
        if (messageHandlerDefinition?.RequestType == null)
        {
            return null;
        }

        return Cache.GetOrAdd(messageHandlerDefinition.RequestType, requestType =>
            new JsonSchemaBuilder()
                .FromType(requestType, new SchemaGeneratorConfiguration
                {
                    PropertyNameResolver = PropertyNameResolvers.CamelCase
                })
                .Build());
    }
}
