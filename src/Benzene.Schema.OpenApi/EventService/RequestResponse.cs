using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using Newtonsoft.Json;

namespace Benzene.Schema.OpenApi.EventService;

public class RequestResponse : IOpenApiSerializable
{
    public string Topic { get; set; }

    /// <summary>
    /// The topic's handler version (core-concepts.md §2) — distinct from a payload schema
    /// version. Empty for the unversioned handler. Written to the wire only when non-empty (an
    /// unversioned topic's JSON carries no <c>version</c> field at all, not an empty string) -
    /// defaults to <see cref="string.Empty"/>, not <c>null</c>, so a round-tripped (deserialized)
    /// unversioned topic matches a freshly built one.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    public HttpMapping[] HttpMappings { get; set; } = Array.Empty<HttpMapping>();
    public OpenApiSchema Request { get; set; }

    /// <summary>
    /// The response payload's schema. <b>Null (absent on the wire) means the handler returns no
    /// payload</b> (contract-document.md §2); <c>{}</c> means a payload of unconstrained shape.
    /// </summary>
    public OpenApiSchema? Response { get; set; }

    /// <summary>
    /// An author-supplied example request payload for this topic, or null. A producer emits only
    /// examples an author supplied - none are synthesized from the schema. Ignored by
    /// Newtonsoft deserialization (an <see cref="IOpenApiAny"/> can't be materialized from JSON
    /// directly); <see cref="EventServiceDocumentDeserializer"/> reads it back explicitly.
    /// </summary>
    [JsonIgnore]
    public IOpenApiAny? Example { get; set; }

    public void SerializeAsV3(IOpenApiWriter writer)
    {
        writer.WriteStartObject();

        writer.WriteProperty("topic", Topic);
        if (!string.IsNullOrEmpty(Version))
        {
            writer.WriteProperty("version", Version);
        }
        writer.WriteOptionalCollection("httpMappings", HttpMappings, (w, o) => o.SerializeAsV3(w));
        writer.WriteRequiredObject("request", Request, (w, o) => o.SerializeAsV3(w));
        if (Response != null)
        {
            writer.WriteRequiredObject("response", Response, (w, o) => o.SerializeAsV3(w));
        }

        if (Example != null)
        {
            writer.WritePropertyName("example");
            writer.WriteAny(Example);
        }

        writer.WriteEndObject();
    }

    public void SerializeAsV2(IOpenApiWriter writer)
    {
        SerializeAsV3(writer);
    }
}
