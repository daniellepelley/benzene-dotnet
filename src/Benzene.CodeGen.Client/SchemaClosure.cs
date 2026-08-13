using Microsoft.OpenApi.Models;

namespace Benzene.CodeGen.Client;

/// <summary>
/// The single schema-closure walk shared by <see cref="AtomicClientSdkBuilder"/> (to build a
/// topic-scoped client's narrowed <c>components.schemas</c>) and the conformance test runner (to
/// verify it against <c>conformance/contract-document-cases.json</c>'s <c>schemaClosureCases</c>,
/// contract-document.md §5.3). Walks <c>$ref</c>, <c>items</c>, <c>additionalProperties</c>,
/// <c>properties</c>, and <c>allOf</c>/<c>anyOf</c>/<c>oneOf</c>, cycle-safe via the reached-set.
/// </summary>
internal static class SchemaClosure
{
    /// <summary>
    /// The set of catalogue schema names reachable from <paramref name="roots"/>, per §5.3's walk.
    /// </summary>
    public static ISet<string> ReachableNames(IDictionary<string, OpenApiSchema> catalogue, params OpenApiSchema?[] roots)
    {
        var reached = new HashSet<string>();

        void Walk(OpenApiSchema? schema)
        {
            if (schema == null)
            {
                return;
            }

            var referenceId = schema.Reference?.Id;
            // reached.Add short-circuits already-visited components, so reference cycles terminate.
            if (referenceId != null && catalogue.ContainsKey(referenceId) && reached.Add(referenceId))
            {
                Walk(catalogue[referenceId]);
            }

            Walk(schema.Items);
            Walk(schema.AdditionalProperties);
            foreach (var property in schema.Properties.Values)
            {
                Walk(property);
            }
            foreach (var composed in schema.AllOf.Concat(schema.AnyOf).Concat(schema.OneOf))
            {
                Walk(composed);
            }
        }

        foreach (var root in roots)
        {
            Walk(root);
        }

        return reached;
    }

    /// <summary>
    /// <paramref name="catalogue"/> narrowed down to exactly the entries <see cref="ReachableNames"/>
    /// reports reachable from <paramref name="roots"/>, keyed the same as the source catalogue.
    /// </summary>
    public static IDictionary<string, OpenApiSchema> Reachable(IDictionary<string, OpenApiSchema> catalogue, params OpenApiSchema?[] roots)
    {
        var reached = ReachableNames(catalogue, roots);

        return catalogue
            .Where(entry => reached.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }
}
