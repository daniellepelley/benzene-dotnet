using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;

namespace Benzene.JsonSchema;

/// <summary>
/// Schema provider that validates against a hand-authored schema from a
/// <see cref="SuppliedJsonSchemaCatalog"/> when the current topic's request type is mapped, and
/// falls back to <see cref="DefaultJsonSchemaProvider{TContext}"/>'s generated schema otherwise.
/// Registered by <see cref="DependencyInjectionExtensions.AddSuppliedJsonSchemas"/>.
/// </summary>
/// <typeparam name="TContext">The context type to resolve the topic from.</typeparam>
public class SuppliedJsonSchemaProvider<TContext> : IJsonSchemaProvider<TContext>
{
    private readonly SuppliedJsonSchemaCatalog _catalog;
    private readonly IMessageTopicGetter<TContext> _messageTopicGetter;
    private readonly IMessageHandlerDefinitionLookUp _messageHandlerDefinitionLookUp;
    private readonly DefaultJsonSchemaProvider<TContext> _fallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuppliedJsonSchemaProvider{TContext}"/> class.
    /// </summary>
    /// <param name="catalog">The hand-authored schemas to check before falling back to the generated default.</param>
    /// <param name="messageTopicGetter">Resolves the current message's topic from the context.</param>
    /// <param name="messageHandlerDefinitionLookUp">Looks up the handler (and its request type) registered for a topic.</param>
    public SuppliedJsonSchemaProvider(SuppliedJsonSchemaCatalog catalog,
        IMessageTopicGetter<TContext> messageTopicGetter,
        IMessageHandlerDefinitionLookUp messageHandlerDefinitionLookUp)
    {
        _catalog = catalog;
        _messageTopicGetter = messageTopicGetter;
        _messageHandlerDefinitionLookUp = messageHandlerDefinitionLookUp;
        _fallback = new DefaultJsonSchemaProvider<TContext>(messageTopicGetter, messageHandlerDefinitionLookUp);
    }

    /// <inheritdoc />
    public Json.Schema.JsonSchema? Get(TContext context)
    {
        var topic = _messageTopicGetter.GetTopic(context);
        if (string.IsNullOrEmpty(topic?.Id))
        {
            return null;
        }

        var requestType = _messageHandlerDefinitionLookUp.FindHandler(topic)?.RequestType;
        if (requestType != null && _catalog.TryGetSchema(requestType, out var suppliedSchema))
        {
            return suppliedSchema;
        }

        return _fallback.Get(context);
    }
}
