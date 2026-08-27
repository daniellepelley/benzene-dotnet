using Microsoft.OpenApi.Models;
using Newtonsoft.Json.Linq;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Benzene.Schema.OpenApi;

public class JsonOpenApiSchemaBuilder
{
    private readonly SchemaRepository _schemaRepository = new();

    public IDictionary<string, OpenApiSchema> CreateSchema(string name, string json)
    {
        Create(name, JObject.Parse(json));
        return _schemaRepository.Schemas;

    }

    private OpenApiSchema Create(string key, JToken jToken)
    {
        return jToken.Type switch
        {
            JTokenType.String => CreateStringSchema(),
            JTokenType.Date => CreateDateTimeSchema(),
            JTokenType.Integer => CreateIntegerSchema(),
            JTokenType.Boolean => CreateBooleanSchema(),
            JTokenType.Guid => CreateGuidSchema(),
            JTokenType.Array => CreateArraySchema(key, jToken.First()),
            JTokenType.Object => CreateObjectSchema(key, jToken),
            _ => throw new Exception($"No map for {jToken.Type}")
        };
    }
    private OpenApiSchema CreateStringSchema()
    {
        return new OpenApiSchema
        {
            Type = "string",
            Nullable = true
        };
    }
    private OpenApiSchema CreateDateTimeSchema()
    {
        return new OpenApiSchema
        {
            Type = "string",
            Format = "date-time"
        };
    }
    private OpenApiSchema CreateIntegerSchema()
    {
        return new OpenApiSchema
        {
            Type = "integer",
            Format = "int32"
        };
    }
    private OpenApiSchema CreateBooleanSchema()
    {
        return new OpenApiSchema
        {
            Type = "boolean",
        };
    }
    private OpenApiSchema CreateGuidSchema()
    {
        return new OpenApiSchema
        {
            Type = "string",
            Format = "uuid"
        };
    }

    private OpenApiSchema CreateArraySchema(string key, JToken jToken)
    {
        return new OpenApiSchema
        {
            Type = "array",
            Items = Create(key, jToken),
            Nullable = true
        };
    }
    private OpenApiSchema CreateObjectSchema(string key, JToken jToken)
    {
        // #169 ripple: a component schema id is conventionally a type name (PascalCase, as
        // Swashbuckle's reflection path always registers it - e.g. "Inner"), not a JSON property
        // name. The top-level call already passes a real type name for `key`, so this is a no-op
        // there; a nested object/array-of-object discovered while walking `Properties()` below passes
        // its own JSON property key instead, which is camelCase now that schema property names match
        // the wire (SchemaBuilder) - capitalizing it here keeps every schema id in one convention
        // regardless of which path produced it, rather than registering a lowercase-led component id
        // no other builder in this codebase would ever emit.
        var schemaId = char.ToUpperInvariant(key[0]) + key.Substring(1);
        var properties = ((JObject)jToken).Properties();
        var schema = new OpenApiSchema
        {
            Type = "object",
            Properties = properties.ToDictionary(x => x.Name, x => Create(x.Name, x.Value)),
            AdditionalPropertiesAllowed = false
        };
        _schemaRepository.AddDefinition(schemaId, schema);
        return new OpenApiSchema
        {
            Reference = new OpenApiReference
            {
                Id = schemaId,
                Type = ReferenceType.Schema,
            }
        };
    }
}
