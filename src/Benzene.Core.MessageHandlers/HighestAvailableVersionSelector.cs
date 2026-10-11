using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Messages;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// An opt-in <see cref="IVersionSelector"/> that keeps the selection policy Benzene used before the
/// spec's version-selection rule (docs/specification/core-concepts.md §2): the handler registered for
/// exactly the requested version, otherwise the highest registered version by ordinal comparison - for
/// a message with no version and for one naming a version no handler has.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> the default, and deliberately so: a caller that named a version and silently got
/// another version's behaviour is the failure versioning exists to prevent. The spec permits a different
/// policy only as an explicit, replaceable option, which is what this is. Register it to opt in:
/// <c>services.AddSingleton&lt;IVersionSelector, HighestAvailableVersionSelector&gt;()</c> (after
/// <c>AddBenzene()</c>, so it replaces the default).
/// </para>
/// <para>
/// An unversioned handler still answers a message with no version, as it always did.
/// </para>
/// </remarks>
public class HighestAvailableVersionSelector : IVersionSelector
{
    /// <inheritdoc />
    public IMessageHandlerDefinition? Select(ITopic topic, IMessageHandlerDefinition[] candidates)
    {
        var requested = topic.Version ?? string.Empty;
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.Topic.Version ?? string.Empty, requested, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        // StringComparer.Ordinal, not the default culture-sensitive comparer: a culture-sensitive max can
        // pick a different handler and vary by machine culture (["a","B"] -> "a" ordinal, "B" under most).
        return candidates.MaxBy(x => x.Topic.Version, StringComparer.Ordinal);
    }
}
