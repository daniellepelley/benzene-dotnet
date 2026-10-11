using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Messages;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// The default <see cref="IVersionSelector"/>: the selection rule of docs/specification/core-concepts.md
/// §2, in order.
/// </summary>
/// <remarks>
/// <para>For a message carrying version <c>v</c>:</para>
/// <list type="number">
/// <item>the handler registered for exactly <c>v</c> (ordinal comparison);</item>
/// <item>otherwise the handler the service can <b>cast</b> <c>v</c> to - a registered payload cast takes a
/// payload of version <c>v</c> to that handler's request type (<see cref="IPayloadVersionCasts"/>, supplied
/// by <c>Benzene.Core.Versioning</c>); an unversioned handler is such a target like any other. When
/// <c>v</c> reaches more than one handler, the topic's default handler wins if it is among them;
/// otherwise the <b>tie-break</b> is this port's documented, deterministic choice: the reachable handler
/// with the ordinal-greatest version, an unversioned handler counting as the lowest. Casting forward
/// (an upcast, which carries every field) is preferred over casting back (a downcast, which drops the
/// fields the older version lacks), and the same registration always resolves the same way.
/// <c>VersionSelectionStartUpCheck</c> logs each tie it can see at start-up, naming the handler chosen;</item>
/// <item>otherwise <c>null</c>: <c>not-found</c>, naming the topic and <c>v</c>.</item>
/// </list>
/// <para>
/// A message with no version goes to the topic's <b>default handler</b>
/// (<see cref="DefaultHandler"/>): the unversioned handler, else the one declared default
/// (<see cref="IMessageHandlerDefinition.IsDefault"/>), else <c>null</c>.
/// </para>
/// <para>
/// There is no "highest available" fallback. An application that wants one registers
/// <see cref="HighestAvailableVersionSelector"/> explicitly.
/// </para>
/// </remarks>
public class VersionSelector : IVersionSelector
{
    private readonly IPayloadVersionCasts? _casts;

    /// <summary>Initializes a selector that matches versions exactly, with no casting.</summary>
    public VersionSelector()
    {
    }

    /// <summary>Initializes a selector that also selects by registered payload casts.</summary>
    /// <param name="casts">The service's payload casts, or <c>null</c> when it registers none.</param>
    public VersionSelector(IPayloadVersionCasts? casts)
    {
        _casts = casts;
    }

    /// <inheritdoc />
    public IMessageHandlerDefinition? Select(ITopic topic, IMessageHandlerDefinition[] candidates)
    {
        var version = topic.Version;
        if (string.IsNullOrEmpty(version))
        {
            return DefaultHandler(candidates);
        }

        // Exact match. A loop rather than LINQ: this runs on every dispatch.
        for (var i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(candidates[i].Topic.Version, version, StringComparison.Ordinal))
            {
                return candidates[i];
            }
        }

        if (_casts == null)
        {
            return null;
        }

        return SelectByCast(_casts, topic.Id, version, candidates, out _);
    }

    /// <summary>
    /// The topic's default handler: the unversioned one, else the one declared default, else <c>null</c>.
    /// </summary>
    /// <param name="candidates">Every handler registered for the topic.</param>
    public static IMessageHandlerDefinition? DefaultHandler(IMessageHandlerDefinition[] candidates)
    {
        IMessageHandlerDefinition? declared = null;
        for (var i = 0; i < candidates.Length; i++)
        {
            if (string.IsNullOrEmpty(candidates[i].Topic.Version))
            {
                return candidates[i];
            }

            if (declared == null && candidates[i].IsDefault)
            {
                declared = candidates[i];
            }
        }

        return declared;
    }

    /// <summary>
    /// Step 2 of the rule: the handler <paramref name="version"/> can be cast to. Shared with the start-up
    /// check so the two can never disagree about how a tie resolves.
    /// </summary>
    /// <param name="casts">The service's payload casts.</param>
    /// <param name="topicId">The topic id.</param>
    /// <param name="version">The message's version.</param>
    /// <param name="candidates">Every handler registered for the topic.</param>
    /// <param name="reachable">Every handler <paramref name="version"/> can be cast to.</param>
    /// <returns>
    /// <c>null</c> when none is reachable; the single reachable handler; or, when several are, the
    /// default handler if it is one of them, else the tie-break (the ordinal-greatest version, an
    /// unversioned handler lowest).
    /// </returns>
    public static IMessageHandlerDefinition? SelectByCast(IPayloadVersionCasts casts, string topicId, string version,
        IMessageHandlerDefinition[] candidates, out IMessageHandlerDefinition[] reachable)
    {
        reachable = candidates.Where(x => casts.CanCast(topicId, version, x.RequestType)).ToArray();
        switch (reachable.Length)
        {
            case 0:
                return null;
            case 1:
                return reachable[0];
            default:
                var defaultHandler = DefaultHandler(candidates);
                if (defaultHandler != null && reachable.Contains(defaultHandler))
                {
                    return defaultHandler;
                }

                // Deterministic tie-break (core-concepts §2 leaves it to the port, documented above).
                // Versions are unique per topic, so the ordinal max is a single handler; "" sorts lowest.
                return reachable.MaxBy(x => x.Topic.Version ?? string.Empty, StringComparer.Ordinal);
        }
    }
}
