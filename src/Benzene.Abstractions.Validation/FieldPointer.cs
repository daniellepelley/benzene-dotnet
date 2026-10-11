using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Serialization;

namespace Benzene.Abstractions.Validation;

/// <summary>
/// Converts a validation library's .NET property path into the <c>errors[].field</c> wire form: an
/// RFC 6901 JSON Pointer to the offending value in the handler's request, addressed in the request
/// type's <b>JSON form</b> (wire-contracts.md §1.3) - property names as the service's serializer writes
/// them, so a caller can resolve the pointer against the body it sent.
/// </summary>
/// <remarks>
/// <para>
/// <c>Name</c> becomes <c>/name</c> and FluentValidation's <c>Items[0].Sku</c> becomes
/// <c>/items/0/sku</c> under the default camelCase naming; a <c>[JsonPropertyName("line_items")]</c>
/// rename gives <c>/line_items/0/sku</c>. Each segment is resolved against the request type itself
/// (descending into property types and collection element types), named through an
/// <see cref="IJsonPropertyNameResolver"/>, and escaped per RFC 6901 (<c>~</c> as <c>~0</c>,
/// <c>/</c> as <c>~1</c>).
/// </para>
/// <para>
/// A path that cannot be resolved to a single location in the request's JSON form yields <c>null</c>,
/// and the error then omits <c>field</c> rather than inventing one: an empty path (a rule on the whole
/// request, a cross-field rule), a member the request type does not have or its serializer ignores
/// (e.g. a FluentValidation <c>OverridePropertyName</c> naming something else), an index into a
/// dictionary (the library reports an enumeration position, not the key the JSON uses), or a
/// non-numeric index.
/// </para>
/// </remarks>
public static class FieldPointer
{
    /// <summary>
    /// Converts <paramref name="propertyPath"/> - a .NET property path rooted at
    /// <paramref name="requestType"/> such as <c>Name</c>, <c>Address.Line1</c> or <c>Items[0].Sku</c> -
    /// into a JSON Pointer into the request's JSON form.
    /// </summary>
    /// <param name="requestType">The handler's request type the path is rooted at.</param>
    /// <param name="propertyPath">The library's property path; <c>null</c>/empty for a rule on the whole request.</param>
    /// <param name="names">
    /// Names members as the service's serializer writes them. <c>null</c> uses
    /// <see cref="JsonPropertyNameResolver.Default"/> (camelCase, rename attributes honoured).
    /// </param>
    /// <returns>The pointer (e.g. <c>/items/0/sku</c>), or <c>null</c> when the path names no single location.</returns>
    public static string? FromPropertyPath(Type requestType, string? propertyPath, IJsonPropertyNameResolver? names = null)
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
        {
            return null;
        }

        var segments = Parse(propertyPath);
        if (segments == null)
        {
            return null;
        }

        names ??= JsonPropertyNameResolver.Default;
        var pointer = new StringBuilder();
        var current = requestType;
        foreach (var segment in segments)
        {
            if (segment.IsIndex)
            {
                // A dictionary's "index" from a validation library is its enumeration position, not
                // the key the JSON form uses - there is no location to point at.
                var elementType = IsDictionary(current) ? null : GetElementType(current);
                if (elementType == null || !IsArrayIndex(segment.Value))
                {
                    return null;
                }

                pointer.Append('/').Append(segment.Value);
                current = elementType;
                continue;
            }

            var member = FindMember(current, segment.Value);
            if (member == null)
            {
                return null;
            }

            var jsonName = names.GetJsonPropertyName(current, member.Name);
            if (jsonName == null)
            {
                return null;
            }

            pointer.Append('/').Append(Escape(jsonName));
            current = Unwrap(member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType);
        }

        return pointer.ToString();
    }

    /// <summary>
    /// Escapes one reference token per RFC 6901 §3: <c>~</c> as <c>~0</c>, then <c>/</c> as <c>~1</c>.
    /// </summary>
    /// <param name="token">The unescaped token (a JSON property name or array index).</param>
    /// <returns>The escaped token.</returns>
    public static string Escape(string token)
    {
        return token.Replace("~", "~0").Replace("/", "~1");
    }

    /// <summary>
    /// Resolves the <see cref="IJsonPropertyNameResolver"/> a validation integration names members
    /// with: a registered resolver (<c>AddBenzene</c> registers the service's JSON serializer), else
    /// the registered <see cref="ISerializer"/> when it can name members itself, else
    /// <see cref="JsonPropertyNameResolver.Default"/>.
    /// </summary>
    /// <param name="serviceResolver">The current scope's resolver.</param>
    /// <returns>The resolver to name members with; never <c>null</c>.</returns>
    public static IJsonPropertyNameResolver ResolveNames(IServiceResolver serviceResolver)
    {
        return serviceResolver.TryGetService<IJsonPropertyNameResolver>()
            ?? serviceResolver.TryGetService<ISerializer>() as IJsonPropertyNameResolver
            ?? JsonPropertyNameResolver.Default;
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        return (MemberInfo?)FindProperty(type, name, flags, StringComparison.Ordinal)
            ?? type.GetField(name, flags)
            ?? (MemberInfo?)FindProperty(type, name, flags, StringComparison.OrdinalIgnoreCase)
            ?? type.GetFields(flags).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static PropertyInfo? FindProperty(Type type, string name, BindingFlags flags, StringComparison comparison)
    {
        // Not Type.GetProperty(name): that throws AmbiguousMatchException for a property a derived
        // type redeclares with `new`; the most-derived declaration is the one the serializer writes.
        return type.GetProperties(flags)
            .Where(x => x.GetIndexParameters().Length == 0 && string.Equals(x.Name, name, comparison))
            .OrderByDescending(x => Depth(x.DeclaringType))
            .FirstOrDefault();
    }

    private static int Depth(Type? type)
    {
        var depth = 0;
        for (var current = type; current != null; current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsDictionary(Type type)
    {
        return typeof(IDictionary).IsAssignableFrom(type)
            || type.GetInterfaces().Append(type).Any(x => x.IsGenericType
                && (x.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || x.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
    }

    private static Type? GetElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetArrayRank() == 1 ? Unwrap(type.GetElementType()!) : null;
        }

        var enumerable = type.GetInterfaces().Append(type)
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable == null ? null : Unwrap(enumerable.GetGenericArguments()[0]);
    }

    private static bool IsArrayIndex(string value)
    {
        // RFC 6901 array index: "0", or digits without a leading zero.
        return value.Length > 0
            && value.All(c => c is >= '0' and <= '9')
            && (value.Length == 1 || value[0] != '0')
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static List<Segment>? Parse(string path)
    {
        // A .NET property path: names separated by '.', each optionally followed by one or more
        // [index] groups - "Name", "Address.Line1", "Items[0].Sku", "Matrix[0][1]".
        var segments = new List<Segment>();
        var i = 0;
        while (i < path.Length)
        {
            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[')
            {
                i++;
            }

            if (i == start)
            {
                return null;
            }

            segments.Add(new Segment(path.Substring(start, i - start).Trim(), false));

            while (i < path.Length && path[i] == '[')
            {
                var close = path.IndexOf(']', i + 1);
                if (close < 0)
                {
                    return null;
                }

                segments.Add(new Segment(path.Substring(i + 1, close - i - 1).Trim(), true));
                i = close + 1;
            }

            if (i < path.Length)
            {
                if (path[i] != '.' || i == path.Length - 1)
                {
                    return null;
                }

                i++;
            }
        }

        return segments;
    }

    private readonly record struct Segment(string Value, bool IsIndex);
}
