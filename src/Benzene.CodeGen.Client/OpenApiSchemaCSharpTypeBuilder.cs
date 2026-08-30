using Benzene.CodeGen.Core;
using Benzene.CodeGen.Core.Writers;
using Benzene.Schema.OpenApi.Examples;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Benzene.CodeGen.Client;

public class OpenApiSchemaCSharpTypeBuilder : ICodeBuilder<IDictionary<string, OpenApiSchema>>
{
    private readonly string _baseNamespace;
    private readonly INameFormatter _nameFormatter;
    private readonly ITypeName _typeName;

    public OpenApiSchemaCSharpTypeBuilder(string baseNamespace)
    {
        _baseNamespace = baseNamespace;
        _nameFormatter = new CSharpNameFormatter();
        _typeName = new CSharpTypeName();
    }

    public ICodeFile[] BuildCodeFiles(IDictionary<string, OpenApiSchema> dictionary)
    {
        return dictionary.Select(type => BuildSimpleType(type.Key, type.Value, dictionary)).ToArray();
    }

    public ICodeFile BuildType(KeyValuePair<string, OpenApiSchema> type)
    {
        return BuildSimpleType(type.Key, type.Value, null);
    }

    private ICodeFile BuildSimpleType(string name, OpenApiSchema schema,
        IDictionary<string, OpenApiSchema>? catalogue)
    {
        // #166: a schema with `enum` entries (however it reached the catalogue - reflected off a
        // real C# enum type via SchemaBuilder/Swashbuckle, or hand-built/deserialized) is not an
        // object and has no properties to emit - falling through to the class-emission code below
        // produced a real but completely empty C# class (`public class Status { }`), which then
        // serializes as "{}" on the wire instead of the enum's actual value. Branch here and emit a
        // real C# enum instead.
        if (schema.Enum is { Count: > 0 })
        {
            return BuildEnumType(name, schema);
        }

        // allOf composition: a single $ref branch is the base type; inline branches carry the
        // schema's own properties (Swashbuckle also leaves own properties at the top level).
        var baseTypeId = schema.AllOf?.FirstOrDefault(x => x.Reference != null)?.Reference.Id;
        var ownProperties = GetOwnProperties(schema);
        var hasDiscriminator = schema.Discriminator?.PropertyName is { Length: > 0 } &&
                               schema.Discriminator.Mapping is { Count: > 0 };

        var lineWriter = new LineWriter();

        foreach (var usingStatement in GetUsingStatements(schema, hasDiscriminator))
        {
            lineWriter.WriteLine($"using {usingStatement};");
        }
        lineWriter.WriteLine("");
        lineWriter.WriteLine($"namespace {_baseNamespace}");
        lineWriter.WriteLine("{");
        lineWriter.WriteLine("[ExcludeFromCodeCoverage]", 1);

        if (hasDiscriminator)
        {
            // Mirror the contract's discriminator as System.Text.Json polymorphism attributes so
            // the generated hierarchy round-trips derived instances the way the spec describes.
            lineWriter.WriteLine(
                $"[JsonPolymorphic(TypeDiscriminatorPropertyName = \"{EscapeCSharpString(schema.Discriminator!.PropertyName)}\")]", 1);
            foreach (var mapping in schema.Discriminator.Mapping)
            {
                lineWriter.WriteLine(
                    $"[JsonDerivedType(typeof({_nameFormatter.Format(RefName(mapping.Value))}), \"{EscapeCSharpString(mapping.Key)}\")]", 1);
            }
        }

        var declaration = $"public class {_nameFormatter.Format(name)}";
        if (!string.IsNullOrEmpty(baseTypeId))
        {
            declaration += $" : {_nameFormatter.Format(baseTypeId)}";
        }
        lineWriter.WriteLine(declaration, 1);
        lineWriter.WriteLine("{", 1);

        foreach (var property in ownProperties)
        {
            // The discriminator is serializer metadata ([JsonPolymorphic] writes it); a real
            // property of the same name would clash with it on serialization.
            if (hasDiscriminator && property.Key == schema.Discriminator!.PropertyName)
            {
                continue;
            }

            lineWriter.WriteLine(
                $"public {GetTypeName(property.Value, catalogue)} {_nameFormatter.Format(property.Key)} {{ get; set; }}", 2);
        }

        lineWriter.WriteLine("}", 1);
        lineWriter.WriteLine("}");

        return new CodeFile($"{name}.cs", lineWriter.GetLines());
    }

    /// <summary>
    /// Emits a real C# enum for an <c>enum</c>-shaped schema: a string enum (Swashbuckle's shape for
    /// a C# enum with <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c> applied) gets
    /// <see cref="JsonStringEnumConverter"/> on the generated type too, using each enum value string
    /// verbatim as the member name so the default converter (which serializes/deserializes by member
    /// name) round-trips exactly what the schema declares; an integer enum gets each numeric value as
    /// an explicit member value - System.Text.Json serializes an int enum as that number by default,
    /// so the member *name* has no wire effect and the schema carries none anyway (Swashbuckle emits
    /// only the raw numbers, no name metadata, for an integer enum).
    /// </summary>
    private ICodeFile BuildEnumType(string name, OpenApiSchema schema)
    {
        var isStringEnum = schema.Enum.Any(x => x is OpenApiString);

        var lineWriter = new LineWriter();
        if (isStringEnum)
        {
            lineWriter.WriteLine("using System.Text.Json.Serialization;");
        }
        lineWriter.WriteLine("");
        lineWriter.WriteLine($"namespace {_baseNamespace}");
        lineWriter.WriteLine("{");
        // No [ExcludeFromCodeCoverage] here - unlike a class, it is not a valid target on an enum
        // declaration (CS0592) and would make every generated enum uncompilable.
        if (isStringEnum)
        {
            lineWriter.WriteLine("[JsonConverter(typeof(JsonStringEnumConverter))]", 1);
        }
        lineWriter.WriteLine($"public enum {_nameFormatter.Format(name)}", 1);
        lineWriter.WriteLine("{", 1);

        foreach (var entry in schema.Enum)
        {
            lineWriter.WriteLine($"{FormatEnumMember(entry, isStringEnum)},", 2);
        }

        lineWriter.WriteLine("}", 1);
        lineWriter.WriteLine("}");

        return new CodeFile($"{name}.cs", lineWriter.GetLines());
    }

    private string FormatEnumMember(IOpenApiAny entry, bool isStringEnum)
    {
        if (isStringEnum)
        {
            var value = OpenApiAnyConverter.ToPlainValue(entry) as string ?? entry.ToString() ?? string.Empty;
            return _nameFormatter.Format(value);
        }

        var numeric = OpenApiAnyConverter.ToPlainValue(entry);
        return $"Value{numeric} = {numeric}";
    }

    private static IEnumerable<KeyValuePair<string, OpenApiSchema>> GetOwnProperties(OpenApiSchema schema)
    {
        var inlineAllOfProperties = schema.AllOf?
            .Where(x => x.Reference == null && x.Properties != null)
            .SelectMany(x => x.Properties) ?? Enumerable.Empty<KeyValuePair<string, OpenApiSchema>>();

        return schema.Properties
            .Concat(inlineAllOfProperties)
            .GroupBy(x => x.Key)
            .Select(x => x.First());
    }

    private string[] GetUsingStatements(OpenApiSchema schema, bool hasDiscriminator)
    {
        var output = new List<string>();
        output.Add("System");
        output.Add("System.Diagnostics.CodeAnalysis");

        // Any map property emits Dictionary<string, T> (see CSharpTypeName.GetName) regardless of the
        // value type - string, int64, or a $ref - so the using must be added for every non-null
        // additionalProperties, not only the string case (a Dictionary<string, long>/<string, Thing>
        // otherwise generates with no `using System.Collections.Generic;` and won't compile).
        if (schema.Properties.Any(x => x.Value.Type == "object" && x.Value.AdditionalProperties != null))
        {
            output.Add("System.Collections.Generic");
        }

        if (hasDiscriminator)
        {
            output.Add("System.Text.Json.Serialization");
        }

        return output.ToArray();
    }

    public string GetTypeName(OpenApiSchema openApiSchema)
    {
        return GetTypeName(openApiSchema, null);
    }

    private string GetTypeName(OpenApiSchema openApiSchema, IDictionary<string, OpenApiSchema>? catalogue)
    {
        // A oneOf union member site: type it as the subtypes' shared base class when one is
        // discoverable from the catalogue (their common allOf base $ref), else fall back to object.
        if (openApiSchema?.OneOf is { Count: > 0 } oneOf && oneOf.All(x => x.Reference != null))
        {
            var baseTypeIds = oneOf
                .Select(x => catalogue != null && catalogue.TryGetValue(x.Reference.Id, out var subtype)
                    ? subtype.AllOf?.FirstOrDefault(branch => branch.Reference != null)?.Reference.Id
                    : null)
                .Distinct()
                .ToArray();

            return baseTypeIds is [{ Length: > 0 } sharedBase]
                ? _nameFormatter.Format(sharedBase)
                : "object";
        }

        return _typeName.GetName(openApiSchema);
    }

    private static string RefName(string reference) =>
        reference.Substring(reference.LastIndexOf('/') + 1);

    // #263: PropertyName and every discriminator mapping.Key are arbitrary caller-supplied strings
    // (the discriminator *value*, not an identifier) with no guarantee they exclude `"`, `\`, or
    // control characters - interpolating them unescaped into a C# string-literal position produced
    // uncompilable generated SDKs from a single stray quote. Mirrors the shape of
    // YamlValueEscaping/NameFormatter.EscapeHclString elsewhere in this codebase; deliberately a
    // small local escaper rather than a Roslyn (Microsoft.CodeAnalysis.CSharp) dependency, which
    // this project does not otherwise need.
    private static string EscapeCSharpString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\0':
                    builder.Append("\\0");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }

        return builder.ToString();
    }
}
