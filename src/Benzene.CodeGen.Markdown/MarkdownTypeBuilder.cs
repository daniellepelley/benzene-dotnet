using Benzene.CodeGen.Core;
using Benzene.CodeGen.Core.Writers;
using Benzene.Schema.OpenApi.Examples;
using Microsoft.OpenApi.Models;

namespace Benzene.CodeGen.Markdown;

public class MarkdownTypeBuilder
{
    private readonly ISchemaGetter _schemaGetter;

    public MarkdownTypeBuilder(ISchemaGetter schemaGetter)
    {
        _schemaGetter = schemaGetter;
    }

    public void BuildType(string key, ILineWriter lineWriter)
    {
        BuildType(_schemaGetter.GetOpenApiSchema(key), lineWriter);
    }

    public void BuildType(OpenApiSchema openApiSchema, ILineWriter lineWriter)
    {
        openApiSchema = _schemaGetter.GetOpenApiSchema(openApiSchema);
        
        if (openApiSchema.Format == "uuid")
        {
            using (lineWriter.StartIndent())
            {
                lineWriter.WriteLine("Guid");
            }
        }
        else if (openApiSchema.Type == "array")
        {
            lineWriter.WriteLine("{");
            using (lineWriter.StartIndent())
            {
                GetLines(openApiSchema.Reference?.ReferenceV2, openApiSchema, lineWriter);
            }
            lineWriter.WriteLine("}[]");
        }
        else
        {
            if (openApiSchema.Properties.Any())
            {
                lineWriter.WriteLine("{");
                using (lineWriter.StartIndent())
                {
                    GetLines(openApiSchema.Reference?.ReferenceV2, openApiSchema, lineWriter);
                }

                lineWriter.WriteLine("}");
            }
            else
            {
                lineWriter.WriteLine("{}");
            }
        }
    }

    private void GetLines(string reference, OpenApiSchema openApiSchema, ILineWriter lineWriter)
    {
        foreach (var property in GetInnerType(openApiSchema).Properties)
        {
            MapProperty(property.Key, reference, property.Value, lineWriter);
        }
    }

    private void MapProperty(string name, string? reference, OpenApiSchema openApiSchema, ILineWriter lineWriter)
    {
        if (openApiSchema.Type == "object")
        {
            if (openApiSchema.Properties.Any())
            {
                lineWriter.WriteLine($"{CodeGenHelpers.Camelcase(name)}: {{");

                using (lineWriter.StartIndent())
                {
                    GetLines(reference, openApiSchema, lineWriter);
                }

                lineWriter.WriteLine("}");
            }
            else
            {
                // #265: this branch is the normal shape for a Dictionary<string, T>-typed property
                // (type: object with additionalProperties but no own declared properties) as well as
                // a genuinely empty object - the property NAME was dropped entirely, rendering a bare,
                // anonymous "{}" line a reader could not associate with any field. Always name the
                // property, and where there IS a value type to report, render the map shape (mirroring
                // CSharpTypeName.GetName's Dictionary<string, T> handling for the C# generator) instead
                // of an uninformative "{}".
                lineWriter.WriteLine($"{CodeGenHelpers.Camelcase(name)}: {GetMapOrEmptyObjectPlaceholder(openApiSchema)}");
            }
        }
        else if (openApiSchema.Type == "array" && openApiSchema.Items != null && (openApiSchema.Items.Reference != null || openApiSchema.Items.Type == "object"))
        {
            // Collapse to "{...}[]" only for a genuine reference cycle (the item points back at a
            // type we're already rendering), mirroring the single-object branch below. The original
            // `||` collapsed every referenced-object array (losing the item's fields) and, for an
            // inline-object array (Items.Reference == null), dereferenced the null Reference -> NRE.
            if (openApiSchema.Items.Reference != null && openApiSchema.Items.Reference.ReferenceV2 == reference)
            {
                lineWriter.WriteLine($"{CodeGenHelpers.Camelcase(name)}: {{...}}[]");
            }
            else
            {
                var innerSchema = _schemaGetter.GetOpenApiSchema(openApiSchema.Items);
                if (innerSchema.Properties.Any())
                {
                    lineWriter.WriteLine($"{CodeGenHelpers.Camelcase(name)}: {{");
        
                    using (lineWriter.StartIndent())
                    {
                        GetLines(reference, innerSchema, lineWriter);
                    }
        
                    lineWriter.WriteLine("}[]");
        
                }
                else
                {
                    // Same #265 fix as the single-object branch above, for an array of such objects
                    // (e.g. a Dictionary<string, T>[] or an array of genuinely empty objects).
                    lineWriter.WriteLine($"{CodeGenHelpers.Camelcase(name)}: {GetMapOrEmptyObjectPlaceholder(innerSchema)}[]");
                }
            }
        }
        else if (openApiSchema.Reference != null)
        {
            if (openApiSchema.Reference.ReferenceV2 == reference)
            {
                lineWriter.WriteLine($"{CodeGenHelpers.Camelcase(name)}: {{...}}");
            }
            else
            {
                MapProperty(name, openApiSchema.Reference.ReferenceV2, _schemaGetter.GetOpenApiSchema(openApiSchema),
                    lineWriter);
            }
        }
        else
        {
            lineWriter.WriteLine($"{CodeGenHelpers.Camelcase(name)}: {GetPropertyTypeName(openApiSchema)}");
        }
    }
    // #265: an `additionalProperties`-shaped object (no own declared properties) carries a value
    // type worth reporting - render it as a map shape rather than an uninformative bare "{}",
    // mirroring CSharpTypeName.GetName's Dictionary<string, T> handling for the C# generator
    // (see OpenApiSchemaCSharpTypeBuilder.cs's comment on GetTypeName/GetName).
    private string GetMapOrEmptyObjectPlaceholder(OpenApiSchema openApiSchema)
    {
        return openApiSchema.AdditionalProperties != null
            ? $"{{[string]: {GetPropertyTypeName(openApiSchema.AdditionalProperties)}}}"
            : "{}";
    }

    private OpenApiSchema GetInnerType(OpenApiSchema openApiSchema)
    {
        return openApiSchema.Type == "array"
            ? _schemaGetter.GetOpenApiSchema(openApiSchema.Items)
            : _schemaGetter.GetOpenApiSchema(openApiSchema);
    }

    private static bool IsNotValueType(Type type)
    {
        return !type.IsArray &&
               !type.IsValueType &&
               type.Name != "String" &&
               type.Name != "Datetime" &&
               type.Name != "Object";
    }

    private string GetPropertyTypeName(OpenApiSchema openApiSchema)
    {
        if (openApiSchema == null)
        {
            return "Void";
        }

        if (openApiSchema.Reference != null && !string.IsNullOrEmpty(openApiSchema.Reference.Id))
        {
            return openApiSchema.Reference.Id;
        }

        if (openApiSchema.Type == "array")
        {
            var type = GetPropertyTypeName(openApiSchema.Items);
            return $"{type}[]";
        }

        if (openApiSchema.Type == "string" && openApiSchema.Format == "date-time")
        {
            return "dateTime";
        }

        if (openApiSchema.Type == "string" && openApiSchema.Format == "uuid")
        {
            return "guid";
        }

        if (openApiSchema.Type == "object" && openApiSchema.AdditionalProperties?.Type == "string")
        {
            return "Dictionary<string, string>";
        }

        if (openApiSchema.Type == "integer")
        {
            return openApiSchema.Nullable ? "int?" : "int";
        }

        if (openApiSchema.Type == "number")
        {
            return openApiSchema.Nullable ? "double?" : "double";
        }

        if (openApiSchema.Type == "boolean")
        {
            return "bool";
        }

        // A polymorphic property (oneOf, no own .Type) fell through to the generic
        // `return openApiSchema.Type;` below and rendered as blank ("payment: " with nothing
        // after it). Render the shared base type when every member is a $ref sharing a common
        // allOf base (mirroring OpenApiSchemaCSharpTypeBuilder.GetTypeName's C#-generator
        // handling of the same shape), else an informative union listing of the member type
        // names.
        if (openApiSchema.OneOf is { Count: > 0 } oneOf)
        {
            if (oneOf.All(x => x.Reference != null))
            {
                var baseTypeIds = oneOf
                    .Select(x => _schemaGetter.GetOpenApiSchema(x).AllOf?
                        .FirstOrDefault(branch => branch.Reference != null)?.Reference.Id)
                    .Distinct()
                    .ToArray();

                if (baseTypeIds is [{ Length: > 0 } sharedBase])
                {
                    return sharedBase;
                }

                return $"oneOf: {{{string.Join("|", oneOf.Select(x => x.Reference.Id))}}}";
            }

            return $"oneOf: {{{string.Join("|", oneOf.Select(GetPropertyTypeName))}}}";
        }

        return openApiSchema.Type;
    }
}
