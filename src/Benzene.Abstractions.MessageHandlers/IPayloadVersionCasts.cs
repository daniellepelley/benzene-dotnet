namespace Benzene.Abstractions.MessageHandlers;

/// <summary>
/// What payload casts a service has registered, as version selection needs to see them
/// (docs/specification/core-concepts.md §2, step 2): a message whose version has no handler of its own
/// still has one when the service can cast its payload to that handler's request type.
/// </summary>
/// <remarks>
/// Implemented by <c>Benzene.Core.Versioning</c> over its caster registry and registered by
/// <c>AddPayloadVersioning</c>/<c>RegisterPayloadSchemaVersions</c>; nothing registers it otherwise, and
/// selection then stops at the exact match. It lives here, rather than in the versioning package, so the
/// default <see cref="IVersionSelector"/> can consult it without the dispatch core knowing about casting.
/// </remarks>
public interface IPayloadVersionCasts
{
    /// <summary>
    /// Whether a payload of <paramref name="topicId"/> in <paramref name="fromVersion"/> reaches
    /// <paramref name="requestType"/>: a cast (direct or chained) is registered from that version to that
    /// type, or the version names that type itself so no cast is needed.
    /// </summary>
    /// <param name="topicId">The topic id.</param>
    /// <param name="fromVersion">The version the payload arrived in.</param>
    /// <param name="requestType">A candidate handler's request type.</param>
    bool CanCast(string topicId, string fromVersion, Type requestType);

    /// <summary>
    /// Every version a payload of <paramref name="topicId"/> can arrive in and be cast from - what the
    /// start-up check walks to find an ambiguous cast before the first message does.
    /// </summary>
    /// <param name="topicId">The topic id.</param>
    IReadOnlyCollection<string> GetVersions(string topicId);
}
