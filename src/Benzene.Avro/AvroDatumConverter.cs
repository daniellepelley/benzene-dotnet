using System.Collections;
using System.Globalization;
using System.Reflection;
using Avro;
using Avro.Generic;

namespace Benzene.Avro;

/// <summary>
/// Converts between plain CLR objects and the Avro datum shapes (<see cref="GenericRecord"/>,
/// arrays, primitives) that <see cref="GenericDatumWriter{T}"/>/<see cref="GenericDatumReader{T}"/>
/// operate on, driven entirely by the resolved <see cref="Schema"/>.
/// </summary>
internal static class AvroDatumConverter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------- CLR object -> Avro datum ----------

    /// <summary>
    /// Converts a CLR object graph to an Avro datum tree, guarding against unbounded recursion (e.g. a
    /// self-referencing type's object graph gone very deep) driving a CLR stack overflow. Unlike the
    /// deserialize-side guard in <see cref="BoundedBinaryDecoder"/>, this recursion is entirely our own
    /// code, so - unlike that guard - <paramref name="depth"/> is an exact current-depth count (each
    /// recursive call passes <c>depth + 1</c>, unwinding correctly on return), not an approximation.
    /// </summary>
    /// <param name="schema">The Avro schema describing <paramref name="value"/>'s shape.</param>
    /// <param name="value">The CLR value to convert.</param>
    /// <param name="maxDepth">The maximum nesting depth to follow (see <see cref="AvroOptions.MaxDepth"/>).</param>
    public static object? ToDatum(Schema schema, object? value, int maxDepth = AvroOptions.DefaultMaxDepth)
    {
        return ToDatum(schema, value, maxDepth, 0);
    }

    private static object? ToDatum(Schema schema, object? value, int maxDepth, int depth)
    {
        if (depth > maxDepth)
        {
            throw new AvroPayloadTooDeepException(depth, maxDepth, "serializing");
        }

        switch (schema.Tag)
        {
            case Schema.Type.Union:
                return ToUnionDatum((UnionSchema)schema, value, maxDepth, depth);
            case Schema.Type.Record:
                return ToRecord((RecordSchema)schema, value, maxDepth, depth);
            case Schema.Type.Array:
                return ToArray((ArraySchema)schema, value, maxDepth, depth);
            case Schema.Type.Map:
                return ToMap((MapSchema)schema, value, maxDepth, depth);
            case Schema.Type.String:
                return value == null ? null : ToAvroString(value);
            case Schema.Type.Boolean:
                return value != null && Convert.ToBoolean(value, Inv);
            case Schema.Type.Int:
                return value == null ? 0 : Convert.ToInt32(value, Inv);
            case Schema.Type.Long:
                return value == null ? 0L : ToAvroLong(value);
            case Schema.Type.Float:
                return value == null ? 0f : Convert.ToSingle(value, Inv);
            case Schema.Type.Double:
                return value == null ? 0d : Convert.ToDouble(value, Inv);
            case Schema.Type.Bytes:
                return value as byte[] ?? Array.Empty<byte>();
            case Schema.Type.Null:
                return null;
            default:
                return value;
        }
    }

    private static object? ToUnionDatum(UnionSchema union, object? value, int maxDepth, int depth)
    {
        if (value == null)
        {
            return null;
        }

        var branch = ResolveWriteBranch(union, value);
        return ToDatum(branch, value, maxDepth, depth + 1);
    }

    private static GenericRecord ToRecord(RecordSchema schema, object? value, int maxDepth, int depth)
    {
        var record = new GenericRecord(schema);
        if (value == null)
        {
            return record;
        }

        var type = value.GetType();
        foreach (var field in schema.Fields)
        {
            var property = type.GetProperty(field.Name, BindingFlags.Public | BindingFlags.Instance);
            var propertyValue = property?.GetValue(value);
            record.Add(field.Name, ToDatum(field.Schema, propertyValue, maxDepth, depth + 1));
        }

        return record;
    }

    private static object[] ToArray(ArraySchema schema, object? value, int maxDepth, int depth)
    {
        if (value is not IEnumerable enumerable)
        {
            return Array.Empty<object>();
        }

        var items = new List<object?>();
        foreach (var item in enumerable)
        {
            items.Add(ToDatum(schema.ItemSchema, item, maxDepth, depth + 1));
        }

        return items.ToArray()!;
    }

    private static object ToMap(MapSchema schema, object? value, int maxDepth, int depth)
    {
        if (value == null)
        {
            return new Dictionary<string, object?>();
        }

        // Avro map keys are ALWAYS strings per spec (unlike CLR dictionaries, which are generic over
        // the key type) - a non-string-keyed CLR target is a genuine mismatch, not something to
        // silently coerce. Check the value's own declared key type up front so this is caught even
        // for an empty dictionary, not just when a non-string key actually shows up below.
        var dictionaryInterface = value.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (dictionaryInterface != null && dictionaryInterface.GetGenericArguments()[0] != typeof(string))
        {
            throw new NotSupportedException(
                $"Avro map fields are always string-keyed per the Avro spec, but '{value.GetType()}' is " +
                $"keyed by '{dictionaryInterface.GetGenericArguments()[0]}'. Use a string-keyed dictionary " +
                "instead of coercing the key type.");
        }

        if (value is not IDictionary dictionary)
        {
            return new Dictionary<string, object?>();
        }

        var result = new Dictionary<string, object?>();
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                throw new NotSupportedException(
                    "Avro map fields are always string-keyed per the Avro spec, but encountered a map key " +
                    $"of type '{entry.Key?.GetType().ToString() ?? "null"}'. Use a string-keyed dictionary " +
                    "instead of coercing the key type.");
            }

            result[key] = ToDatum(schema.ValueSchema, entry.Value, maxDepth, depth + 1);
        }

        return result;
    }

    private static long ToAvroLong(object value)
    {
        // ulong maps to the signed Avro long; its upper half (> long.MaxValue) doesn't fit positively,
        // so reinterpret the bits (Convert.ToInt64 would throw OverflowException). FromDatum reverses
        // this for a ulong target. uint and long stay on the plain Convert path.
        return value is ulong u ? unchecked((long)u) : Convert.ToInt64(value, Inv);
    }

    private static string ToAvroString(object value)
    {
        return value switch
        {
            string s => s,
            Guid g => g.ToString(),
            DateTime dt => dt.ToString("O", Inv),
            DateTimeOffset dto => dto.ToString("O", Inv),
            decimal d => d.ToString(Inv),
            Enum e => e.ToString(),
            _ => Convert.ToString(value, Inv) ?? string.Empty
        };
    }

    // ---------- Avro datum -> CLR object ----------

    public static object? FromDatum(Schema schema, object? datum, Type targetType)
    {
        switch (schema.Tag)
        {
            case Schema.Type.Union:
                return FromUnion((UnionSchema)schema, datum, targetType);
            case Schema.Type.Record:
                return FromRecord((RecordSchema)schema, datum, targetType);
            case Schema.Type.Array:
                return FromArray((ArraySchema)schema, datum, targetType);
            case Schema.Type.Map:
                return FromMap((MapSchema)schema, datum, targetType);
            case Schema.Type.Null:
                return DefaultValue(targetType);
            case Schema.Type.String:
                return FromAvroString(datum as string, targetType);
            default:
                return datum == null ? DefaultValue(targetType) : ConvertPrimitive(datum, targetType);
        }
    }

    private static object? FromUnion(UnionSchema union, object? datum, Type targetType)
    {
        if (datum == null)
        {
            return DefaultValue(targetType);
        }

        var branch = ResolveReadBranch(union, datum);
        return FromDatum(branch, datum, Nullable.GetUnderlyingType(targetType) ?? targetType);
    }

    private static object? FromRecord(RecordSchema schema, object? datum, Type targetType)
    {
        if (datum is not GenericRecord record)
        {
            return DefaultValue(targetType);
        }

        var instance = Activator.CreateInstance(targetType)!;
        foreach (var property in AvroSchemaGenerator.GetProperties(targetType))
        {
            if (!record.TryGetValue(property.Name, out var fieldDatum))
            {
                continue;
            }

            var field = schema.Fields.First(f => f.Name == property.Name);
            property.SetValue(instance, FromDatum(field.Schema, fieldDatum, property.PropertyType));
        }

        return instance;
    }

    private static object FromArray(ArraySchema schema, object? datum, Type targetType)
    {
        var elementType = AvroSchemaGenerator.GetEnumerableElementType(targetType) ?? typeof(object);
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;

        if (datum is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                list.Add(FromDatum(schema.ItemSchema, item, elementType));
            }
        }

        if (!targetType.IsArray)
        {
            return list;
        }

        var array = Array.CreateInstance(elementType, list.Count);
        list.CopyTo(array, 0);
        return array;
    }

    private static object FromMap(MapSchema schema, object? datum, Type targetType)
    {
        var (valueType, stringKeyed) = GetMapValueType(targetType);
        if (!stringKeyed)
        {
            throw new NotSupportedException(
                $"Avro map fields are always string-keyed per the Avro spec, but the target type " +
                $"'{targetType}' is keyed by a non-string type. Use a string-keyed dictionary " +
                "(Dictionary<string,V>, IDictionary<string,V>, or IReadOnlyDictionary<string,V>) instead.");
        }

        var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType))!;

        if (datum is IDictionary sourceDictionary)
        {
            foreach (DictionaryEntry entry in sourceDictionary)
            {
                // Avro map keys are always strings on the wire (GenericDatumReader's map datum is
                // string-keyed by construction), so this cast can't legitimately fail.
                var key = (string)entry.Key;
                dictionary[key] = FromDatum(schema.ValueSchema, entry.Value, valueType);
            }
        }

        return dictionary;
    }

    /// <summary>
    /// Resolves the value type and key-string-ness of a CLR dictionary target type, for
    /// <see cref="FromMap"/>. Supports a concrete <c>Dictionary&lt;string,V&gt;</c> or an
    /// interface-typed <c>IDictionary&lt;string,V&gt;</c>/<c>IReadOnlyDictionary&lt;string,V&gt;</c>
    /// property; a property typed as anything else that still declares a dictionary interface with a
    /// non-string key is reported as non-string-keyed rather than silently defaulting.
    /// </summary>
    private static (Type ValueType, bool StringKeyed) GetMapValueType(Type targetType)
    {
        var dictionaryInterface = new[] { targetType }.Concat(targetType.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

        if (dictionaryInterface == null)
        {
            // Not a strongly-typed dictionary target (e.g. a plain `object` field) - default to a
            // string-keyed Dictionary<string, object>.
            return (typeof(object), true);
        }

        var keyType = dictionaryInterface.GetGenericArguments()[0];
        var valueType = dictionaryInterface.GetGenericArguments()[1];
        return (valueType, keyType == typeof(string));
    }

    private static object? FromAvroString(string? value, Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value == null)
        {
            return DefaultValue(targetType);
        }

        if (type == typeof(string)) return value;
        if (type == typeof(Guid)) return Guid.Parse(value);
        if (type == typeof(DateTime)) return DateTime.Parse(value, Inv, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value, Inv, DateTimeStyles.RoundtripKind);
        if (type == typeof(decimal)) return decimal.Parse(value, NumberStyles.Any, Inv);
        if (type.IsEnum) return Enum.Parse(type, value);
        return value;
    }

    private static object ConvertPrimitive(object datum, Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type == typeof(byte[]) || type.IsInstanceOfType(datum))
        {
            return datum;
        }

        // Reverse the ulong-as-signed-long bit reinterpretation from ToAvroLong: a value above
        // long.MaxValue comes back as a negative long, which Convert.ChangeType would reject.
        if (type == typeof(ulong) && datum is long l)
        {
            return unchecked((ulong)l);
        }

        return Convert.ChangeType(datum, type, Inv);
    }

    /// <summary>
    /// Picks the union branch to serialize <paramref name="value"/> (never null - the null case is
    /// handled by the caller) against, by the value's actual CLR type. Correct for any branch count:
    /// for the common 2-branch <c>["null", X]</c> shape there is only one non-null candidate, so this
    /// always resolves to it (byte-identical to the old first-non-null-branch behavior); for 3+
    /// non-null branches it picks the one matching the value's runtime shape instead of always the
    /// first declared.
    /// </summary>
    private static Schema ResolveWriteBranch(UnionSchema union, object value)
    {
        var candidates = union.Schemas.Where(s => s.Tag != Schema.Type.Null).ToList();
        if (candidates.Count == 0)
        {
            return union.Schemas[0];
        }

        // Exact-shape match: the branch whose Avro type is the natural (narrowest) mapping for the
        // value's actual CLR type.
        var exact = candidates.FirstOrDefault(s => IsNaturalMatch(s, value));
        if (exact != null)
        {
            return exact;
        }

        // Widening match: no exact-width branch is present (e.g. an `int` value but the union only
        // declares "long") - pick the narrowest branch the value still fits into losslessly.
        var widened = candidates.FirstOrDefault(s => IsWideningMatch(s, value));
        if (widened != null)
        {
            return widened;
        }

        // No scalar/shape match found (e.g. two record branches for the same POCO shape, which this
        // converter can't disambiguate without a registered CLR-type-to-schema-name map) - fall back
        // to the first non-null branch in declaration order, same as the old (2-branch-safe) behavior.
        return candidates[0];
    }

    private static bool IsNaturalMatch(Schema schema, object value)
    {
        var type = value.GetType();
        return schema.Tag switch
        {
            Schema.Type.Boolean => value is bool,
            Schema.Type.Int => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
                                type == typeof(ushort) || type == typeof(int),
            Schema.Type.Long => type == typeof(uint) || type == typeof(long) || type == typeof(ulong),
            Schema.Type.Float => value is float,
            Schema.Type.Double => value is double,
            Schema.Type.Bytes => value is byte[],
            Schema.Type.String => type == typeof(string) || type == typeof(Guid) || type == typeof(DateTime) ||
                                   type == typeof(DateTimeOffset) || type == typeof(decimal) || type.IsEnum,
            Schema.Type.Map => value is IDictionary,
            Schema.Type.Array => value is IEnumerable and not string and not byte[] and not IDictionary,
            Schema.Type.Record => value is not IEnumerable and not IDictionary &&
                                   type != typeof(bool) && type != typeof(byte[]) &&
                                   type != typeof(Guid) && type != typeof(DateTime) &&
                                   type != typeof(DateTimeOffset) && type != typeof(decimal) &&
                                   !type.IsEnum && !type.IsPrimitive,
            _ => false
        };
    }

    private static bool IsWideningMatch(Schema schema, object value)
    {
        var type = value.GetType();
        return schema.Tag switch
        {
            Schema.Type.Long => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
                                 type == typeof(ushort) || type == typeof(int),
            Schema.Type.Double => value is float,
            _ => false
        };
    }

    /// <summary>
    /// Picks the union branch to read <paramref name="datum"/> (never null - the null case is handled
    /// by the caller) against, by the datum's actual runtime type. <see cref="GenericDatumReader{T}"/>
    /// already resolved the wire's actual branch when it produced <paramref name="datum"/> (it read
    /// the union's branch index off the wire itself); this recovers that information from the datum's
    /// CLR shape instead of discarding it by always picking the first non-null branch. For the common
    /// 2-branch <c>["null", X]</c> shape there is only one non-null candidate, so this always resolves
    /// to it (byte-identical to the old behavior).
    /// </summary>
    private static Schema ResolveReadBranch(UnionSchema union, object datum)
    {
        var candidates = union.Schemas.Where(s => s.Tag != Schema.Type.Null).ToList();
        if (candidates.Count == 0)
        {
            return union.Schemas[0];
        }

        var match = candidates.FirstOrDefault(s => MatchesDatum(s, datum));
        return match ?? candidates[0];
    }

    private static bool MatchesDatum(Schema schema, object datum)
    {
        return schema.Tag switch
        {
            Schema.Type.Boolean => datum is bool,
            Schema.Type.Int => datum is int,
            Schema.Type.Long => datum is long,
            Schema.Type.Float => datum is float,
            Schema.Type.Double => datum is double,
            Schema.Type.Bytes => datum is byte[],
            Schema.Type.String => datum is string,
            Schema.Type.Map => datum is IDictionary,
            Schema.Type.Array => datum is IEnumerable and not IDictionary and not string and not byte[],
            Schema.Type.Record => schema is RecordSchema recordSchema && datum is GenericRecord record &&
                                   record.Schema.Fullname == recordSchema.Fullname,
            _ => false
        };
    }

    private static object? DefaultValue(Type type)
    {
        return type.IsValueType && Nullable.GetUnderlyingType(type) == null
            ? Activator.CreateInstance(type)
            : null;
    }
}
