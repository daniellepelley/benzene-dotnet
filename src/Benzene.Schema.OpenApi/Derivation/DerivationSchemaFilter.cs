using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Benzene.Schema.OpenApi.Derivation;

/// <summary>
/// Brings Swashbuckle's output to the OpenAPI 3.0 dialect the contract document declares
/// (contract-document.md §4.2). Run with <c>SupportNonNullableReferenceTypes</c> and
/// <c>UseAllOfToExtendReferenceSchemas</c> on, it:
/// <list type="bullet">
/// <item>gives a nullable reference to a named type T the shape
/// <c>{"type":&lt;T's type&gt;,"nullable":true,"allOf":[{"$ref":…}]}</c> (OAS 3.0.3 ignores
/// <c>nullable</c> without a <c>type</c>), and collapses a non-nullable one back to a bare
/// <c>$ref</c>;</item>
/// <item>drops <c>additionalProperties: false</c> from records - only a string-keyed map carries
/// <c>additionalProperties</c>;</item>
/// <item>makes <c>required</c> exactly the properties the marshaler always emits: every serialized
/// property, less those marked <c>[JsonIgnore(Condition = WhenWritingNull/WhenWritingDefault)]</c>
/// (Benzene's default System.Text.Json serializer writes nulls).</item>
/// </list>
/// Enums keep their base type and values, and <c>format</c> stays where Swashbuckle knows it; both are
/// what §4.2 asks for.
/// </summary>
public sealed class DerivationSchemaFilter : ISchemaFilter
{
    private readonly JsonNamingPolicy? _namingPolicy;

    /// <summary>Initializes the filter for a marshaler that names properties with <paramref name="namingPolicy"/>.</summary>
    /// <param name="namingPolicy">The property naming policy; null for the CLR names.</param>
    public DerivationSchemaFilter(JsonNamingPolicy? namingPolicy)
    {
        _namingPolicy = namingPolicy;
    }

    /// <inheritdoc />
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.MemberInfo != null || context.ParameterInfo != null)
        {
            ApplyToMember(schema, context);
            return;
        }

        ApplyToType(schema, context.Type);
    }

    private static void ApplyToMember(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Type != null || schema.AllOf is not { Count: 1 } || schema.AllOf[0].Reference == null)
        {
            return;
        }

        var reference = schema.AllOf[0].Reference;
        if (!schema.Nullable)
        {
            // UseAllOfToExtendReferenceSchemas wraps every reference so a member can carry metadata;
            // a non-nullable one carries none the contract needs, so it is a plain $ref again.
            schema.Reference = reference;
            schema.AllOf = new List<OpenApiSchema>();
            return;
        }

        schema.Type = context.SchemaRepository.Schemas.TryGetValue(reference.Id, out var target) && target.Type != null
            ? target.Type
            : "object";
    }

    private void ApplyToType(OpenApiSchema schema, Type type)
    {
        if (schema.Type != "object" || schema.AdditionalProperties != null)
        {
            return;
        }

        // A record: Swashbuckle closes it (additionalProperties: false); the derivation rule does not.
        schema.AdditionalPropertiesAllowed = true;

        if (schema.Properties.Count == 0)
        {
            return;
        }

        var omittable = OmittableJsonNames(type);
        schema.Required = new SortedSet<string>(
            schema.Properties.Keys.Where(name => !omittable.Contains(name)),
            StringComparer.Ordinal);
    }

    private HashSet<string> OmittableJsonNames(Type type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        foreach (var member in type.GetProperties(flags).Cast<MemberInfo>().Concat(type.GetFields(flags)))
        {
            var ignore = member.GetCustomAttribute<JsonIgnoreAttribute>();
            if (ignore?.Condition is not (JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault))
            {
                continue;
            }

            var declared = member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            names.Add(declared ?? _namingPolicy?.ConvertName(member.Name) ?? member.Name);
        }

        return names;
    }
}
