using System.Text.Encodings.Web;
using System.Text.Json;
using Benzene.Abstractions.Serialization;

namespace Benzene.Clients;

/// <summary>
/// A System.Text.Json-backed <see cref="ISerializer"/>: camelCase property names on serialize,
/// case-insensitive property matching on deserialize.
/// </summary>
/// <remarks>
/// Uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>, matching
/// <c>Benzene.Core.MessageHandlers.Serialization.JsonSerializer</c> (the process default) — see that
/// class's remarks for why. Corrected 2026-08-22: <see cref="Shared"/> lacked this encoder when it was
/// first added, so pointing Kafka's and RabbitMq's outbound clients at it (their own prior per-class
/// instances used the process default) silently switched their wire JSON from relaxed to HTML-safe
/// escaping — a real behavioral change for any payload containing <c>&lt;</c>, <c>&gt;</c>,
/// <c>&amp;</c> or <c>'</c>, and a divergence from every other Benzene transport's wire format.
/// </remarks>
public class JsonSerializer : ISerializer
{
    /// <summary>
    /// A shared <see cref="ISerializer"/> instance, for outbound client/health-check code that needs
    /// one but has no reason to own a per-class copy — <see cref="JsonSerializer"/> is stateless once
    /// constructed (its <see cref="JsonSerializerOptions"/> are shared static fields below), so one
    /// instance safely serves every caller. Kafka/RabbitMq/HTTP client code reference this instead of
    /// each declaring its own private static instance.
    /// </summary>
    public static readonly ISerializer Shared = new JsonSerializer();

    // Created once and shared: System.Text.Json caches its reflection-built type metadata per
    // JsonSerializerOptions instance, so per-call options re-paid that whole build on every
    // serialize/deserialize.
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public string Serialize(Type type, object payload)
    {
        return Serialize(payload);
    }

    /// <inheritdoc />
    public string Serialize<T>(T payload)
    {
        return System.Text.Json.JsonSerializer.Serialize(payload, SerializeOptions);
    }

    /// <inheritdoc />
    public object Deserialize(Type type, string payload)
    {
        return System.Text.Json.JsonSerializer.Deserialize(payload, type, DeserializeOptions);
    }

    /// <inheritdoc />
    public T Deserialize<T>(string payload)
    {
        return System.Text.Json.JsonSerializer.Deserialize<T>(payload, DeserializeOptions);
    }
}
