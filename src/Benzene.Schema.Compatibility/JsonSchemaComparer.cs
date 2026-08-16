using System.Text.Json.Nodes;

namespace Benzene.Schema.OpenApi.Compatibility;

/// <summary>
/// Compares two JSON Schema documents held as <see cref="JsonNode"/> and classifies every difference,
/// field by field, using the same taxonomy and the same <see cref="SchemaCompatibilityRules"/> as
/// <c>SchemaCompatibilityComparer</c>.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the mesh aggregator holds payload schemas as <see cref="JsonObject"/> with
/// <c>$ref</c>s already inlined, and must not take a dependency on an OpenAPI toolchain to compare
/// them. The two walkers are deliberately kept behaviourally identical — same traversal order, same
/// descriptions, same kinds — and a test asserts that over a shared corpus. Two walkers are
/// tolerable; two rule tables would not be, because a verdict that differs between the CI gate and
/// the mesh screen destroys the credibility of both.
/// </para>
/// <para>
/// It reads only the keywords the taxonomy is defined over — <c>type</c>, <c>format</c>,
/// <c>properties</c>, <c>required</c> and <c>items</c>. Everything else in a schema (descriptions,
/// examples, <c>minimum</c>, <c>pattern</c>) is ignored, which is a real limit: a tightened
/// <c>maxLength</c> is a breaking change this will not see. Narrowing that gap means adding kinds to
/// the taxonomy, not special cases here.
/// </para>
/// </remarks>
public static class JsonSchemaComparer
{
    /// <summary>
    /// Bounds recursion on schemas that are self-referential once <c>$ref</c>s are inlined. Matches
    /// <c>SchemaCompatibilityComparer</c>'s own limit so the two walkers agree at the boundary too.
    /// </summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Compares one side of a message across two versions.
    /// </summary>
    /// <param name="baseline">The older version's schema. <c>null</c> is treated as "not published".</param>
    /// <param name="current">The newer version's schema. <c>null</c> is treated as "not published".</param>
    /// <param name="direction">Which side of the message this is — the rules are asymmetric.</param>
    /// <param name="topic">The topic id, recorded on every change.</param>
    /// <param name="rootPath">The path prefix, e.g. <c>orders:create.request</c>.</param>
    /// <param name="rules">The rule table; <see cref="SchemaCompatibilityRules.Default"/> when null.</param>
    /// <returns>
    /// Every detected change, in traversal order. An empty list means the two schemas are equivalent
    /// <em>under this taxonomy</em> — which is not the same as byte-identical, and is the point.
    /// </returns>
    public static IReadOnlyList<SchemaChange> Compare(
        JsonNode? baseline, JsonNode? current, SchemaDirection direction, string topic, string rootPath,
        SchemaCompatibilityRules? rules = null)
    {
        var changes = new List<SchemaChange>();
        Walk(baseline as JsonObject, current as JsonObject, direction, topic, rootPath, changes,
            rules ?? SchemaCompatibilityRules.Default(), 0);
        return changes;
    }

    private static void Walk(JsonObject? baseline, JsonObject? current, SchemaDirection direction,
        string topic, string path, List<SchemaChange> changes, SchemaCompatibilityRules rules, int depth)
    {
        // A null on either side is "not published at this version", which is a statement about the
        // catalogue rather than about the contract. The caller decides what to say about it; emitting
        // a change here would manufacture a finding out of an absence.
        if (baseline == null || current == null || depth > MaxDepth)
        {
            return;
        }

        if (Str(baseline, "type") != Str(current, "type") || Str(baseline, "format") != Str(current, "format"))
        {
            changes.Add(Change(SchemaChangeKind.TypeChanged, direction, topic, path,
                $"Type changed from '{Describe(baseline)}' to '{Describe(current)}'", rules));
            return; // fundamentally different types — no point diffing their members
        }

        var baselineProps = Obj(baseline, "properties");
        var currentProps = Obj(current, "properties");
        var baselineRequired = Required(baseline);
        var currentRequired = Required(current);

        foreach (var prop in baselineProps)
        {
            if (!currentProps.ContainsKey(prop.Key))
            {
                changes.Add(Change(SchemaChangeKind.PropertyRemoved, direction, topic, $"{path}.{prop.Key}",
                    $"Property '{prop.Key}' was removed", rules));
            }
        }

        foreach (var prop in currentProps)
        {
            if (!baselineProps.ContainsKey(prop.Key))
            {
                var isRequired = currentRequired.Contains(prop.Key);
                var kind = isRequired ? SchemaChangeKind.RequiredPropertyAdded : SchemaChangeKind.PropertyAdded;
                changes.Add(Change(kind, direction, topic, $"{path}.{prop.Key}",
                    $"Property '{prop.Key}' was added{(isRequired ? " (required)" : "")}", rules));
            }
        }

        foreach (var prop in baselineProps)
        {
            if (!currentProps.TryGetValue(prop.Key, out var currentPropSchema))
            {
                continue;
            }

            var wasRequired = baselineRequired.Contains(prop.Key);
            var isRequired = currentRequired.Contains(prop.Key);

            if (!wasRequired && isRequired)
            {
                changes.Add(Change(SchemaChangeKind.PropertyBecameRequired, direction, topic, $"{path}.{prop.Key}",
                    $"Property '{prop.Key}' became required", rules));
            }
            else if (wasRequired && !isRequired)
            {
                changes.Add(Change(SchemaChangeKind.PropertyBecameOptional, direction, topic, $"{path}.{prop.Key}",
                    $"Property '{prop.Key}' became optional", rules));
            }

            Walk(prop.Value as JsonObject, currentPropSchema as JsonObject, direction, topic,
                $"{path}.{prop.Key}", changes, rules, depth + 1);
        }

        if (baseline["items"] is JsonObject baselineItems && current["items"] is JsonObject currentItems)
        {
            Walk(baselineItems, currentItems, direction, topic, $"{path}[]", changes, rules, depth + 1);
        }
    }

    /// <summary>
    /// The properties map, or an empty one. Ordinal comparison matches JSON's own semantics and the
    /// <c>Dictionary&lt;string, OpenApiSchema&gt;</c> the other walker reads — <c>customerId</c> and
    /// <c>CustomerId</c> are different fields, and treating them as one would silently swallow a rename.
    /// </summary>
    private static Dictionary<string, JsonNode?> Obj(JsonObject schema, string name)
    {
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (schema[name] is JsonObject properties)
        {
            foreach (var property in properties)
            {
                map[property.Key] = property.Value;
            }
        }

        return map;
    }

    private static HashSet<string> Required(JsonObject schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry?.GetValue<string>() is { } name)
                {
                    required.Add(name);
                }
            }
        }

        return required;
    }

    private static string? Str(JsonObject schema, string name) =>
        schema[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Describe(JsonObject schema)
    {
        var format = Str(schema, "format");
        return string.IsNullOrEmpty(format) ? Str(schema, "type") ?? "object" : $"{Str(schema, "type")}/{format}";
    }

    private static SchemaChange Change(SchemaChangeKind kind, SchemaDirection direction, string topic, string path,
        string description, SchemaCompatibilityRules rules) =>
        new(kind, direction, topic, path, description, rules.Evaluate(kind, direction));
}
