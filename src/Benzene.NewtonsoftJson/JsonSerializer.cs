using Benzene.Abstractions.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Benzene.NewtonsoftJson;

/// <summary>
/// <see cref="ISerializer"/> backed by Newtonsoft.Json (Json.NET). All members are <c>virtual</c>, so
/// a subclass can override the settings.
/// </summary>
public class JsonSerializer : ISerializer
{
    /// <summary>Serializes <paramref name="payload"/> as <paramref name="type"/> to a JSON string.</summary>
    /// <param name="type">The runtime type of the object to serialize. Ignored - see remarks.</param>
    /// <param name="payload">The object to serialize.</param>
    /// <returns>The serialized JSON.</returns>
    /// <remarks>Delegates to <see cref="Serialize{T}"/>, so <paramref name="type"/> does not change the output.</remarks>
    public virtual string Serialize(Type type, object payload)
    {
        return Serialize(payload);
    }

    /// <summary>Serializes a strongly-typed <paramref name="payload"/> to a JSON string, camelCasing property names only.</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="payload">The object to serialize.</param>
    /// <returns>The serialized JSON.</returns>
    public virtual string Serialize<T>(T payload)
    {
        return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
        {
            // CamelCasePropertyNamesContractResolver's default naming strategy has
            // ProcessDictionaryKeys = true, so it camel-cases DICTIONARY KEYS too (not just property
            // names) - corrupting free-form keys ("MyKey" -> "myKey"), and deserialize doesn't undo
            // it, so a round-trip silently loses the original keys. The default System.Text.Json
            // serializer does not rename dictionary keys; match it by camel-casing property names only.
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = true
                }
            }
        });
    }

    /// <summary>Deserializes a JSON string as <paramref name="type"/>.</summary>
    /// <param name="type">The runtime type to deserialize into.</param>
    /// <param name="payload">The JSON string.</param>
    /// <returns>The deserialized object.</returns>
    public virtual object? Deserialize(Type type, string payload)
    {
        return JsonConvert.DeserializeObject(payload, type);
    }

    /// <summary>Deserializes a JSON string to a strongly-typed object.</summary>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="payload">The JSON string.</param>
    /// <returns>The deserialized object.</returns>
    public virtual T? Deserialize<T>(string payload)
    {
        return JsonConvert.DeserializeObject<T>(payload, new JsonSerializerSettings());
    }
}
