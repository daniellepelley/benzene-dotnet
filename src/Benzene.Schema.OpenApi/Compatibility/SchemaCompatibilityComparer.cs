using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi.Models;

namespace Benzene.Schema.OpenApi.Compatibility;

/// <summary>
/// Compares two versions of a service's schema (the client's generation-time
/// <see cref="EventServiceDocument"/> against the service's current one) and produces a
/// <see cref="SchemaCompatibilityReport"/> classifying each difference as compatible, a warning, or
/// breaking. Unlike a single schema hash — which only tells you <em>whether</em> anything changed —
/// this tells you <em>what</em> changed and whether it actually breaks the contract.
/// </summary>
public class SchemaCompatibilityComparer
{
    private const int MaxDepth = 32;

    private readonly SchemaCompatibilityRules _rules;

    public SchemaCompatibilityComparer(SchemaCompatibilityRules? rules = null)
    {
        _rules = rules ?? SchemaCompatibilityRules.Default();
    }

    /// <summary>
    /// Compares <paramref name="baseline"/> (what the client was generated against) with
    /// <paramref name="current"/> (the service's current schema).
    /// </summary>
    public SchemaCompatibilityReport Compare(EventServiceDocument baseline, EventServiceDocument current)
    {
        var changes = new List<SchemaChange>();

        CompareRequests(baseline, current, changes);
        CompareEvents(baseline, current, changes);

        return new SchemaCompatibilityReport(changes);
    }

    private void CompareRequests(EventServiceDocument baseline, EventServiceDocument current, List<SchemaChange> changes)
    {
        var baselineByKey = Index(baseline.Requests, RequestKey);
        var currentByKey = Index(current.Requests, RequestKey);

        foreach (var entry in baselineByKey)
        {
            if (!currentByKey.ContainsKey(entry.Key))
            {
                changes.Add(Change(SchemaChangeKind.TopicRemoved, SchemaDirection.Request, entry.Value.Topic,
                    entry.Value.Topic, $"Topic '{entry.Value.Topic}' is no longer served"));
            }
        }

        foreach (var entry in currentByKey)
        {
            if (!baselineByKey.ContainsKey(entry.Key))
            {
                changes.Add(Change(SchemaChangeKind.TopicAdded, SchemaDirection.Request, entry.Value.Topic,
                    entry.Value.Topic, $"Topic '{entry.Value.Topic}' was added"));
            }
        }

        foreach (var entry in baselineByKey)
        {
            if (currentByKey.TryGetValue(entry.Key, out var currentReq))
            {
                CompareSchemas(Resolve(entry.Value.Request, baseline), Resolve(currentReq.Request, current),
                    baseline, current, SchemaDirection.Request, entry.Value.Topic, $"{entry.Value.Topic}.request", changes, 0);
                CompareSchemas(Resolve(entry.Value.Response, baseline), Resolve(currentReq.Response, current),
                    baseline, current, SchemaDirection.Response, entry.Value.Topic, $"{entry.Value.Topic}.response", changes, 0);
            }
        }
    }

    private void CompareEvents(EventServiceDocument baseline, EventServiceDocument current, List<SchemaChange> changes)
    {
        var baselineByKey = Index(baseline.Events, e => e.Topic);
        var currentByKey = Index(current.Events, e => e.Topic);

        foreach (var entry in baselineByKey)
        {
            if (!currentByKey.ContainsKey(entry.Key))
            {
                changes.Add(Change(SchemaChangeKind.TopicRemoved, SchemaDirection.Event, entry.Value.Topic,
                    entry.Value.Topic, $"Event '{entry.Value.Topic}' is no longer published"));
            }
        }

        foreach (var entry in currentByKey)
        {
            if (!baselineByKey.ContainsKey(entry.Key))
            {
                changes.Add(Change(SchemaChangeKind.TopicAdded, SchemaDirection.Event, entry.Value.Topic,
                    entry.Value.Topic, $"Event '{entry.Value.Topic}' was added"));
            }
        }

        foreach (var entry in baselineByKey)
        {
            if (currentByKey.TryGetValue(entry.Key, out var currentEvent))
            {
                CompareSchemas(Resolve(entry.Value.Message, baseline), Resolve(currentEvent.Message, current),
                    baseline, current, SchemaDirection.Event, entry.Value.Topic, $"{entry.Value.Topic}.message", changes, 0);
            }
        }
    }

    private void CompareSchemas(OpenApiSchema? baseline, OpenApiSchema? current,
        EventServiceDocument baselineDoc, EventServiceDocument currentDoc, SchemaDirection direction,
        string topic, string path, List<SchemaChange> changes, int depth)
    {
        if (baseline == null || current == null || depth > MaxDepth)
        {
            return;
        }

        if (baseline.Type != current.Type || baseline.Format != current.Format)
        {
            changes.Add(Change(SchemaChangeKind.TypeChanged, direction, topic, path,
                $"Type changed from '{Describe(baseline)}' to '{Describe(current)}'"));
            return; // fundamentally different types — no point diffing their members
        }

        var baselineProps = baseline.Properties ?? new Dictionary<string, OpenApiSchema>();
        var currentProps = current.Properties ?? new Dictionary<string, OpenApiSchema>();
        var baselineRequired = baseline.Required ?? new HashSet<string>();
        var currentRequired = current.Required ?? new HashSet<string>();

        foreach (var prop in baselineProps)
        {
            if (!currentProps.ContainsKey(prop.Key))
            {
                changes.Add(Change(SchemaChangeKind.PropertyRemoved, direction, topic, $"{path}.{prop.Key}",
                    $"Property '{prop.Key}' was removed"));
            }
        }

        foreach (var prop in currentProps)
        {
            if (!baselineProps.ContainsKey(prop.Key))
            {
                var isRequired = currentRequired.Contains(prop.Key);
                var kind = isRequired ? SchemaChangeKind.RequiredPropertyAdded : SchemaChangeKind.PropertyAdded;
                changes.Add(Change(kind, direction, topic, $"{path}.{prop.Key}",
                    $"Property '{prop.Key}' was added{(isRequired ? " (required)" : "")}"));
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
                    $"Property '{prop.Key}' became required"));
            }
            else if (wasRequired && !isRequired)
            {
                changes.Add(Change(SchemaChangeKind.PropertyBecameOptional, direction, topic, $"{path}.{prop.Key}",
                    $"Property '{prop.Key}' became optional"));
            }

            CompareSchemas(Resolve(prop.Value, baselineDoc), Resolve(currentPropSchema, currentDoc),
                baselineDoc, currentDoc, direction, topic, $"{path}.{prop.Key}", changes, depth + 1);
        }

        if (baseline.Items != null && current.Items != null)
        {
            CompareSchemas(Resolve(baseline.Items, baselineDoc), Resolve(current.Items, currentDoc),
                baselineDoc, currentDoc, direction, topic, $"{path}[]", changes, depth + 1);
        }
        else if (baseline.Type == "array" && (baseline.Items != null || current.Items != null))
        {
            // One side has an item schema and the other doesn't - not "nothing to compare", a type
            // change: the array's element contract appeared or disappeared entirely.
            var description = baseline.Items != null
                ? "Array item schema was removed"
                : "Array item schema was added";
            changes.Add(Change(SchemaChangeKind.TypeChanged, direction, topic, $"{path}[]", description));
        }

        // #168: additionalProperties (a Dictionary<string, T>-shaped schema) used to never be walked
        // at all, so a breaking change entirely inside a map's value schema (a type change, a new
        // required property) passed `benzene diff` undetected. Mirror the Items branch immediately
        // above: recurse when both sides have a value schema, and treat one side having none as the
        // map's value contract appearing/disappearing entirely.
        if (baseline.AdditionalProperties != null && current.AdditionalProperties != null)
        {
            CompareSchemas(Resolve(baseline.AdditionalProperties, baselineDoc), Resolve(current.AdditionalProperties, currentDoc),
                baselineDoc, currentDoc, direction, topic, $"{path}{{}}", changes, depth + 1);
        }
        else if (baseline.Type == "object" && (baseline.AdditionalProperties != null || current.AdditionalProperties != null))
        {
            var description = baseline.AdditionalProperties != null
                ? "Map value schema was removed"
                : "Map value schema was added";
            changes.Add(Change(SchemaChangeKind.TypeChanged, direction, topic, $"{path}{{}}", description));
        }

        CompareUnionMembers(baseline, current, baselineDoc, currentDoc, direction, topic, path, changes, depth,
            "oneOf", baseline.OneOf, current.OneOf);
        CompareUnionMembers(baseline, current, baselineDoc, currentDoc, direction, topic, path, changes, depth,
            "anyOf", baseline.AnyOf, current.AnyOf);
        CompareAllOfMembers(baseline, current, baselineDoc, currentDoc, direction, topic, path, changes, depth,
            baseline.AllOf, current.AllOf);
    }

    /// <summary>
    /// Walks a <c>oneOf</c>/<c>anyOf</c> member list pairwise between baseline and current. Matching
    /// priority: (1) <c>$ref</c> target name, when the member has one — a <c>$ref</c> already uniquely
    /// and stably identifies the target component, regardless of whether a discriminator mapping happens
    /// to cover it; (2) discriminator mapping value, for inline (non-<c>$ref</c>) members only, where
    /// there is no <c>$ref</c> name to key on; (3) position. Unmatched baseline members are <see cref="SchemaChangeKind.UnionVariantRemoved"/>,
    /// unmatched current members are <see cref="SchemaChangeKind.UnionVariantAdded"/>, and a matched pair
    /// that differs recurses and is reported as/within <see cref="SchemaChangeKind.UnionVariantChanged"/>.
    /// </summary>
    private void CompareUnionMembers(OpenApiSchema baseline, OpenApiSchema current, EventServiceDocument baselineDoc,
        EventServiceDocument currentDoc, SchemaDirection direction, string topic, string path,
        List<SchemaChange> changes, int depth, string label, IList<OpenApiSchema>? baselineMembers,
        IList<OpenApiSchema>? currentMembers)
    {
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
                changes.Add(Change(SchemaChangeKind.UnionVariantRemoved, direction, topic, $"{path}.{label}[{name}]",
                    $"Union variant '{name}' was removed from {label}"));
            }
        }

        foreach (var entry in currentByKey)
        {
            if (!baselineByKey.ContainsKey(entry.Key))
            {
                var name = VariantName(entry.Value);
                changes.Add(Change(SchemaChangeKind.UnionVariantAdded, direction, topic, $"{path}.{label}[{name}]",
                    $"Union variant '{name}' was added to {label}"));
            }
        }

        foreach (var entry in baselineByKey)
        {
            if (!currentByKey.TryGetValue(entry.Key, out var currentVariant))
            {
                continue;
            }

            var name = VariantName(entry.Value);
            var variantPath = $"{path}.{label}[{name}]";
            RecurseIntoMatchedVariant(entry.Value, currentVariant, baselineDoc, currentDoc, direction, topic,
                variantPath, changes, depth, name);
        }
    }

    /// <summary>
    /// Walks <c>allOf</c> pairwise: <c>$ref</c> members match by target name, inline members match by
    /// their position among the inline members. Added/removed/changed members are reported the same way
    /// as <see cref="CompareUnionMembers"/>.
    /// </summary>
    private void CompareAllOfMembers(OpenApiSchema baseline, OpenApiSchema current, EventServiceDocument baselineDoc,
        EventServiceDocument currentDoc, SchemaDirection direction, string topic, string path,
        List<SchemaChange> changes, int depth, IList<OpenApiSchema>? baselineMembers, IList<OpenApiSchema>? currentMembers)
    {
        if ((baselineMembers == null || baselineMembers.Count == 0) && (currentMembers == null || currentMembers.Count == 0))
        {
            return;
        }

        var baselineList = baselineMembers ?? new List<OpenApiSchema>();
        var currentList = currentMembers ?? new List<OpenApiSchema>();

        var baselineRefs = baselineList.Where(m => !string.IsNullOrEmpty(m.Reference?.Id))
            .ToDictionary(m => m.Reference!.Id, m => m);
        var currentRefs = currentList.Where(m => !string.IsNullOrEmpty(m.Reference?.Id))
            .ToDictionary(m => m.Reference!.Id, m => m);
        var baselineInline = baselineList.Where(m => string.IsNullOrEmpty(m.Reference?.Id)).ToList();
        var currentInline = currentList.Where(m => string.IsNullOrEmpty(m.Reference?.Id)).ToList();

        foreach (var entry in baselineRefs)
        {
            if (!currentRefs.ContainsKey(entry.Key))
            {
                changes.Add(Change(SchemaChangeKind.UnionVariantRemoved, direction, topic, $"{path}.allOf[{entry.Key}]",
                    $"allOf member '{entry.Key}' was removed"));
            }
        }

        foreach (var entry in currentRefs)
        {
            if (!baselineRefs.ContainsKey(entry.Key))
            {
                changes.Add(Change(SchemaChangeKind.UnionVariantAdded, direction, topic, $"{path}.allOf[{entry.Key}]",
                    $"allOf member '{entry.Key}' was added"));
            }
        }

        foreach (var entry in baselineRefs)
        {
            if (currentRefs.TryGetValue(entry.Key, out var currentMember))
            {
                RecurseIntoMatchedVariant(entry.Value, currentMember, baselineDoc, currentDoc, direction, topic,
                    $"{path}.allOf[{entry.Key}]", changes, depth, entry.Key);
            }
        }

        for (var i = 0; i < Math.Min(baselineInline.Count, currentInline.Count); i++)
        {
            RecurseIntoMatchedVariant(baselineInline[i], currentInline[i], baselineDoc, currentDoc, direction, topic,
                $"{path}.allOf[{i}]", changes, depth, $"#{i}");
        }

        for (var i = currentInline.Count; i < baselineInline.Count; i++)
        {
            changes.Add(Change(SchemaChangeKind.UnionVariantRemoved, direction, topic, $"{path}.allOf[{i}]",
                $"allOf member '#{i}' was removed"));
        }

        for (var i = baselineInline.Count; i < currentInline.Count; i++)
        {
            changes.Add(Change(SchemaChangeKind.UnionVariantAdded, direction, topic, $"{path}.allOf[{i}]",
                $"allOf member '#{i}' was added"));
        }
    }

    /// <summary>
    /// Recurses into a matched pair using the same top-level comparison logic; if that recursion finds
    /// any difference, a single <see cref="SchemaChangeKind.UnionVariantChanged"/> entry is inserted
    /// immediately before the differences it found, so the report reads as "variant X changed" followed
    /// by (within) exactly what changed.
    /// </summary>
    private void RecurseIntoMatchedVariant(OpenApiSchema baselineVariant, OpenApiSchema currentVariant,
        EventServiceDocument baselineDoc, EventServiceDocument currentDoc, SchemaDirection direction, string topic,
        string variantPath, List<SchemaChange> changes, int depth, string name)
    {
        var before = changes.Count;
        CompareSchemas(Resolve(baselineVariant, baselineDoc), Resolve(currentVariant, currentDoc),
            baselineDoc, currentDoc, direction, topic, variantPath, changes, depth + 1);

        if (changes.Count > before)
        {
            changes.Insert(before, Change(SchemaChangeKind.UnionVariantChanged, direction, topic, variantPath,
                $"Variant '{name}' changed"));
        }
    }

    /// <summary>
    /// Indexes a <c>oneOf</c>/<c>anyOf</c> member list by its matching key: its <c>$ref</c> target name
    /// when it has one, else the discriminator mapping value that points at this member when
    /// <paramref name="owner"/> declares a discriminator, else its position.
    /// </summary>
    private static Dictionary<string, OpenApiSchema> IndexVariants(OpenApiSchema owner, IList<OpenApiSchema>? members)
    {
        var result = new Dictionary<string, OpenApiSchema>();
        if (members == null)
        {
            return result;
        }

        var mapping = owner.Discriminator?.Mapping;

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var key = VariantKey(mapping, member, i);
            result[key] = member;
        }

        return result;
    }

    private static string VariantKey(IDictionary<string, string>? mapping, OpenApiSchema member, int index)
    {
        var refId = member.Reference?.Id;

        // A $ref already uniquely and stably identifies the target component, so it takes priority over
        // discriminator-mapping coverage: whether a mapping entry happens to name this $ref is metadata
        // about the variant, not part of its identity. Keying on mapping coverage instead let an
        // additive mapping edit (a new entry covering a previously-unmapped $ref) look like the variant
        // was replaced — disc:X on one side, ref:X on the other, for the very same schema.
        if (!string.IsNullOrEmpty(refId))
        {
            return $"ref:{refId}";
        }

        // No $ref to key on - this is an inline member. Fall back to the discriminator mapping when it
        // identifies this exact member (mapping values are $ref-shaped, so this only ever matches an
        // inline member a mapping entry names directly).
        if (mapping is { Count: > 0 })
        {
            foreach (var entry in mapping)
            {
                if (RefTargetName(entry.Value) == refId)
                {
                    return $"disc:{entry.Key}";
                }
            }
        }

        return $"idx:{index}";
    }

    /// <summary>The schema name a discriminator mapping value points at, e.g. <c>"Dog"</c> from either
    /// a bare name or a full <c>"#/components/schemas/Dog"</c> pointer.</summary>
    private static string RefTargetName(string mappingValue)
    {
        var slash = mappingValue.LastIndexOf('/');
        return slash >= 0 ? mappingValue[(slash + 1)..] : mappingValue;
    }

    private static string VariantName(OpenApiSchema variant) => variant.Reference?.Id ?? Describe(variant);

    /// <summary>Follows a <c>$ref</c> into the document's components, or returns the schema unchanged.</summary>
    private static OpenApiSchema? Resolve(OpenApiSchema? schema, EventServiceDocument doc)
    {
        if (schema == null)
        {
            return null;
        }

        var id = schema.Reference?.Id;
        if (!string.IsNullOrEmpty(id)
            && doc.Components?.Schemas != null
            && doc.Components.Schemas.TryGetValue(id, out var resolved))
        {
            return resolved;
        }

        return schema;
    }

    private static Dictionary<string, T> Index<T>(IEnumerable<T>? items, Func<T, string> key)
    {
        return (items ?? Enumerable.Empty<T>())
            .GroupBy(key)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private static string RequestKey(RequestResponse request) => $"{request.Topic}@{request.Version}";

    private static string Describe(OpenApiSchema schema) =>
        string.IsNullOrEmpty(schema.Format) ? (schema.Type ?? "object") : $"{schema.Type}/{schema.Format}";

    private SchemaChange Change(SchemaChangeKind kind, SchemaDirection direction, string topic, string path, string description) =>
        new(kind, direction, topic, path, description, _rules.Evaluate(kind, direction));
}
