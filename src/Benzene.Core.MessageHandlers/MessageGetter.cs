using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Default <see cref="IMessageGetter{TContext}"/> implementation that composes the individually
/// registered <see cref="IMessageTopicGetter{TContext}"/>, <see cref="IMessageBodyGetter{TContext}"/>
/// and <see cref="IMessageHeadersGetter{TContext}"/> for <typeparamref name="TContext"/> into a
/// single facade, so callers that need all three don't have to depend on each mapper individually.
/// <see cref="GetTopic"/> also joins in the optionally-registered <see cref="IMessageVersionGetter{TContext}"/>
/// (task #98, work/bug-fix-designs-round10-2026-08.md WP-V), so this facade's topic is always the same
/// version-resolved <see cref="ITopic"/> <see cref="MessageRouter{TContext}"/> routes on.
/// </summary>
/// <typeparam name="TContext">The transport-specific context type messages are extracted from.</typeparam>
public class MessageGetter<TContext> : IMessageGetter<TContext>
{
    private readonly IMessageHeadersGetter<TContext> _messageHeadersGetter;
    private readonly IMessageTopicGetter<TContext> _messageTopicGetter;
    private readonly IMessageBodyGetter<TContext> _messageBodyGetter;
    private readonly IMessageVersionGetter<TContext>? _messageVersionGetter;
    private readonly ResolvedTopicCache<TContext>? _resolvedTopicCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageGetter{TContext}"/> class.
    /// </summary>
    /// <param name="messageTopicGetter">Extracts the topic from a <typeparamref name="TContext"/>.</param>
    /// <param name="messageBodyGetter">Extracts the body from a <typeparamref name="TContext"/>.</param>
    /// <param name="messageHeadersGetter">Extracts the headers from a <typeparamref name="TContext"/>.</param>
    /// <param name="resolvedTopicCache">
    /// The scoped per-message topic cache so the topic is extracted once and reused by every consumer.
    /// Optional: when <c>null</c> (e.g. a direct construction in a test) the topic is extracted on every
    /// call, exactly as before this cache existed. DI supplies it in a running app.
    /// </param>
    /// <param name="messageVersionGetter">
    /// Joins the message's own version signal into the topic <see cref="GetTopic"/> returns, via the
    /// shared <see cref="MessageTopicGetterExtensions.GetVersionedTopic{TContext}"/> helper (task #98,
    /// work/bug-fix-designs-round10-2026-08.md WP-V) - the same join <see cref="MessageRouter{TContext}"/>
    /// used to perform locally and throw away, leaving every other consumer of this facade (mesh trace,
    /// health checks, cloud-service dispatch, ...) reading a version-blind topic. Optional: when
    /// <c>null</c> (no version getter registered for <typeparamref name="TContext"/>, or a direct
    /// construction in a test) the topic is returned unaugmented - never throws.
    /// </param>
    public MessageGetter(IMessageTopicGetter<TContext> messageTopicGetter, IMessageBodyGetter<TContext> messageBodyGetter, IMessageHeadersGetter<TContext> messageHeadersGetter, ResolvedTopicCache<TContext>? resolvedTopicCache = null, IMessageVersionGetter<TContext>? messageVersionGetter = null)
    {
        _messageHeadersGetter = messageHeadersGetter;
        _messageTopicGetter = messageTopicGetter;
        _messageBodyGetter = messageBodyGetter;
        _resolvedTopicCache = resolvedTopicCache;
        _messageVersionGetter = messageVersionGetter;
    }

    /// <summary>
    /// Gets the raw body of the message, via the registered <see cref="IMessageBodyGetter{TContext}"/>.
    /// </summary>
    /// <param name="context">The transport-specific context to extract the body from.</param>
    /// <returns>The raw message body.</returns>
    public string GetBody(TContext context)
    {
        return _messageBodyGetter.GetBody(context);
    }

    /// <summary>
    /// Gets the headers of the message, via the registered <see cref="IMessageHeadersGetter{TContext}"/>.
    /// </summary>
    /// <param name="context">The transport-specific context to extract the headers from.</param>
    /// <returns>The message headers.</returns>
    public IDictionary<string, string> GetHeaders(TContext context)
    {
        return _messageHeadersGetter.GetHeaders(context);
    }

    /// <summary>
    /// Gets the topic of the message, via the registered <see cref="IMessageTopicGetter{TContext}"/>,
    /// joined with the message's own version signal (task #98, work/bug-fix-designs-round10-2026-08.md
    /// WP-V) when the topic getter didn't already supply one.
    /// </summary>
    /// <param name="context">The transport-specific context to extract the topic from.</param>
    /// <returns>The message's version-joined <see cref="ITopic"/>.</returns>
    public ITopic GetTopic(TContext context)
    {
        // Extract once per message and reuse: the router, health-check middleware and every tracing
        // decorator's tagging all call this, so on a traced request it would otherwise re-run the
        // transport's topic extraction (and the version join below) ~a dozen times for the same answer.
        // The join itself happens here, not in the router - MessageRouter used to be the only caller
        // that combined IMessageTopicGetter with IMessageVersionGetter (via GetVersionedTopic), so every
        // other consumer of this cache (Benzene.Mesh.Wire, Benzene.CloudService, Benzene.HealthChecks,
        // Benzene.Auth.Core, ...) read a version-blind topic even though ITopic has a Version property.
        if (_resolvedTopicCache is null)
        {
            return _messageTopicGetter.GetVersionedTopic(context, _messageVersionGetter)!;
        }

        if (_resolvedTopicCache.HasValue)
        {
            return _resolvedTopicCache.Topic!;
        }

        var topic = _messageTopicGetter.GetVersionedTopic(context, _messageVersionGetter);
        _resolvedTopicCache.Set(topic);
        return topic!;
    }
}
