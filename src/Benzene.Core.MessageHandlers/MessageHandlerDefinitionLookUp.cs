using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Messages;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Default <see cref="IMessageHandlerDefinitionLookUp"/> implementation that resolves the requested
/// version via <see cref="IVersionSelector"/> against the shared <see cref="MessageHandlerDefinitionIndex"/>.
/// </summary>
/// <remarks>
/// This type is constructed fresh per message (scoped DI lifetime), matching every other per-message
/// pipeline collaborator; the aggregation and de-duplication over every registered
/// <see cref="IMessageHandlersFinder"/> is done once, in the singleton <see cref="MessageHandlerDefinitionIndex"/>,
/// not repeated per instance.
/// </remarks>
public class MessageHandlerDefinitionLookUp : IMessageHandlerDefinitionLookUp
{
    private readonly MessageHandlerDefinitionIndex _index;
    private readonly IVersionSelector _versionSelector;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageHandlerDefinitionLookUp"/> class.
    /// </summary>
    /// <param name="index">The shared index over every registered handler finder's definitions.</param>
    /// <param name="versionSelector">Resolves which version of a handler to use when several are registered for the same topic.</param>
    public MessageHandlerDefinitionLookUp(MessageHandlerDefinitionIndex index, IVersionSelector versionSelector)
    {
        _index = index;
        _versionSelector = versionSelector;
    }

    /// <summary>
    /// Finds the handler that answers <paramref name="topic"/>: the topic id's registered handlers, chosen
    /// between by <see cref="IVersionSelector"/> (docs/specification/core-concepts.md §2 - exact version,
    /// else a cast-reachable handler, else none; no version means the topic's default handler).
    /// </summary>
    /// <param name="topic">The topic (id + requested version) to find a handler for.</param>
    /// <returns>
    /// The matching handler definition, or <c>null</c> when no handler is registered for the topic id or
    /// none answers the requested version.
    /// </returns>
    public IMessageHandlerDefinition FindHandler(ITopic topic)
    {
        var handlers = _index.GetByTopicId(topic.Id);
        if (handlers.Length == 0)
        {
            return null;
        }

        // Selection runs even for a single registered handler. It used to be skipped there ("the max of a
        // one-element set is that element"), which routed a message naming any version at all to a topic's
        // only handler - a v2 message reached an unversioned handler. The rule is exact-or-cast, so the
        // version has to be looked at; the selector is allocation-free on the exact-match path.
        return _versionSelector.Select(topic, handlers);
    }

    /// <summary>
    /// Returns every distinct handler definition (by topic id + version) across all registered finders.
    /// </summary>
    /// <returns>All registered handler definitions.</returns>
    public IMessageHandlerDefinition[] GetAllHandlers()
    {
        return _index.GetAll();
    }
}
