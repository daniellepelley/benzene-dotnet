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
    private readonly IMessageHandlerDefinitionLookUp _messageHandlerDefinitionLookUp;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultJsonSchemaProvider{TContext}"/> class.
    /// </summary>
    /// <param name="messageTopicGetter">Resolves the current message's topic from the context.</param>
    /// <param name="messageHandlerDefinitionLookUp">Looks up the handler (and its request type) registered for a topic.</param>
    public DefaultJsonSchemaProvider(IMessageTopicGetter<TContext> messageTopicGetter,
        IMessageHandlerDefinitionLookUp messageHandlerDefinitionLookUp)
    {
        _messageTopicGetter = messageTopicGetter;
        _messageHandlerDefinitionLookUp = messageHandlerDefinitionLookUp;
    }

    /// <inheritdoc />
    public Json.Schema.JsonSchema? Get(TContext context)
    {
        var topic = _messageTopicGetter.GetTopic(context);
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
