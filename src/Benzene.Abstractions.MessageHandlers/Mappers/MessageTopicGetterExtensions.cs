using Benzene.Abstractions.Messages;

namespace Benzene.Abstractions.MessageHandlers.Mappers;

/// <summary>
/// Shared version-augmentation helper for resolving the topic a request actually declares. Any
/// consumer that resolves a handler via a topic (JSON Schema validation, tracing/log diagnostics,
/// message routing itself) must combine <see cref="IMessageTopicGetter{TContext}.GetTopic"/> with the
/// message's own version signal (<see cref="IMessageVersionGetter{TContext}"/>) before calling
/// <see cref="IMessageHandlerDefinitionLookUp.FindHandler"/> - otherwise, for a topic with 2+
/// registered handler versions, the lookup falls back to <c>VersionSelector</c>'s unversioned
/// max-by-ordinal default rather than the version the request declares (docs/specification/versioning.md
/// §2.3). This is the one implementation of that combination; every consumer - present and future -
/// should call it rather than re-deriving the same three lines (WP-P,
/// work/bug-fix-designs-round7-10-2026-08.md, tasks #69/#70).
/// </summary>
public static class MessageTopicGetterExtensions
{
    /// <summary>
    /// Resolves the topic <paramref name="context"/> declares, augmented with the message's own
    /// version signal when the topic getter didn't already supply one.
    /// </summary>
    /// <param name="messageTopicGetter">Extracts the (possibly version-less) topic from the context.</param>
    /// <param name="context">The transport-specific context for the incoming message.</param>
    /// <param name="messageVersionGetter">
    /// Extracts the payload schema version from the context, or <c>null</c> when no version getter is
    /// registered/available for this context type - in which case the topic is returned unaugmented
    /// (today's behaviour), rather than throwing.
    /// </param>
    /// <returns>The resolved topic - version-augmented when possible - or <c>null</c>/the "&lt;missing&gt;"
    /// sentinel topic when <paramref name="messageTopicGetter"/> couldn't resolve one, exactly as
    /// <see cref="IMessageTopicGetter{TContext}.GetTopic"/> would return on its own.</returns>
    public static ITopic? GetVersionedTopic<TContext>(
        this IMessageTopicGetter<TContext> messageTopicGetter,
        TContext context,
        IMessageVersionGetter<TContext>? messageVersionGetter)
    {
        var topic = messageTopicGetter.GetTopic(context);

        // A version already on the topic (e.g. an explicit UsePresetTopic(topicId, version)) is a
        // deliberate override and wins; the message's own version signal only fills the gap when the
        // topic getter didn't already supply one.
        if (messageVersionGetter != null && !string.IsNullOrEmpty(topic?.Id) && string.IsNullOrEmpty(topic.Version))
        {
            var version = messageVersionGetter.GetVersion(context);
            if (!string.IsNullOrEmpty(version))
            {
                topic = new VersionedTopic(topic.Id, version);
            }
        }

        return topic;
    }

    private sealed class VersionedTopic : ITopic
    {
        public VersionedTopic(string id, string version)
        {
            Id = id;
            Version = version;
        }

        public string Id { get; }

        public string Version { get; }
    }
}
