using System.Linq.Expressions;

namespace Benzene.Core.Helper;

/// <summary>
/// Provides utility methods for manipulating and combining dictionaries, and for enriching plain
/// objects from a dictionary of property values (route params, query-string, headers, claims, ...).
/// </summary>
public static class DictionaryUtils
{
    /// <summary>
    /// Maps values from an overlay dictionary onto a source dictionary using case-insensitive keys.
    /// </summary>
    /// <param name="source">The source dictionary to modify.</param>
    /// <param name="overlayDictionary">The overlay dictionary containing values to map.</param>
    public static void MapOnto(IDictionary<string, object> source, IDictionary<string, string> overlayDictionary)
    {
        if (overlayDictionary == null)
        {
            return;
        }

        // Overlay the string entries directly instead of first materializing a full
        // Dictionary<string, object> copy (one throwaway dictionary per call on the HTTP path).
        foreach (var overlay in overlayDictionary)
        {
            MapOnto(source, overlay.Key, overlay.Value);
        }
    }

    /// <summary>
    /// Maps values from an overlay dictionary onto a source dictionary using case-insensitive keys.
    /// Only adds or updates keys that are missing or have default values in the source.
    /// </summary>
    /// <param name="source">The source dictionary to modify.</param>
    /// <param name="overlayDictionary">The overlay dictionary containing values to map.</param>
    /// <returns>The modified source dictionary.</returns>
    public static IDictionary<string, object> MapOnto(IDictionary<string, object> source, IDictionary<string, object> overlayDictionary)
    {
        if (overlayDictionary == null)
        {
            return source;
        }

        foreach (var overlay in overlayDictionary)
        {
            MapOnto(source, overlay.Key, overlay.Value);
        }

        return source;
    }

    // Adds the key (lower-cased) if absent, or fills it in when the existing value is null; the key is
    // lower-cased once and looked up once, instead of a ContainsKey + up-to-three indexer re-lookups
    // (each re-lower-casing the key) per entry.
    private static void MapOnto(IDictionary<string, object> source, string key, object value)
    {
        var lowerKey = key.ToLowerInvariant();

        if (!source.TryGetValue(lowerKey, out var existing))
        {
            source.Add(lowerKey, value);
        }
        else if (existing == default)
        {
            source[lowerKey] = value;
        }
    }

    /// <summary>
    /// Combines multiple dictionaries into a single dictionary, using the first occurrence of each key.
    /// </summary>
    /// <typeparam name="TKey">The type of dictionary keys.</typeparam>
    /// <typeparam name="TValue">The type of dictionary values.</typeparam>
    /// <param name="source">The collection of dictionaries to combine.</param>
    /// <returns>A combined dictionary containing all unique keys from the source dictionaries.</returns>
    public static IDictionary<TKey, TValue> Combine<TKey, TValue>(IEnumerable<IDictionary<TKey, TValue>> source)
    {
        var output = new Dictionary<TKey, TValue>();

        foreach (var dictionary in source)
        {
            foreach (var keyValue in dictionary)
            {
                output.TryAdd(keyValue.Key, keyValue.Value);
            }
        }

        return output;
    }

    /// <summary>
    /// Keeps only the entries of <paramref name="source"/> whose key is in <paramref name="filter"/>,
    /// renaming each kept key to the value <paramref name="filter"/> maps it to.
    /// </summary>
    /// <param name="source">The dictionary to filter and rename entries from, or <c>null</c> (treated as empty).</param>
    /// <param name="filter">Maps source keys (case-insensitive) to the output key to rename them to.</param>
    /// <returns>A new dictionary containing only the renamed, filtered entries.</returns>
    public static IDictionary<string, string> FilterAndReplace(IDictionary<string, string> source,
        IDictionary<string, string> filter)
    {
        // Single pass with TryGetValue/TryAdd (first-wins), replacing a per-entry double lookup +
        // GroupBy/First/ToDictionary. Only entries whose key is in the filter are kept.
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (source == null)
        {
            return output;
        }

        foreach (var entry in source)
        {
            if (filter.TryGetValue(entry.Key.ToLowerInvariant(), out var mapped))
            {
                output.TryAdd(mapped, entry.Value);
            }
        }

        return output;
    }

    /// <summary>
    /// Renames <paramref name="source"/>'s entries using <paramref name="filter"/> as a key-rename map,
    /// keeping entries whose key is not in <paramref name="filter"/> unchanged (unlike
    /// <see cref="FilterAndReplace"/>, which drops them).
    /// </summary>
    /// <param name="source">The source dictionary to process.</param>
    /// <param name="filter">Maps source keys (case-insensitive) to the output key to rename them to.</param>
    /// <returns>A new dictionary with replaced keys.</returns>
    public static IDictionary<string, string> Replace(IDictionary<string, string> source,
        IDictionary<string, string> filter)
    {
        // Single pass with TryGetValue/TryAdd (first-wins), replacing a per-entry double lookup +
        // GroupBy/First/ToDictionary. A key found in the filter is renamed; others pass through.
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in source)
        {
            var key = filter.TryGetValue(entry.Key.ToLowerInvariant(), out var mapped) ? mapped : entry.Key;
            output.TryAdd(key, entry.Value);
        }

        return output;
    }

    /// <summary>
    /// Sets a value in the dictionary for the specified key.
    /// </summary>
    /// <param name="dictionary">The dictionary to modify.</param>
    /// <param name="key">The key to set.</param>
    /// <param name="value">The value to set.</param>
    public static void Set(IDictionary<string, string> dictionary, string key, string value)
    {
        dictionary[key] = value;
    }

    /// <summary>
    /// Checks if a dictionary key equals a specific value.
    /// </summary>
    /// <param name="dictionary">The dictionary to check, or <c>null</c> (in which case <c>false</c> is returned).</param>
    /// <param name="key">The key to check.</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>True if the key exists and its value equals the specified value; otherwise, false.</returns>
    public static bool KeyEquals(IDictionary<string, string> dictionary, string key, string value)
    {
        if (dictionary != null && dictionary.TryGetValue(key, out var keyValue))
        {
            return keyValue == value;
        }

        return false;
    }

    /// <summary>
    /// Sets each property on <paramref name="source"/> whose name matches (case-insensitively) a key
    /// in <paramref name="dictionary"/> to that entry's value, converting the value's type if needed.
    /// Used to fold enricher-supplied values (route params, query-string, headers, claims - typically
    /// strings) onto a mapped request object.
    /// </summary>
    /// <typeparam name="T">The type of object to enrich.</typeparam>
    /// <param name="source">The object to enrich, or <c>null</c> (in which case a new instance is created via <see cref="Activator.CreateInstance{T}()"/> before enriching).</param>
    /// <param name="dictionary">The dictionary containing property values.</param>
    /// <returns>The enriched object (a new instance if <paramref name="source"/> was <c>null</c>).</returns>
    public static T Enrich<T>(T source, IDictionary<string, object> dictionary)
    {
        if (dictionary.Count == 0)
        {
            return source;
        }

        var setters = PropertySetterCache<T>.Setters;
        if (setters.Count == 0)
        {
            return source;
        }

        // Build one case-insensitive lookup over the caller's dictionary up front (O(dictionary size)),
        // instead of a per-property linear .Where() scan (O(properties x dictionary size)). TryAdd (not
        // the case-insensitive-dictionary copy constructor) so two differently-cased keys in the source
        // don't throw - the first one wins.
        var caseInsensitiveValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dictionary)
        {
            caseInsensitiveValues.TryAdd(entry.Key, entry.Value);
        }

        foreach (var (propertyName, setter) in setters)
        {
            if (caseInsensitiveValues.TryGetValue(propertyName, out var value))
            {
                source = EnsureNotNull(source);
                setter.Set(source, GetValue(value, setter.PropertyType));
            }
        }

        return source;
    }

    private static T EnsureNotNull<T>(T source)
    {
        return source ?? Activator.CreateInstance<T>();
    }

    private static object GetValue(object originalValue, Type propertyType)
    {
        if (originalValue.GetType() == propertyType)
        {
            return originalValue;
        }

        // Enrichers feed string values (route params, query-string, headers, claims) that need to be
        // coerced to the DTO's property type. Convert.ChangeType only handles the IConvertible
        // primitives, so Guid, enum and any Nullable<T> target threw InvalidCastException. Unwrap the
        // nullable, and parse Guid/enum explicitly; the compiled setter boxes the value back to the
        // declared property type.
        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (originalValue.GetType() == targetType)
        {
            return originalValue;
        }

        if (targetType == typeof(Guid))
        {
            return originalValue is Guid guid ? guid : Guid.Parse(originalValue.ToString());
        }

        if (targetType.IsEnum)
        {
            return originalValue is string enumText
                ? Enum.Parse(targetType, enumText, ignoreCase: true)
                : Enum.ToObject(targetType, originalValue);
        }

        return Convert.ChangeType(originalValue, targetType);
    }

    /// <summary>
    /// Caches a compiled setter delegate per writable public instance property of <typeparamref name="T"/>,
    /// built once (via <see cref="System.Linq.Expressions"/>) the first time <typeparamref name="T"/> is
    /// enriched, so repeated <see cref="Enrich{T}"/> calls for the same request type avoid re-reflecting
    /// over <see cref="Type.GetProperties()"/> and calling <c>PropertyInfo.SetValue</c> per property, per
    /// message. Non-writable properties are silently excluded (rather than replicating the reflective
    /// path's <c>ArgumentException</c> if such a property happened to match a dictionary key - an
    /// unencountered edge case in practice, since enrichment targets are always plain settable DTOs).
    /// </summary>
    private static class PropertySetterCache<T>
    {
        public static readonly IReadOnlyDictionary<string, (Type PropertyType, Action<T, object> Set)> Setters = Build();

        private static Dictionary<string, (Type, Action<T, object>)> Build()
        {
            var setters = new Dictionary<string, (Type, Action<T, object>)>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in typeof(T).GetProperties())
            {
                var setMethod = property.GetSetMethod();
                if (setMethod == null)
                {
                    continue;
                }

                var instanceParameter = Expression.Parameter(typeof(T), "instance");
                var valueParameter = Expression.Parameter(typeof(object), "value");
                var convertedValue = Expression.Convert(valueParameter, property.PropertyType);
                var call = Expression.Call(instanceParameter, setMethod, convertedValue);
                var compiled = Expression.Lambda<Action<T, object>>(call, instanceParameter, valueParameter).Compile();

                setters[property.Name] = (property.PropertyType, compiled);
            }

            return setters;
        }
    }
}
