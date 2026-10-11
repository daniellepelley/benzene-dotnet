using Microsoft.OpenApi.Models;

namespace Benzene.Schema.OpenApi;

/// <summary>
/// The contract document's nullable reference to a named type (contract-document.md §4.2):
/// <c>{"type":&lt;T's type&gt;,"nullable":true,"allOf":[{"$ref":…}]}</c>. Consumers that resolve a
/// property's type (code generation, example payloads, docs) see through it to the referenced type.
/// </summary>
public static class NullableReference
{
    /// <summary>
    /// The <c>$ref</c> schema inside <paramref name="schema"/> when it is a nullable-reference wrapper
    /// (one <c>allOf</c> member that is a reference, no properties of its own); otherwise
    /// <paramref name="schema"/> itself.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The referenced schema, or the schema unchanged.</returns>
    public static OpenApiSchema Unwrap(OpenApiSchema schema)
    {
        return IsWrapper(schema) ? schema.AllOf[0] : schema;
    }

    /// <summary>Whether <paramref name="schema"/> is a nullable-reference wrapper.</summary>
    /// <param name="schema">The schema.</param>
    /// <returns>True for <c>{"nullable":true,"allOf":[{"$ref":…}]}</c> (with or without <c>type</c>).</returns>
    public static bool IsWrapper(OpenApiSchema? schema)
    {
        return schema is { Reference: null, AllOf.Count: 1 }
               && schema.AllOf[0].Reference != null
               && (schema.Properties == null || schema.Properties.Count == 0)
               && schema.OneOf.Count == 0 && schema.AnyOf.Count == 0;
    }
}
