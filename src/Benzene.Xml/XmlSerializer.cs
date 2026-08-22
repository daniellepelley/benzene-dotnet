using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using Benzene.Abstractions.Serialization;

namespace Benzene.Xml;

/// <summary>
/// <see cref="ISerializer"/> backed by <see cref="System.Xml.Serialization.XmlSerializer"/>, caching
/// one instance per CLR type.
/// </summary>
public class XmlSerializer : ISerializer
{
    // A plain StringWriter always reports Encoding.Unicode (UTF-16), so XmlWriter stamps
    // `encoding="utf-16"` into the XML declaration. The body is returned as a string and then
    // transmitted as UTF-8 (like every other body), so that declaration contradicts the bytes on the
    // wire and a conformant XML client that honors it fails to parse the response. Report UTF-8 so the
    // declaration matches the wire.
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    // System.Xml.Serialization.XmlSerializer's own constructor already caches (and reuses) the
    // dynamically-generated serialization assembly per type internally, but still repeats its own
    // cache lookup/lock and object construction on every call; caching the instance here removes
    // that repeated overhead for a type once it's been serialized/deserialized once.
    private static readonly ConcurrentDictionary<Type, System.Xml.Serialization.XmlSerializer> SerializersByType = new();

    /// <summary>Serializes <paramref name="payload"/> as <paramref name="type"/> to an XML string.</summary>
    /// <param name="type">The runtime type of the object to serialize.</param>
    /// <param name="payload">The object to serialize; <c>null</c> serializes to an empty string.</param>
    /// <returns>The serialized XML.</returns>
    public string Serialize(Type type, object payload)
    {
        // Match the generic Serialize<T> and the sibling serializers (Avro/MessagePack): a null
        // payload serializes to empty rather than throwing inside the BCL XmlSerializer.
        if (payload == null)
        {
            return string.Empty;
        }

        var xsSubmit = GetSerializer(type);

        using var sww = new Utf8StringWriter();
        using var writer = XmlWriter.Create(sww);
        xsSubmit.Serialize(writer, payload);
        return sww.ToString();
    }

    /// <summary>Serializes a strongly-typed <paramref name="payload"/> to an XML string.</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="payload">The object to serialize; <c>null</c> serializes to an empty string.</param>
    /// <returns>The serialized XML.</returns>
    public string Serialize<T>(T payload)
    {
        return payload == null
            ? string.Empty
            : Serialize(typeof(T), payload);
    }

    // XML is deserialized from an untrusted, content-negotiated request body, so the reader is
    // hardened against the classic entity-expansion DoS ("billion laughs"): DtdProcessing.Prohibit
    // rejects any inline DOCTYPE outright, and XmlResolver = null blocks external-entity/DTD fetches
    // (SSRF / local-file disclosure) as defence in depth. A serializer deserialize path never needs
    // a DTD, so prohibiting it costs nothing.
    private static readonly XmlReaderSettings SafeReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    /// <summary>Deserializes an XML string as <paramref name="type"/>.</summary>
    /// <param name="type">The runtime type to deserialize into.</param>
    /// <param name="payload">The XML string.</param>
    /// <returns>The deserialized object.</returns>
    public object? Deserialize(Type type, string payload)
    {
        using var stringReader = new StringReader(payload);
        using var xmlReader = XmlReader.Create(stringReader, SafeReaderSettings);
        return GetSerializer(type).Deserialize(xmlReader);
    }

    /// <summary>Deserializes an XML string to a strongly-typed object.</summary>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="payload">The XML string.</param>
    /// <returns>The deserialized object.</returns>
    public T? Deserialize<T>(string payload)
    {
        var obj = Deserialize(typeof(T), payload);

        if (obj == null)
        {
            return default;
        }

        return (T)obj;
    }

    private static System.Xml.Serialization.XmlSerializer GetSerializer(Type type)
    {
        return SerializersByType.GetOrAdd(type, static t => new System.Xml.Serialization.XmlSerializer(t));
    }
}
