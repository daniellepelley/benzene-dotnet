using System.Text.Json.Nodes;
using Benzene.CodeGen.Core;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;

namespace Benzene.Schema.OpenApi.EventService;

/// <summary>
/// The Contract Document (contract-document.md) - what a service derives from its registrations to
/// describe itself: the topics it serves (<see cref="Requests"/>), the events it publishes
/// (<see cref="Events"/>), the messages it sends to topics others define (<see cref="Sends"/>), their
/// payload schemas (<see cref="Components"/>) and how its topics are reachable over HTTP. It is what
/// the reserved <c>benzene:spec</c> topic answers with and what every language's client generator
/// reads, and - with its deployment-side fields - the body a mesh-enabled service registers with a
/// collector (mesh.md §2).
/// </summary>
/// <remarks>
/// Serialized, it carries <c>schemaDialect</c> (<see cref="SchemaDialectOpenApi30"/>), the service's
/// identity (<c>service</c>, <c>serviceVersion</c>, <c>versionScheme</c>, <c>description</c>), the
/// optional deployment-side fields (<c>runtime</c>, <c>placement</c>, <c>profile</c>, <c>degraded</c>),
/// the optional <c>messageEndpoint</c> and <c>transports</c>, both hashes (<c>contractHash</c> and
/// <c>registrationHash</c>, always computed over the document as written - <see cref="ContractHash"/>),
/// the three entry lists and <c>components</c>. It is not itself an OpenAPI document; its schemas are
/// OpenAPI 3.0 Schema Objects.
/// </remarks>
public class EventServiceDocument : IOpenApiSerializable
{
    /// <summary>The only schema dialect this port reads or writes.</summary>
    public const string SchemaDialectOpenApi30 = "openapi-3.0";

    /// <summary>Initializes a document with no sends.</summary>
    public EventServiceDocument(RequestResponse[] requests, Event[] events, OpenApiComponents components)
        : this(requests, events, Array.Empty<Send>(), components)
    {
    }

    /// <summary>Initializes a document.</summary>
    public EventServiceDocument(RequestResponse[] requests, Event[] events, Send[] sends, OpenApiComponents components)
    {
        Requests = requests;
        Events = events;
        Sends = sends;
        Components = components;
    }

    /// <summary>The dialect of every schema in the document. Always <see cref="SchemaDialectOpenApi30"/> for a document this port produces.</summary>
    public string SchemaDialect { get; set; } = SchemaDialectOpenApi30;

    /// <summary>The service's name (REQUIRED). From <see cref="Benzene.Abstractions.MessageHandlers.Info.IApplicationInfo.Name"/>.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Which release this is, or null (service-versions.md).</summary>
    public string? ServiceVersion { get; set; }

    /// <summary>The comparison scheme of <see cref="ServiceVersion"/>, or null (service-versions.md §2).</summary>
    public string? VersionScheme { get; set; }

    /// <summary>Free text, or null.</summary>
    public string? Description { get; set; }

    /// <summary>The implementing port (<c>dotnet</c>), or null (contract-document.md §1). Outside both hashes.</summary>
    public string? Runtime { get; set; }

    /// <summary>Where the service runs, or null (contract-document.md §1). Outside both hashes.</summary>
    public ContractDocumentPlacement? Placement { get; set; }

    /// <summary>The service's conformance-profile self-assessment, or null (contract-document.md §1). Outside both hashes.</summary>
    public ContractDocumentProfile? Profile { get; set; }

    /// <summary>
    /// The members (<see cref="ContractDocumentMembers"/>) the producer could not determine; empty, and
    /// then omitted, when it determined all three (contract-document.md §1). Outside both hashes.
    /// </summary>
    public string[] Degraded { get; set; } = Array.Empty<string>();

    /// <summary>The topics this service serves.</summary>
    public RequestResponse[] Requests { get; set; }

    /// <summary>The events this service publishes (it owns their contract).</summary>
    public Event[] Events { get; set; }

    /// <summary>The messages this service sends to topics another party defines.</summary>
    public Send[] Sends { get; set; }

    /// <summary><c>components.schemas</c>: every <c>$ref</c> in the document points in here.</summary>
    public OpenApiComponents Components { get; set; }

    /// <summary>
    /// The path of the service's BenzeneMessage-over-HTTP endpoint (see
    /// <c>Benzene.Http.BenzeneMessage</c>'s <c>UseBenzeneMessage</c>), or null when the service
    /// doesn't expose one. Serialized as the optional top-level <c>messageEndpoint</c> field —
    /// spec consumers feature-detect send capability on it.
    /// </summary>
    public string? MessageEndpoint { get; set; }

    /// <summary>
    /// The names of every transport this host is wired to receive messages over (e.g. <c>"sqs"</c>,
    /// <c>"kafka"</c>, <c>"http"</c>), sourced from every registered
    /// <see cref="Benzene.Abstractions.MessageHandlers.Info.ITransportInfo"/> at spec-build time.
    /// Document-level, not per-topic: any wired non-HTTP transport can reach any registered handler by
    /// topic. HTTP is the one exception — a topic's actual HTTP reachability is its own
    /// <see cref="RequestResponse.HttpMappings"/>. Serialized only when non-empty.
    /// </summary>
    public string[] Transports { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The <c>contractHash</c> this document carries when it was read from JSON (the served value), or
    /// null for a document built in this process. Serialization always writes the hash computed over the
    /// document as written; this is only what a reader saw.
    /// </summary>
    public string? DeclaredContractHash { get; set; }

    /// <summary>
    /// True when this document was read from the legacy (spec-0.2.0) shape - no <c>schemaDialect</c>.
    /// A compatibility baseline in that shape must be regenerated (<c>LegacyBaselineException</c>).
    /// </summary>
    public bool IsLegacyShape { get; set; }

    /// <summary>As <see cref="DeclaredContractHash"/>, for <c>registrationHash</c>.</summary>
    public string? DeclaredRegistrationHash { get; set; }

    /// <summary>The document's <c>contractHash</c>, computed over it as it stands (contract-document.md §6).</summary>
    public string ContractHash => Benzene.CodeGen.Core.ContractHash.Compute(this);

    /// <summary>The document's <c>registrationHash</c>, computed over it as it stands (contract-document.md §6).</summary>
    public string RegistrationHash => Benzene.CodeGen.Core.ContractHash.ComputeRegistrationHash(this);

    /// <summary>
    /// A copy with the given lists replaced and everything else - identity, endpoint, transports - kept.
    /// The hashes follow the copy: they are always computed over the document as it stands.
    /// </summary>
    public EventServiceDocument With(RequestResponse[]? requests = null, Event[]? events = null, Send[]? sends = null,
        OpenApiComponents? components = null)
    {
        return new EventServiceDocument(requests ?? Requests, events ?? Events, sends ?? Sends, components ?? Components)
        {
            SchemaDialect = SchemaDialect,
            Service = Service,
            ServiceVersion = ServiceVersion,
            VersionScheme = VersionScheme,
            Description = Description,
            Runtime = Runtime,
            Placement = Placement,
            Profile = Profile,
            Degraded = Degraded,
            MessageEndpoint = MessageEndpoint,
            Transports = Transports,
        };
    }

    /// <inheritdoc />
    public void SerializeAsV3(IOpenApiWriter writer)
    {
        var hashes = ToJsonObject(includeHashes: false);
        Write(writer, Benzene.CodeGen.Core.ContractHash.Compute(hashes),
            Benzene.CodeGen.Core.ContractHash.ComputeRegistrationHash(hashes));
    }

    /// <inheritdoc />
    public void SerializeAsV2(IOpenApiWriter writer)
    {
        SerializeAsV3(writer);
    }

    /// <summary>The document as JSON, with or without its hash fields.</summary>
    internal JsonObject ToJsonObject(bool includeHashes)
    {
        string json;
        if (includeHashes)
        {
            json = this.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        }
        else
        {
            using var textWriter = new StringWriter();
            var writer = new OpenApiJsonWriter(textWriter);
            Write(writer, null, null);
            writer.Flush();
            json = textWriter.ToString();
        }

        return JsonNode.Parse(json) as JsonObject
               ?? throw new InvalidOperationException("A serialized contract document did not parse as a JSON object.");
    }

    private void Write(IOpenApiWriter writer, string? contractHash, string? registrationHash)
    {
        writer.WriteStartObject();
        writer.WriteProperty("schemaDialect", SchemaDialect);
        writer.WriteProperty("service", Service ?? string.Empty);
        WriteOptional(writer, "serviceVersion", ServiceVersion);
        WriteOptional(writer, "versionScheme", VersionScheme);
        WriteOptional(writer, "description", Description);
        WriteOptional(writer, "runtime", Runtime);
        if (Placement != null)
        {
            writer.WritePropertyName("placement");
            writer.WriteStartObject();
            writer.WriteProperty("cloud", Placement.Cloud);
            WriteOptional(writer, "region", Placement.Region);
            writer.WriteEndObject();
        }
        if (Profile != null)
        {
            writer.WritePropertyName("profile");
            writer.WriteStartObject();
            writer.WriteProperty("name", Profile.Name);
            writer.WriteOptionalCollection("missing", Profile.Missing, (w, m) => w.WriteValue(m));
            writer.WriteEndObject();
        }
        writer.WriteOptionalCollection("degraded", Degraded, (w, d) => w.WriteValue(d));
        WriteOptional(writer, "messageEndpoint", MessageEndpoint);
        writer.WriteOptionalCollection("transports", Transports, (w, t) => w.WriteValue(t));
        WriteOptional(writer, "contractHash", contractHash);
        WriteOptional(writer, "registrationHash", registrationHash);
        writer.WriteRequiredCollection("requests", Requests, (w, c) => c.SerializeAsV3(w));
        writer.WriteRequiredCollection("events", Events, (w, c) => c.SerializeAsV3(w));
        writer.WriteRequiredCollection("sends", Sends, (w, c) => c.SerializeAsV3(w));
        writer.WriteRequiredObject(OpenApiConstants.Components, Components, (w, c) => c.SerializeAsV3(w));
        writer.WriteEndObject();
    }

    private static void WriteOptional(IOpenApiWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteProperty(name, value);
        }
    }
}
