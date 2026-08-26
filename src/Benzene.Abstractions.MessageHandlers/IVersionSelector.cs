namespace Benzene.Abstractions.MessageHandlers;

/// <summary>
/// Chooses which handler version to route a message to when a topic has multiple registered
/// handler versions, allowing several versions of the same message contract to coexist (e.g. so
/// callers can migrate at their own pace). The default implementation matches the requested version
/// exactly if available, otherwise falls back to the highest available version.
/// </summary>
public interface IVersionSelector
{
    /// <summary>Selects a handler version to use for the requested version.</summary>
    /// <param name="requestedVersion">
    /// The version requested by the incoming message, or <c>null</c>/empty for an unversioned message
    /// (see <see cref="Benzene.Abstractions.Messages.Mappers.IMessageVersionGetter{TContext}"/> - "null/empty
    /// means the topic's default version"). <see cref="Benzene.Core.MessageHandlers.MessageHandlerDefinitionLookUp"/>,
    /// the only caller, passes exactly that for every unversioned message.
    /// </param>
    /// <param name="availableVersions">The versions of the handler that are registered for the topic.</param>
    /// <returns>
    /// The version to route to, which must be one of <paramref name="availableVersions"/> - a contract
    /// that presumes a non-empty <paramref name="availableVersions"/> array and so cannot hold when it's
    /// empty. Unreachable via the default lookup path today: <c>MessageHandlerDefinitionLookUp</c>
    /// early-returns before calling <see cref="Select"/> when zero handlers are registered for the
    /// topic, and fast-paths (skips calling <see cref="Select"/> at all) when exactly one is.
    /// </returns>
    string Select(string? requestedVersion, string[] availableVersions);
}