using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.Exceptions;

namespace Benzene.Core.MessageHandlers.StartUpChecks;

/// <summary>
/// Two handlers claiming the same topic and version, across all finders.
/// </summary>
/// <remarks>
/// <para>
/// The runtime is inconsistent about this and always has been.
/// <c>ReflectionMessageHandlersFinder</c> throws when it sees the collision within its own scan, but
/// <c>MessageHandlerDefinitionIndex</c> groups by (topic, version) and takes <c>.First()</c>, so the
/// same mistake made across two finders — a reflection-discovered <c>[Message]</c> handler and an
/// explicitly registered one — silently drops one of them and answers with the other. Same mistake,
/// opposite outcome, depending on how the handlers happened to be registered.
/// </para>
/// <para>
/// Nothing about this needs a message to detect, and there is no arrangement where two handlers for
/// one topic is intended, so it is checked once at start-up and it throws.
/// </para>
/// </remarks>
public class DuplicateTopicStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "duplicate-topic";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        var finder = resolver.TryGetService<IMessageHandlersFinder>();
        if (finder is null)
        {
            return;
        }

        var duplicates = finder.FindDefinitions()
            // Handler type included in the key: registering the same handler for the same topic twice
            // (two overlapping AddMessageHandlers scans, say) is a duplicate registration, not two
            // handlers competing for one topic, and the index's de-dup handles it correctly.
            .GroupBy(x => (x.Topic.Id, x.Topic.Version))
            .Where(x => x.Select(d => d.HandlerType).Distinct().Count() > 1)
            .ToArray();

        if (duplicates.Length == 0)
        {
            return;
        }

        var described = duplicates.Select(group =>
        {
            var version = string.IsNullOrEmpty(group.Key.Version) ? "" : $" (version '{group.Key.Version}')";
            var handlers = string.Join(", ", group.Select(x => x.HandlerType.FullName).Distinct());
            return $"'{group.Key.Id}'{version} is handled by {handlers}";
        });

        throw new BenzeneException(
            $"More than one message handler is registered for the same topic: {string.Join("; ", described)}. " +
            "Only one of them will ever run, and which one depends on registration order. Remove the " +
            "duplicate [Message] attribute, or the duplicate explicit registration.");
    }
}
