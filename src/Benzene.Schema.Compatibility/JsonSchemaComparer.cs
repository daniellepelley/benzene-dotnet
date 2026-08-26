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

        var baselineHasItems = baseline["items"] is JsonObject;
        var currentHasItems = current["items"] is JsonObject;

        if (baselineHasItems && currentHasItems)
        {
            Walk(baseline["items"] as JsonObject, current["items"] as JsonObject, direction, topic,
                $"{path}[]", changes, rules, depth + 1);
        }
        else if (Str(baseline, "type") == "array" && (baselineHasItems || currentHasItems))
        {
            // One side has an item schema and the other doesn't - not "nothing to compare", a type
            // change: the array's element contract appeared or disappeared entirely.
            var description = baselineHasItems ? "Array item schema was removed" : "Array item schema was added";
            changes.Add(Change(SchemaChangeKind.TypeChanged, direction, topic, $"{path}[]", description, rules));
        }

        CompareUnionMembers(baseline, current, direction, topic, path, changes, rules, depth, "oneOf");
        CompareUnionMembers(baseline, current, direction, topic, path, changes, rules, depth, "anyOf");
        CompareAllOfMembers(baseline, current, direction, topic, path, changes, rules, depth);
    }

    /// <summary>
    /// Walks a <c>oneOf</c>/<c>anyOf</c> member array pairwise between baseline and current. Matching
    /// priority: (1) <c>$ref</c> target name, when the member has one — a <c>$ref</c> already uniquely
    /// and stably identifies the target component, regardless of whether a discriminator mapping happens
    /// to cover it; (2) discriminator mapping value, for inline (non-<c>$ref</c>) members only, where
    /// there is no <c>$ref</c> name to key on; (3) position. Unmatched baseline members are <see cref="SchemaChangeKind.UnionVariantRemoved"/>,
    /// unmatched current members are <see cref="SchemaChangeKind.UnionVariantAdded"/>, and a matched pair
    /// that differs recurses and is reported as/within <see cref="SchemaChangeKind.UnionVariantChanged"/>.
    /// </summary>
    private static void CompareUnionMembers(JsonObject baseline, JsonObject current, SchemaDirection direction,
        string topic, string path, List<SchemaChange> changes, SchemaCompatibilityRules rules, int depth, string keyword)
    {
        var baselineMembers = baseline[keyword] as JsonArray;
        var currentMembers = current[keyword] as JsonArray;

        if ((baselineMembers == null || baselineMembers.Count == 0) && (currentMembers == null || currentMembers.Count == 0))
        {
            return;
        }

        var baselineByKey = IndexVariants(baseline, baselineMembers);
        var currentByKey = IndexVariants(current, currentMembers);

        foreach (var entry in baselineByKey)
        {
            if (!currentByKey.ContainsKey(entry.Key))
            {
                var name = VariantName(entry.Value);
                changes.Add(Change(SchemaChangeKind.UnionVariantRemoved, direction, topic, $"{path}.{keyword}[{name}]",
                    $"Union variant '{name}' was removed from {keyword}", rules));
            }
        }

        foreach (var entry in currentByKey)
        {
            if (!baselineByKey.ContainsKey(entry.Key))
            {
                var name = VariantName(entry.Value);
                changes.Add(Change(SchemaChangeKind.UnionVariantAdded, direction, topic, $"{path}.{keyword}[{name}]",
                    $"Union variant '{name}' was added to {keyword}", rules));
            }
        }

        foreach (var entry in baselineByKey)
        {
            if (!currentByKey.TryGetValue(entry.Key, out var currentVariant))
            {
                continue;
            }

            var name = VariantName(entry.Value);
            var variantPath = $"{path}.{keyword}[{name}]";
            RecurseIntoMatchedVariant(entry.Value, currentVariant, direction, topic, variantPath, changes, rules,
                depth, name);
        }
    }

    /// <summary>
    /// Walks <c>allOf</c> pairwise: <c>$ref</c> members match by target name, inline members match by
    /// their position among the inline members. Added/removed/changed members are reported the same way
    /// as <see cref="CompareUnionMembers"/>.
    /// </summary>
    private static void CompareAllOfMembers(JsonObject baseline, JsonObject current, SchemaDirection direction,
        string topic, string path, List<SchemaChange> changes, SchemaCompatibilityRules rules, int depth)
    {
        var baselineMembers = baseline["allOf"] as JsonArray;
        var currentMembers = current["allOf"] as JsonArray;

        if ((baselineMembers == null || baselineMembers.Count == 0) && (currentMembers == null || currentMembers.Count == 0))
        {
            return;
        }

        var baselineList = (baselineMembers ?? new JsonArray()).OfType<JsonObject>().ToList();
        var currentList = (currentMembers ?? new JsonArray()).OfType<JsonObject>().ToList();

        var baselineRefs = baselineList.Where(m => RefId(m) != null).ToDictionary(m => RefId(m)!, m => m);
        var currentRefs = currentList.Where(m => RefId(m) != null).ToDictionary(m => RefId(m)!, m => m);
        var baselineInline = baselineList.Where(m => RefId(m) == null).ToList();
        var currentInline = currentList.Where(m => RefId(m) == null).ToList();

        foreach (var entry in baselineRefs)
        {
            if (!currentRefs.ContainsKey(entry.Key))
            {
                changes.Add(Change(SchemaChangeKind.UnionVariantRemoved, direction, topic, $"{path}.allOf[{entry.Key}]",
                    $"allOf member '{entry.Key}' was removed", rules));
            }
        }

        foreach (var entry in currentRefs)
        {
            if (!baselineRefs.ContainsKey(entry.Key))
            {
                changes.Add(Change(SchemaChangeKind.UnionVariantAdded, direction, topic, $"{path}.allOf[{entry.Key}]",
                    $"allOf member '{entry.Key}' was added", rules));
            }
        }

        foreach (var entry in baselineRefs)
        {
            if (currentRefs.TryGetValue(entry.Key, out var currentMember))
            {
                RecurseIntoMatchedVariant(entry.Value, currentMember, direction, topic, $"{path}.allOf[{entry.Key}]",
                    changes, rules, depth, entry.Key);
            }
        }

        for (var i = 0; i < Math.Min(baselineInline.Count, currentInline.Count); i++)
        {
            RecurseIntoMatchedVariant(baselineInline[i], currentInline[i], direction, topic, $"{path}.allOf[{i}]",
                changes, rules, depth, $"#{i}");
        }

        for (var i = currentInline.Count; i < baselineInline.Count; i++)
        {
            changes.Add(Change(SchemaChangeKind.UnionVariantRemoved, direction, topic, $"{path}.allOf[{i}]",
                $"allOf member '#{i}' was removed", rules));
        }

        for (var i = baselineInline.Count; i < currentInline.Count; i++)
        {
            changes.Add(Change(SchemaChangeKind.UnionVariantAdded, direction, topic, $"{path}.allOf[{i}]",
                $"allOf member '#{i}' was added", rules));
        }
    }

    /// <summary>
    /// Recurses into a matched pair using the same top-level comparison logic; if that recursion finds
    /// any difference, a single <see cref="SchemaChangeKind.UnionVariantChanged"/> entry is inserted
    /// immediately before the differences it found, so the report reads as "variant X changed" followed
    /// by (within) exactly what changed.
    /// </summary>
    private static void RecurseIntoMatchedVariant(JsonObject baselineVariant, JsonObject currentVariant,
        SchemaDirection direction, string topic, string variantPath, List<SchemaChange> changes,
        SchemaCompatibilityRules rules, int depth, string name)
    {
        var before = changes.Count;
        Walk(baselineVariant, currentVariant, direction, topic, variantPath, changes, rules, depth + 1);

        if (changes.Count > before)
        {
            changes.Insert(before, Change(SchemaChangeKind.UnionVariantChanged, direction, topic, variantPath,
                $"Variant '{name}' changed", rules));
        }
    }

    /// <summary>
    /// Indexes a <c>oneOf</c>/<c>anyOf</c> member array by its matching key: its <c>$ref</c> target name
    /// when it has one, else the discriminator mapping value that points at this member when
    /// <paramref name="owner"/> declares one, else its position.
    /// </summary>
    private static Dictionary<string, JsonObject> IndexVariants(JsonObject owner, JsonArray? members)
    {
        var result = new Dictionary<string, JsonObject>();
        if (members == null)
        {
            return result;
        }

        var mapping = owner["discriminator"] is JsonObject discriminator ? discriminator["mapping"] as JsonObject : null;

        for (var i = 0; i < members.Count; i++)
        {
            if (members[i] is not JsonObject member)
            {
                continue;
            }

            result[VariantKey(mapping, member, i)] = member;
        }

        return result;
    }

    private static string VariantKey(JsonObject? mapping, JsonObject member, int index)
    {
        var refId = RefId(member);

        // A $ref already uniquely and stably identifies the target component, so it takes priority over
        // discriminator-mapping coverage: whether a mapping entry happens to name this $ref is metadata
        // about the variant, not part of its identity. Keying on mapping coverage instead let an
        // additive mapping edit (a new entry covering a previously-unmapped $ref) look like the variant
        // was replaced — disc:X on one side, ref:X on the other, for the very same schema.
        if (refId != null)
        {
            return $"ref:{refId}";
        }

        // No $ref to key on - this is an inline member. Fall back to the discriminator mapping when it
        // identifies this exact member (mapping values are $ref-shaped, so this only ever matches an
        // inline member a mapping entry names directly).
        if (mapping != null)
        {
            foreach (var entry in mapping)
            {
                if (entry.Value is JsonValue value && value.TryGetValue<string>(out var target)
                    && RefTargetName(target) == refId)
                {
                    return $"disc:{entry.Key}";
                }
            }
        }

        return $"idx:{index}";
    }

    /// <summary>The <c>$ref</c> target name of a member, e.g. <c>"Dog"</c> from
    /// <c>"#/components/schemas/Dog"</c>, or <c>null</c> when the member is inline.</summary>
    private static string? RefId(JsonObject member) =>
        member["$ref"] is JsonValue value && value.TryGetValue<string>(out var text) ? RefTargetName(text) : null;

    /// <summary>The schema name a <c>$ref</c>/discriminator-mapping pointer targets, e.g. <c>"Dog"</c>
    /// from either a bare name or a full <c>"#/components/schemas/Dog"</c> pointer.</summary>
    private static string RefTargetName(string pointer)
    {
        var slash = pointer.LastIndexOf('/');
        return slash >= 0 ? pointer[(slash + 1)..] : pointer;
    }

    private static string VariantName(JsonObject variant) => RefId(variant) ?? Describe(variant);

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
