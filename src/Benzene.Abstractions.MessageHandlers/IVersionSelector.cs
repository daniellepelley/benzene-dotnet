using Benzene.Abstractions.Messages;

namespace Benzene.Abstractions.MessageHandlers;

/// <summary>
/// Chooses which of a topic's registered handlers answers a message, by the message's version
/// (docs/specification/core-concepts.md §2, "Version selection"). Called only when at least one
/// handler is registered for the topic id.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation, <c>Benzene.Core.MessageHandlers.VersionSelector</c>, applies the spec's
/// rule: for a message carrying version <c>v</c>, the handler registered for exactly <c>v</c>; else the
/// handler the service can <b>cast</b> <c>v</c> to (<see cref="IPayloadVersionCasts"/>), with the topic's
/// default handler winning a tie; else none (<c>not-found</c>). A message with no version goes to the
/// topic's default handler - the unversioned one, else the one declared default
/// (<see cref="IMessageHandlerDefinition.IsDefault"/>) - else none.
/// </para>
/// <para>
/// There is deliberately no "highest available" fallback in the default: serving another version's
/// behaviour to a caller that named a version is the failure versioning exists to prevent. A different
/// policy (e.g. <c>HighestAvailableVersionSelector</c>) is an explicit, replaceable registration.
/// </para>
/// </remarks>
public interface IVersionSelector
{
    /// <summary>Selects the handler that answers <paramref name="topic"/>.</summary>
    /// <param name="topic">
    /// The message's topic: its id and requested version (<see cref="ITopic.Version"/> empty when the
    /// message carried no version signal).
    /// </param>
    /// <param name="candidates">
    /// Every handler registered for <paramref name="topic"/>'s id - never empty, one per version.
    /// </param>
    /// <returns>The handler to route to, or <c>null</c> when none answers this version (<c>not-found</c>).</returns>
    IMessageHandlerDefinition? Select(ITopic topic, IMessageHandlerDefinition[] candidates);
}
