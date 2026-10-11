using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Benzene.Abstractions.Serialization;

/// <summary>
/// <see cref="IJsonPropertyNameResolver"/> backed by <see cref="System.Text.Json"/>: names a member
/// exactly as a <see cref="JsonSerializer"/> configured with the given <see cref="JsonSerializerOptions"/>
/// writes it - its <see cref="JsonSerializerOptions.PropertyNamingPolicy"/>,
/// <see cref="JsonPropertyNameAttribute"/> renames, <see cref="JsonIgnoreAttribute"/> and any contract
/// customization included - by reading the options' own contract metadata rather than re-deriving it.
/// </summary>
/// <remarks>
/// The options are copied, never locked or mutated: an application's options instance stays exactly
/// as configurable as it was. Per-type answers are cached for the life of the instance.
/// </remarks>
public class JsonPropertyNameResolver : IJsonPropertyNameResolver
{
    /// <summary>
    /// The resolver for Benzene's default JSON conventions: camelCase property names, rename
    /// attributes honoured - what <c>Benzene.Core.MessageHandlers</c>' default <c>JsonSerializer</c>
    /// writes. The fallback when no <see cref="IJsonPropertyNameResolver"/> is registered.
    /// </summary>
    public static readonly JsonPropertyNameResolver Default = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    private readonly JsonSerializerOptions _options;
    private readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, string>> _names = new();

    /// <summary>
    /// Initializes a new instance that names members as <paramref name="options"/> would.
    /// </summary>
    /// <param name="options">The options the service serializes with.</param>
    public JsonPropertyNameResolver(JsonSerializerOptions options)
    {
        // A private, read-only copy: GetTypeInfo needs a resolver and locks the instance it is called
        // on, and the caller's own options must not be locked as a side effect of naming a field.
        var copy = new JsonSerializerOptions(options);
        copy.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        copy.MakeReadOnly();
        _options = copy;
    }

    /// <inheritdoc />
    public string? GetJsonPropertyName(Type type, string memberName)
    {
        return _names.GetOrAdd(type, BuildNames).TryGetValue(memberName, out var name) ? name : null;
    }

    private IReadOnlyDictionary<string, string> BuildNames(Type type)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonTypeInfo typeInfo;
        try
        {
            typeInfo = _options.GetTypeInfo(type);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // A type the serializer cannot describe has no JSON form to point into.
            return names;
        }

        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return names;
        }

        foreach (var property in typeInfo.Properties)
        {
            // A [JsonIgnore]d member stays in the contract with neither accessor: it is never written
            // or read, so it has no location in the JSON form.
            if (property.Get == null && property.Set == null)
            {
                continue;
            }

            if (property.AttributeProvider is MemberInfo member)
            {
                names.TryAdd(member.Name, property.Name);
            }
        }

        return names;
    }
}
