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
            JTokenType.Float => CreateNumberSchema(),
            JTokenType.Boolean => CreateBooleanSchema(),
            JTokenType.Guid => CreateGuidSchema(),
            JTokenType.Null => CreateNullPlaceholderSchema(),
            JTokenType.Array => CreateArraySchema(key, jToken),
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
    private OpenApiSchema CreateNumberSchema()
    {
        return new OpenApiSchema
        {
            Type = "number",
            Format = "double"
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

    // #264: a JSON `null` example value (an ordinary, legal shape for an optional/nullable field in
    // a captured real-world example) has no case here and used to throw "No map for Null", aborting
    // the whole document. Mirror CreateArraySchema's #242 "nothing in the example to infer from"
    // convention exactly: an untyped schema - no `type` keyword, so it matches anything - marked
    // Nullable, is the honest answer when a single null sample carries no type information at all.
    private OpenApiSchema CreateNullPlaceholderSchema()
    {
        return new OpenApiSchema
        {
            Nullable = true
        };
    }

    private OpenApiSchema CreateArraySchema(string key, JToken jArrayToken)
    {
        // #242: an ordinary empty example array ("tags": []) carries no element to infer an item
        // schema from. jToken.First() used to be called unconditionally here, throwing
        // InvalidOperationException ("Sequence contains no elements") on any such array anywhere in
        // the example payload. Match ExamplePayloadBuilder's own convention for "no known item
        // schema" (GetValue's `resolved.Items == null` branch, elsewhere in this package) as closely
        // as an OpenApiSchema allows: an untyped items schema - no `type` keyword, so it matches
        // anything - is the honest answer when there is nothing in the example to infer from.
        if (!jArrayToken.HasValues)
        {
            return new OpenApiSchema
            {
                Type = "array",
                Items = new OpenApiSchema(),
                Nullable = true
            };
        }

        return new OpenApiSchema
        {
            Type = "array",
            Items = Create(key, jArrayToken.First()),
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
