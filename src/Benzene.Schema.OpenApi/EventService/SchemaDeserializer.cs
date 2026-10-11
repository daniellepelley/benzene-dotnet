using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Benzene.Schema.OpenApi.EventService;

/// <summary>
/// Reads a Contract Document (contract-document.md) from JSON.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A <c>schemaDialect</c> other than <c>openapi-3.0</c> is rejected
/// (<see cref="UnknownSchemaDialectException"/>, contract-document.md §1).</item>
/// <item>Any <c>benzene:</c>-prefixed entry is ignored (§1).</item>
/// <item>An absent <c>response</c> reads as null - no payload (§2, §3.2).</item>
/// <item>A <b>legacy</b> document (spec-0.2.0 and earlier: <c>openapi</c> and <c>info</c>, no
/// <c>schemaDialect</c>) is still accepted, with a warning in <see cref="Warnings"/>: <c>service</c>,
/// <c>serviceVersion</c> and <c>description</c> come from <c>info</c>, and its reserved entries are
/// ignored like any other <c>benzene:</c> entry.</item>
/// </list>
/// </remarks>
public class EventServiceDocumentDeserializer
{
    /// <summary>The warning a legacy-shape document produces.</summary>
    public const string LegacyShapeWarning =
        "The contract document has the legacy (spec-0.2.0) shape - 'openapi'/'info' and no 'schemaDialect'. " +
        "It was read, but regenerate it with the current producer (contract-document.md §1).";

    private readonly OpenApiStringReader _openApiStringReader = new();
    private readonly List<string> _warnings = new();
    private readonly Action<string>? _onWarning;

    /// <summary>Initializes a deserializer.</summary>
    /// <param name="onWarning">Called with each warning as it is raised (e.g. a legacy-shape document); optional.</param>
    public EventServiceDocumentDeserializer(Action<string>? onWarning = null)
    {
        _onWarning = onWarning;
    }

    /// <summary>The warnings raised by every <see cref="Deserialize"/> call so far.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Whether <paramref name="jObject"/> is a legacy-shape document: one with no <c>schemaDialect</c> (spec-0.2.0 and earlier wrote <c>openapi</c> and <c>info</c> instead).</summary>
    public static bool IsLegacyShape(JObject jObject) => jObject["schemaDialect"] == null;

    /// <summary>Whether <paramref name="json"/> is a legacy-shape document (see <see cref="IsLegacyShape(JObject)"/>).</summary>
    public static bool IsLegacyShape(string json) => IsLegacyShape(JObject.Parse(json));

    /// <summary>Reads a document.</summary>
    /// <exception cref="UnknownSchemaDialectException">The document declares a dialect this port does not know.</exception>
    public EventServiceDocument Deserialize(string json)
    {
        // A fresh builder (hence a fresh SchemaBuilder/SchemaRepository) per call keeps this
        // instance safe to call more than once: SchemaBuilder.AddSchema is first-write-wins, so
        // reusing one builder/repository across two Deserialize() calls that share a schema name
        // (e.g. a baseline vs. current spec of the same evolving service) would silently resolve
        // the second document's schema to the first document's old definition.
        var eventServiceDocumentBuilder = new EventServiceDocumentBuilder();

        // DateParseHandling.None keeps ISO-date-looking strings (e.g. example payload values) as
        // strings, so they round-trip through serialize → deserialize → serialize unchanged.
        using var stringReader = new StringReader(json);
        using var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None };
        var jObject = JObject.Load(jsonReader);

        var dialect = jObject["schemaDialect"]?.Value<string>();
        var legacy = IsLegacyShape(jObject);
        if (!legacy && dialect != EventServiceDocument.SchemaDialectOpenApi30)
        {
            throw new UnknownSchemaDialectException(dialect);
        }

        if (legacy)
        {
            Warn(LegacyShapeWarning);
        }

        AddSchema(jObject, eventServiceDocumentBuilder);

        var doc = eventServiceDocumentBuilder.Build();
        var info = jObject["info"] as JObject;
        doc.SchemaDialect = EventServiceDocument.SchemaDialectOpenApi30;
        doc.IsLegacyShape = legacy;
        doc.Service = jObject["service"]?.Value<string>() ?? info?["title"]?.Value<string>() ?? string.Empty;
        doc.ServiceVersion = NullIfEmpty(jObject["serviceVersion"]?.Value<string>() ?? info?["version"]?.Value<string>());
        doc.VersionScheme = NullIfEmpty(jObject["versionScheme"]?.Value<string>());
        doc.Description = NullIfEmpty(jObject["description"]?.Value<string>() ?? info?["description"]?.Value<string>());
        doc.Runtime = NullIfEmpty(jObject["runtime"]?.Value<string>());
        if (jObject["placement"] is JObject placement && !string.IsNullOrEmpty(placement["cloud"]?.Value<string>()))
        {
            doc.Placement = new ContractDocumentPlacement(placement["cloud"]!.Value<string>()!, placement["region"]?.Value<string>());
        }
        if (jObject["profile"] is JObject profile && !string.IsNullOrEmpty(profile["name"]?.Value<string>()))
        {
            doc.Profile = new ContractDocumentProfile(profile["name"]!.Value<string>()!,
                (profile["missing"] as JArray)?.Select(x => x.Value<string>()).OfType<string>().ToArray());
        }
        doc.Degraded = (jObject["degraded"] as JArray)?.Select(x => x.Value<string>()).OfType<string>().ToArray()
                       ?? Array.Empty<string>();
        doc.MessageEndpoint = jObject["messageEndpoint"]?.Value<string>();
        doc.Transports = GetTransports(jObject);
        doc.DeclaredContractHash = jObject["contractHash"]?.Value<string>();
        doc.DeclaredRegistrationHash = jObject["registrationHash"]?.Value<string>();
        doc.Events = GetEntries(jObject, "events").Select(GetEvent).ToArray();
        doc.Requests = GetEntries(jObject, "requests").Select(GetRequest).ToArray();
        doc.Sends = GetEntries(jObject, "sends").Select(GetSend).ToArray();
        return doc;
    }

    private void Warn(string warning)
    {
        _warnings.Add(warning);
        _onWarning?.Invoke(warning);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // §1: a consumer ignores any benzene:-prefixed entry. #243: a missing list reads as empty.
    private static IEnumerable<JToken> GetEntries(JObject jObject, string name)
        => (jObject.GetValue(name) as JArray ?? new JArray())
            .Where(x => !Benzene.Abstractions.BenzeneTopic.IsReserved(x["topic"]?.Value<string>()));

    private void AddSchema(JObject jObject, EventServiceDocumentBuilder eventServiceDocumentBuilder)
    {
        var schemaJToken = jObject[OpenApiConstants.Components]?[OpenApiConstants.Schemas];

        if (schemaJToken == null)
        {
            return;
        }

        foreach (var x in schemaJToken)
        {
            var p = (JProperty)x;
            var schema =
                _openApiStringReader.ReadFragment<OpenApiSchema>(p.Value.ToString(), OpenApiSpecVersion.OpenApi3_0,
                    out _);
            var key = p.Path.Split('.').Last();
            eventServiceDocumentBuilder.AddSchema(key, schema);
        }
    }

    private static string[] GetTransports(JObject jObject)
    {
        var transportsJArray = jObject.GetValue("transports") as JArray;
        return transportsJArray?.Select(x => x.Value<string>()!).ToArray() ?? Array.Empty<string>();
    }

    private Event GetEvent(JToken jToken)
    {
        var @event = JsonConvert.DeserializeObject<Event>(jToken.ToString(Formatting.Indented));

        return new Event(@event!.Topic, GetSchema(jToken, "message") ?? new OpenApiSchema())
        {
            Version = @event.Version ?? string.Empty,
            Example = GetExample(jToken)
        };
    }

    private RequestResponse GetRequest(JToken jToken)
    {
        var request = JsonConvert.DeserializeObject<RequestResponse>(jToken.ToString(Formatting.Indented));

        request!.Version ??= string.Empty;
        request.Request = GetSchema(jToken, "request") ?? new OpenApiSchema();
        request.Response = GetSchema(jToken, "response");
        request.Example = GetExample(jToken);

        return request;
    }

    private Send GetSend(JToken jToken)
    {
        return new Send(jToken["topic"]?.Value<string>() ?? string.Empty,
            GetSchema(jToken, "request") ?? new OpenApiSchema(), GetSchema(jToken, "response"))
        {
            Version = jToken["version"]?.Value<string>() ?? string.Empty,
        };
    }

    private OpenApiSchema? GetSchema(JToken jToken, string path)
    {
        var token = jToken[path];
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        return _openApiStringReader.ReadFragment<OpenApiSchema>(token.ToString(), OpenApiSpecVersion.OpenApi3_0, out _);
    }

    private static IOpenApiAny? GetExample(JToken jToken)
    {
        var example = jToken["example"];
        return example == null ? null : ToOpenApiAny(example);
    }

    private static IOpenApiAny ToOpenApiAny(JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
                var openApiObject = new OpenApiObject();
                foreach (var property in ((JObject)token).Properties())
                {
                    openApiObject[property.Name] = ToOpenApiAny(property.Value);
                }

                return openApiObject;
            case JTokenType.Array:
                var openApiArray = new OpenApiArray();
                foreach (var item in (JArray)token)
                {
                    openApiArray.Add(ToOpenApiAny(item));
                }

                return openApiArray;
            case JTokenType.Integer:
                return new OpenApiLong(token.Value<long>());
            case JTokenType.Float:
                return new OpenApiDouble(token.Value<double>());
            case JTokenType.Boolean:
                return new OpenApiBoolean(token.Value<bool>());
            case JTokenType.Null:
                return new OpenApiNull();
            default:
                return new OpenApiString(token.Value<string>() ?? string.Empty);
        }
    }
}
