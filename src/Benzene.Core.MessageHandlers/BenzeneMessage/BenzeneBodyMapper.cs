using System.Text;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Core.Messages;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Core.MessageHandlers.BenzeneMessage;

/// <summary>
/// Default <see cref="IMessageGetter{TContext}"/> for the <c>BenzeneMessage</c> transport-agnostic
/// message format: extracts topic, body, and headers from <see cref="BenzeneMessageContext"/>'s
/// underlying <see cref="IBenzeneMessageRequest"/>. Also implements
/// <see cref="IMessageBodyBytesGetter{TContext}"/> (UTF-8 encoding the string body), making
/// <c>BenzeneMessage</c> the reference transport for Phase 4's byte-oriented request-mapping path.
/// </summary>
/// <remarks>
/// Despite the file name, this class is <c>BenzeneMessageGetter</c>, not <c>BenzeneBodyMapper</c> -
/// it maps more than just the body (topic and headers too). Registered by <c>AddBenzeneMessage</c>
/// against <see cref="IMessageGetter{TContext}"/> and each of its constituent interfaces.
/// </remarks>
public class BenzeneMessageGetter : IMessageGetter<BenzeneMessageContext>, IMessageBodyBytesGetter<BenzeneMessageContext>
{
    private readonly IServiceResolver? _serviceResolver;
    private readonly ResolvedTopicCache<BenzeneMessageContext>? _resolvedTopicCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="BenzeneMessageGetter"/> class.
    /// </summary>
    /// <param name="serviceResolver">
    /// Used to LAZILY resolve <see cref="IMessageVersionGetter{TContext}"/> inside <see cref="GetTopic"/>
    /// (task #98, work/archive/bug-fix-designs-round10-2026-08.md WP-V), rather than taking it as an ordinary
    /// constructor dependency. This is deliberate, not a style choice: this class is registered as the
    /// DI implementation of <em>both</em> <c>IMessageGetter&lt;BenzeneMessageContext&gt;</c> and
    /// <c>IMessageHeadersGetter&lt;BenzeneMessageContext&gt;</c>, and the default
    /// <see cref="IMessageVersionGetter{TContext}"/> (<c>HeaderMessageVersionGetter</c>) itself depends
    /// on <c>IMessageHeadersGetter&lt;BenzeneMessageContext&gt;</c> - so a constructor dependency on
    /// <c>IMessageVersionGetter&lt;BenzeneMessageContext&gt;</c> here re-enters this same class's own
    /// construction one level down and DEADLOCKS Microsoft.Extensions.DependencyInjection's per-scoped-
    /// service lock (verified: it hangs indefinitely, not a fast "circular dependency" exception, since
    /// the two entries into the cycle are different service types that its cycle detector doesn't
    /// correlate). Resolving lazily, after this instance's own construction has already returned, breaks
    /// the cycle. Optional: when <c>null</c> (a direct construction in a test) the topic is returned
    /// unaugmented - never throws.
    /// </param>
    /// <param name="resolvedTopicCache">
    /// The scoped per-message topic cache so the joined topic is resolved once and reused by every
    /// consumer of <c>IMessageGetter&lt;BenzeneMessageContext&gt;</c>, mirroring
    /// <see cref="Benzene.Core.MessageHandlers.MessageGetter{TContext}"/>'s cache. Optional: when
    /// <c>null</c> the topic is joined on every call, exactly as before this cache existed.
    /// </param>
    public BenzeneMessageGetter(
        IServiceResolver? serviceResolver = null,
        ResolvedTopicCache<BenzeneMessageContext>? resolvedTopicCache = null)
    {
        _serviceResolver = serviceResolver;
        _resolvedTopicCache = resolvedTopicCache;
    }

    /// <summary>
    /// Gets the request's headers, or an empty dictionary if none are set.
    /// </summary>
    /// <param name="context">The context to extract headers from.</param>
    /// <returns>The request's headers.</returns>
    public IDictionary<string, string> GetHeaders(BenzeneMessageContext context)
    {
        return context.BenzeneMessageRequest.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the request's topic, joined with the message's own version signal (task #98,
    /// work/archive/bug-fix-designs-round10-2026-08.md WP-V), or the <see cref="Constants.Missing"/> sentinel
    /// topic if the request has no topic set.
    /// </summary>
    /// <param name="context">The context to extract the topic from.</param>
    /// <returns>The request's topic.</returns>
    /// <remarks>
    /// The raw envelope topic (<see cref="RawTopicGetter"/>) never carries a version itself: the
    /// payload schema version is resolved by the configurable, priority-ordered
    /// <see cref="IMessageVersionGetter{TContext}"/> (default <c>benzene-version</c> &gt;
    /// <c>version</c> &gt; <c>x-version</c>) and joined in here via the shared
    /// <see cref="MessageTopicGetterExtensions.GetVersionedTopic{TContext}"/> helper, like every other
    /// transport's <c>IMessageGetter</c>. Baking the raw <c>"version"</c> header into the raw topic
    /// directly would make <see cref="MessageRouter{TContext}"/> (and every other consumer of this
    /// topic) treat it as a preset override and skip the version getter, defeating both the configured
    /// header order and any app that narrows the list (docs/specification/versioning.md §2.1). This
    /// class used to leave the join to each individual consumer (the router did it locally, and every
    /// other reader - <c>UseMeshTrace</c>, <c>Benzene.CloudService</c>, health checks - silently read
    /// the version-less topic instead); it is now resolved once here so <c>GetTopic</c> means the same
    /// thing to every caller (#98).
    /// </remarks>
    public ITopic GetTopic(BenzeneMessageContext context)
    {
        if (_resolvedTopicCache is null)
        {
            return RawTopicGetter.Instance.GetVersionedTopic(context, ResolveVersionGetter())!;
        }

        if (_resolvedTopicCache.HasValue)
        {
            return _resolvedTopicCache.Topic!;
        }

        var topic = RawTopicGetter.Instance.GetVersionedTopic(context, ResolveVersionGetter());
        _resolvedTopicCache.Set(topic);
        return topic!;
    }

    // See the constructor's doc comment on _serviceResolver for why this is resolved lazily here
    // rather than taken as a constructor dependency.
    private IMessageVersionGetter<BenzeneMessageContext>? ResolveVersionGetter() =>
        _serviceResolver?.TryGetService<IMessageVersionGetter<BenzeneMessageContext>>();

    /// <summary>
    /// The version-less envelope topic extraction, isolated behind <see cref="IMessageTopicGetter{TContext}"/>
    /// so <see cref="GetTopic"/> can join it with <see cref="IMessageVersionGetter{TContext}"/> via the
    /// shared <see cref="MessageTopicGetterExtensions.GetVersionedTopic{TContext}"/> helper instead of
    /// re-deriving the join inline (docs/specification/versioning.md §2.3, WP-P). Stateless, so one
    /// shared instance is enough.
    /// </summary>
    private sealed class RawTopicGetter : IMessageTopicGetter<BenzeneMessageContext>
    {
        public static readonly RawTopicGetter Instance = new();

        public ITopic GetTopic(BenzeneMessageContext context)
        {
            if (context?.BenzeneMessageRequest?.Topic == null)
            {
                return new Topic(Messages.Constants.Missing.Id);
            }

            return new Topic(context.BenzeneMessageRequest.Topic);
        }
    }

    /// <summary>
    /// Gets the request's raw body.
    /// </summary>
    /// <param name="context">The context to extract the body from.</param>
    /// <returns>The request's raw body.</returns>
    public string GetBody(BenzeneMessageContext context)
    {
        return context.BenzeneMessageRequest.Body;
    }

    /// <summary>
    /// Gets the request's raw body as UTF-8 bytes.
    /// </summary>
    /// <param name="context">The context to extract the body from.</param>
    /// <returns>The request's raw body, UTF-8 encoded, or empty if there is no body.</returns>
    public ReadOnlyMemory<byte> GetBodyBytes(BenzeneMessageContext context)
    {
        var body = context.BenzeneMessageRequest.Body;
        return string.IsNullOrEmpty(body) ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body);
    }
}
