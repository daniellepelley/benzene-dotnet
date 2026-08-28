namespace Benzene.Abstractions.MessageHandlers;

/// <summary>
/// The registry of handler definitions typically populated at startup from one or more
/// <see cref="IMessageHandlersFinder"/>s, and consumed via an <see cref="IMessageHandlerDefinitionLookUp"/>
/// to resolve a handler for an incoming topic. <see cref="Add"/> can also be called at runtime, after
/// startup - a definition added later is picked up by lookups that consult a cached index built over
/// this list (the concrete <c>MessageHandlerDefinitionIndex</c> in <c>Benzene.Core.MessageHandlers</c>
/// documents the invalidation mechanism that makes this safe). An implementation of this interface
/// must therefore make <see cref="Add"/> safe to call concurrently with any read it exposes.
/// </summary>
public interface IMessageHandlersList
{
    /// <summary>Registers a handler definition. Safe to call at runtime, concurrently with reads.</summary>
    /// <param name="messageHandlerDefinition">The handler definition to add.</param>
    void Add(IMessageHandlerDefinition messageHandlerDefinition);
}