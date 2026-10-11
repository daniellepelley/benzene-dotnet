using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace Benzene.Core.MessageHandlers.StartUpChecks;

/// <summary>
/// What version selection (docs/specification/core-concepts.md §2) can see about a topic before the first
/// message: one configuration error, and the arrangements worth saying once at start-up rather than once
/// per message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Throws</b> when a topic declares more than one default handler. There is no arrangement where that
/// is intended, and whichever declaration the selector met first would silently win.
/// </para>
/// <para>
/// <b>Logs a warning</b> for each of these - all valid, all surprising:
/// </para>
/// <list type="bullet">
/// <item>a topic whose handlers are all versioned with none declared default: every message to it without
/// a version is answered <c>not-found</c>;</item>
/// <item>a topic that declares a versioned default <i>and</i> has an unversioned handler: the unversioned
/// handler is the default, so the declaration never takes effect;</item>
/// <item>a version that casts to more than one handler without the default among them: the selector's
/// documented tie-break picks one (<see cref="VersionSelector"/>), and the warning names which.</item>
/// </list>
/// </remarks>
public class VersionSelectionStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "version-selection";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        var finder = resolver.TryGetService<IMessageHandlersFinder>();
        if (finder is null)
        {
            return;
        }

        var casts = resolver.TryGetService<IPayloadVersionCasts>();
        var problems = new List<string>();
        var warnings = new List<string>();
        var undefaulted = new List<string>();

        var topics = finder.FindDefinitions()
            .GroupBy(x => (x.Topic.Id, x.Topic.Version))
            .Select(x => x.First())
            .GroupBy(x => x.Topic.Id);

        foreach (var topic in topics)
        {
            var candidates = topic.ToArray();
            var unversioned = candidates.FirstOrDefault(x => string.IsNullOrEmpty(x.Topic.Version));
            var declared = candidates.Where(x => x.IsDefault && !string.IsNullOrEmpty(x.Topic.Version)).ToArray();

            if (declared.Length > 1)
            {
                problems.Add($"topic '{topic.Key}' declares more than one default handler " +
                             $"(versions {string.Join(", ", declared.Select(x => $"'{x.Topic.Version}'"))}); declare one");
            }
            else if (declared.Length == 1 && unversioned != null)
            {
                warnings.Add($"topic '{topic.Key}' declares version '{declared[0].Topic.Version}' its default, but its " +
                             $"unversioned handler {unversioned.HandlerType.Name} is the default, so the declaration has no effect");
            }
            else if (declared.Length == 0 && unversioned == null)
            {
                undefaulted.Add(topic.Key);
            }

            if (casts == null)
            {
                continue;
            }

            foreach (var version in casts.GetVersions(topic.Key))
            {
                if (candidates.Any(x => string.Equals(x.Topic.Version, version, StringComparison.Ordinal)))
                {
                    continue; // an exact match wins before casting is considered
                }

                var selected = VersionSelector.SelectByCast(casts, topic.Key, version, candidates, out var reachable);
                var defaultHandler = VersionSelector.DefaultHandler(candidates);
                if (selected != null && reachable.Length > 1 && !ReferenceEquals(selected, defaultHandler))
                {
                    warnings.Add($"topic '{topic.Key}': version '{version}' can be cast to {reachable.Length} handlers " +
                                 $"({string.Join(", ", reachable.Select(Describe))}) and the default is not one of them, so the " +
                                 $"tie-break routes it to {Describe(selected)} (the greatest version)");
                }
            }
        }

        var logger = resolver.TryGetService<ILoggerFactory>()?.CreateLogger("Benzene.StartUpChecks");
        if (undefaulted.Count > 0)
        {
            logger?.LogWarning(
                "Topic(s) {Topics} have only versioned handlers and none declared default, so a message to them " +
                "without a benzene-version header is answered not-found. If producers may omit the version, declare " +
                "one handler the default: [Message(topic, version, IsDefault = true)].",
                string.Join(", ", undefaulted.Select(x => $"'{x}'")));
        }

        foreach (var warning in warnings)
        {
            logger?.LogWarning("Version selection: {Warning}.", warning);
        }

        if (problems.Count > 0)
        {
            throw new BenzeneException(
                $"Version selection is misconfigured: {string.Join("; ", problems)}. A message carrying no version " +
                "goes to the topic's one default handler.");
        }
    }

    private static string Describe(IMessageHandlerDefinition definition) =>
        string.IsNullOrEmpty(definition.Topic.Version)
            ? $"{definition.HandlerType.Name} (unversioned)"
            : $"{definition.HandlerType.Name} ('{definition.Topic.Version}')";
}
