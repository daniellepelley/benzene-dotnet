using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Benzene.Mcp;

/// <summary>
/// A small builder for the JSON Schema a tool advertises, so a tool list stays readable, plus
/// <see cref="For{T}"/> for the shorthand of deriving one from a request type.
/// </summary>
/// <remarks>
/// The hand-written forms are the default: a schema the model reads deserves the same care as the
/// description, and a request type's property names are rarely the words a model would choose.
/// <see cref="For{T}"/> is for the topic-bound tool whose request type already reads well
/// (camelCase names, <see cref="DescriptionAttribute"/> on the properties, <see cref="RequiredAttribute"/>
/// or <see cref="JsonRequiredAttribute"/> on the ones that are).
/// </remarks>
public static class McpSchema
{
    /// <summary>An object with the given properties; <c>additionalProperties</c> is false.</summary>
    public static JsonObject Object(params (string Name, JsonObject Property, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, property, isRequired) in properties)
        {
            props[name] = property;
            if (isRequired) required.Add(name);
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    /// <summary>A string.</summary>
    public static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    /// <summary>A date, written <c>yyyy-MM-dd</c> (the description says so, and <see cref="McpArgs"/> enforces it).</summary>
    public static JsonObject Date(string description) =>
        new() { ["type"] = "string", ["format"] = "date", ["description"] = description + " Written yyyy-MM-dd." };

    /// <summary>A number (an amount, a rate).</summary>
    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    /// <summary>A whole number.</summary>
    public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    /// <summary>A true or false.</summary>
    public static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };

    /// <summary>An id, as a UUID string.</summary>
    public static JsonObject Id(string description) =>
        new() { ["type"] = "string", ["format"] = "uuid", ["description"] = description };

    /// <summary>
    /// A list of the same thing several times over. Only for the jobs that genuinely are several at
    /// once; asking for them one tool call at a time is several approvals for one decision.
    /// </summary>
    public static JsonObject ListOf(string description, JsonObject item) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = item };

    /// <summary>One of a fixed set of strings.</summary>
    public static JsonObject Choice(string description, params string[] values)
    {
        var options = new JsonArray();
        foreach (var v in values) options.Add(v);
        return new JsonObject { ["type"] = "string", ["description"] = description, ["enum"] = options };
    }

    /// <summary>The schema of <typeparamref name="T"/>'s public properties, as the shorthand for a topic-bound tool.</summary>
    /// <typeparam name="T">The request type.</typeparam>
    public static JsonObject For<T>() => For(typeof(T));

    /// <summary>
    /// The schema of a type's public instance properties: names in camelCase (or <see cref="JsonPropertyNameAttribute"/>),
    /// descriptions from <see cref="DescriptionAttribute"/>, required from <see cref="RequiredAttribute"/> /
    /// <see cref="JsonRequiredAttribute"/> / the <c>required</c> modifier, <see cref="JsonIgnoreAttribute"/> honoured.
    /// Strings, numbers, booleans, dates, GUIDs, enums, arrays, dictionaries and nested objects are mapped;
    /// anything else becomes an unconstrained property.
    /// </summary>
    /// <param name="type">The request type.</param>
    public static JsonObject For(Type type) => ObjectSchema(type, new HashSet<Type>(), 0);

    private const int MaxDepth = 8;

    private static JsonObject ObjectSchema(Type type, HashSet<Type> inProgress, int depth)
    {
        var props = new JsonObject();
        var required = new JsonArray();

        if (depth < MaxDepth && inProgress.Add(type))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
                if (!property.CanRead) continue;

                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                           ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                var schema = PropertySchema(property.PropertyType, inProgress, depth + 1);
                var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (!string.IsNullOrWhiteSpace(description)) schema["description"] = description;
                props[name] = schema;

                if (property.GetCustomAttribute<RequiredAttribute>() is not null
                    || property.GetCustomAttribute<JsonRequiredAttribute>() is not null
                    || property.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null)
                {
                    required.Add(name);
                }
            }
            inProgress.Remove(type);
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    private static JsonObject PropertySchema(Type type, HashSet<Type> inProgress, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) type = underlying;

        if (type == typeof(string) || type == typeof(char)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            return new JsonObject { ["type"] = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new JsonObject { ["type"] = "number" };
        if (type == typeof(DateOnly)) return new JsonObject { ["type"] = "string", ["format"] = "date" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (type == typeof(TimeOnly)) return new JsonObject { ["type"] = "string", ["format"] = "time" };
        if (type == typeof(Guid)) return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        if (type == typeof(Uri)) return new JsonObject { ["type"] = "string", ["format"] = "uri" };
        if (type.IsEnum)
        {
            var values = new JsonArray();
            foreach (var name in Enum.GetNames(type)) values.Add(JsonNamingPolicy.CamelCase.ConvertName(name));
            return new JsonObject { ["type"] = "string", ["enum"] = values };
        }
        if (type == typeof(JsonObject)) return new JsonObject { ["type"] = "object" };
        if (type == typeof(JsonArray)) return new JsonObject { ["type"] = "array" };
        if (type == typeof(JsonNode) || type == typeof(object) || type == typeof(JsonElement)) return new JsonObject();

        if (TryDictionaryValueType(type, out var valueType))
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = PropertySchema(valueType!, inProgress, depth + 1),
            };
        }

        if (type != typeof(string) && TryElementType(type, out var elementType))
        {
            return new JsonObject { ["type"] = "array", ["items"] = PropertySchema(elementType!, inProgress, depth + 1) };
        }

        if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
        {
            return ObjectSchema(type, inProgress, depth);
        }

        return new JsonObject();
    }

    private static bool TryDictionaryValueType(Type type, out Type? valueType)
    {
        var dictionary = new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType &&
                                 (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                                  i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        valueType = dictionary?.GetGenericArguments()[1];
        return valueType is not null;
    }

    private static bool TryElementType(Type type, out Type? elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
        }
        var enumerable = new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        elementType = enumerable?.GetGenericArguments()[0];
        return elementType is not null && typeof(IEnumerable).IsAssignableFrom(type);
    }
}
