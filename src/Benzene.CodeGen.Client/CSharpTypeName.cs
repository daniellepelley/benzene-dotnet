using Benzene.CodeGen.Core;
using Microsoft.OpenApi.Models;

namespace Benzene.CodeGen.Client
{
    public class CSharpTypeName : ITypeName
    {
        // Every $ref's Reference.Id is an arbitrary caller-supplied schema id (e.g. from the
        // documented bring-your-own-schema SuppliedSchemaCatalog feature) - not already a valid C#
        // identifier. The class declaration for that same id is emitted via this formatter
        // (OpenApiSchemaCSharpTypeBuilder), so every read of a Reference.Id here must go through the
        // same formatter or the generated property/parameter type can name a class that was never
        // generated (or isn't valid C# at all) - see #240.
        private readonly INameFormatter _nameFormatter = new CSharpNameFormatter();

        public string GetName(OpenApiSchema openApiSchema)
        {
            if (openApiSchema == null)
            {
                return "Void";
            }

            if (openApiSchema.Reference != null && !string.IsNullOrEmpty(openApiSchema.Reference.Id))
            {
                return _nameFormatter.Format(openApiSchema.Reference.Id);
            }

            // A bare (non-$ref) oneOf/anyOf schema - typically a top-level polymorphic
            // request/response - has no .Type, so falling through to `return openApiSchema.Type;`
            // below produced null (uncompilable `Task<IBenzeneResult<>>`). Mirror
            // OpenApiSchemaCSharpTypeBuilder.GetTypeName's handling: when every member is a $ref
            // whose own body was parsed inline (so its AllOf branches are visible without a separate
            // schema catalogue) and they share a common allOf base, type it as that base; otherwise
            // fall back to object, which always compiles.
            var union = openApiSchema.OneOf is { Count: > 0 } ? openApiSchema.OneOf : openApiSchema.AnyOf;
            if (union is { Count: > 0 })
            {
                if (union.All(x => x.Reference != null))
                {
                    var baseTypeIds = union
                        .Select(x => x.AllOf?.FirstOrDefault(branch => branch.Reference != null)?.Reference.Id)
                        .Select(id => string.IsNullOrEmpty(id) ? id : _nameFormatter.Format(id))
                        .Distinct()
                        .ToArray();

                    if (baseTypeIds is [{ Length: > 0 } sharedBase])
                    {
                        return sharedBase;
                    }
                }

                return "object";
            }

            if (openApiSchema.Type == "array")
            {
                var type = GetArrayType(openApiSchema.Items);
                return $"{type}[]";
            }

            if (openApiSchema.Type == "string" && openApiSchema.Format == "date-time")
            {
                return "DateTime?";
            }

            if (openApiSchema.Type == "string" && openApiSchema.Format == "uuid")
            {
                return "Guid?";
            }

            // A free-form object (a map) is modelled by additionalProperties. Guard the null case first:
            // a plain "object" schema with no additionalProperties leaves the property null, so reading
            // .Type off it threw a NullReferenceException. When it is present, type the dictionary value
            // from it (string -> Dictionary<string, string>, an int64 map -> Dictionary<string, long>,
            // a $ref -> Dictionary<string, Thing>) instead of only recognising the string case.
            if (openApiSchema.Type == "object" && openApiSchema.AdditionalProperties != null)
            {
                var valueType = GetName(openApiSchema.AdditionalProperties);
                if (!string.IsNullOrEmpty(valueType))
                {
                    return $"Dictionary<string, {valueType}>";
                }
            }

            if (openApiSchema.Type == "integer")
            {
                // An int64-format integer must map to long, not int, or generated clients silently
                // truncate 64-bit ids/amounts.
                var integerType = openApiSchema.Format == "int64" ? "long" : "int";
                return openApiSchema.Nullable ? $"{integerType}?" : integerType;
            }

            if (openApiSchema.Type == "number")
            {
                return openApiSchema.Nullable ? "double?" : "double";
            }

            if (openApiSchema.Type == "boolean")
            {
                return "bool";
            }

            return openApiSchema.Type;
        }

        private string GetArrayType(OpenApiSchema openApiSchema)
        {
            if (string.IsNullOrEmpty(openApiSchema.Type) || openApiSchema.Type == "object")
            {
                return _nameFormatter.Format(openApiSchema.Reference.Id);
            }

            return GetName(openApiSchema);
        }
    }
}
