namespace Benzene.Schema.OpenApi.Derivation;

/// <summary>
/// Two different payload types claim one component name (contract-document.md §4.1 rule 4). Derivation
/// fails rather than suffixing a name or letting the first registration win; the message names both
/// types, the topics that use them, and the remedy (<see cref="Benzene.Abstractions.Schema.SchemaNameAttribute"/>).
/// </summary>
public class ComponentNameCollisionException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="name">The contested component name.</param>
    /// <param name="existingType">The type that claimed the name first.</param>
    /// <param name="newType">The type that claimed it second.</param>
    /// <param name="topics">The topics whose payloads reach either type, when known.</param>
    public ComponentNameCollisionException(string name, Type existingType, Type newType, IReadOnlyCollection<string>? topics = null)
        : base(BuildMessage(name, existingType, newType, topics))
    {
        Name = name;
        ExistingType = existingType;
        NewType = newType;
        Topics = topics ?? Array.Empty<string>();
    }

    /// <summary>The kind the conformance fixtures name this failure by.</summary>
    public const string Kind = "component-name-collision";

    /// <summary>The contested component name.</summary>
    public string Name { get; }

    /// <summary>The type that claimed the name first.</summary>
    public Type ExistingType { get; }

    /// <summary>The type that claimed it second.</summary>
    public Type NewType { get; }

    /// <summary>The topics whose payloads reach either type.</summary>
    public IReadOnlyCollection<string> Topics { get; }

    /// <summary>The same collision, with the topics that use the two types attached.</summary>
    /// <param name="topics">The topics.</param>
    /// <returns>A new exception.</returns>
    public ComponentNameCollisionException WithTopics(IReadOnlyCollection<string> topics)
        => new(Name, ExistingType, NewType, topics);

    private static string BuildMessage(string name, Type existingType, Type newType, IReadOnlyCollection<string>? topics)
    {
        var used = topics is { Count: > 0 }
            ? $" (used by topic(s) {string.Join(", ", topics.Select(t => $"'{t}'"))})"
            : string.Empty;
        return $"{Kind}: component name '{name}' is claimed by two different types, '{existingType.FullName}' and " +
               $"'{newType.FullName}'{used}. Declare a distinct schema name on one or both with " +
               "[Benzene.Abstractions.Schema.SchemaName(\"...\")] (contract-document.md §4.1).";
    }
}
