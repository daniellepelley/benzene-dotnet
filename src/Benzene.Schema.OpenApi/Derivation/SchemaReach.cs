using Microsoft.OpenApi.Models;

namespace Benzene.Schema.OpenApi.Derivation;

/// <summary>
/// The component names reachable from a set of schemas (contract-document.md §5.3's walk: <c>$ref</c>,
/// <c>items</c>, <c>additionalProperties</c>, <c>properties</c>, <c>allOf</c>/<c>anyOf</c>/<c>oneOf</c>),
/// used to name the topics that reach a colliding component.
/// </summary>
internal static class SchemaReach
{
    public static ISet<string> Reachable(IDictionary<string, OpenApiSchema> catalogue, params OpenApiSchema?[] roots)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);

        void Walk(OpenApiSchema? schema)
        {
            if (schema == null)
            {
                return;
            }

            var id = schema.Reference?.Id;
            if (id != null && reached.Add(id) && catalogue.TryGetValue(id, out var target))
            {
                Walk(target);
            }

            Walk(schema.Items);
            Walk(schema.AdditionalProperties);
            foreach (var property in schema.Properties.Values)
            {
                Walk(property);
            }

            foreach (var member in schema.AllOf.Concat(schema.AnyOf).Concat(schema.OneOf))
            {
                Walk(member);
            }
        }

        foreach (var root in roots)
        {
            Walk(root);
        }

        return reached;
    }
}
