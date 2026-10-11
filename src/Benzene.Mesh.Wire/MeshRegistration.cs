using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Benzene.Mesh.Wire;

/// <summary>
/// The body of a <c>benzene:mesh:register</c> message as a collector reads it (mesh.md §2): the
/// service's Contract Document - the document its <c>benzene:spec</c> topic serves, including the
/// deployment-side fields (<c>runtime</c>, <c>placement</c>, <c>profile</c>, <c>degraded</c>). A service
/// produces it with <c>Benzene.Schema.OpenApi</c>'s document builder; this type is only the collector's
/// view of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only <see cref="Service"/> is required.</b> The REQUIRED fields of contract-document.md §1 bind
/// producers; a collector does not interpret schemas, so it neither checks <c>schemaDialect</c> nor
/// requires any other member. A member that is absent (a null list here), or that
/// <see cref="Degraded"/> names, is <b>unknown</b> (<see cref="IsKnown"/>): it contributes no edges,
/// and a trace on one of its topics is not undeclared.
/// </para>
/// <para>
/// Every member this type does not model (<c>components</c>, <c>schemaDialect</c>, <c>httpMappings</c>,
/// a future field) is kept in <see cref="Extra"/>, so the registered document reads back whole.
/// </para>
/// </remarks>
public class MeshRegistration
{
    /// <summary>The service's name - the only member a collector requires.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Which release this is; with <see cref="Service"/>, the catalog key (mesh.md §4).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceVersion { get; set; }

    /// <summary>How versions order (service-versions.md §2). Not part of the catalog key.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VersionScheme { get; set; }

    /// <summary>Free text.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>The implementing port (<c>dotnet</c>, <c>go</c>, …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Runtime { get; set; }

    /// <summary>Where the service runs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MeshPlacement? Placement { get; set; }

    /// <summary>The service's conformance-profile self-assessment.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MeshProfile? Profile { get; set; }

    /// <summary>The members the producer could not determine (<c>requests</c>, <c>events</c>, <c>sends</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Degraded { get; set; }

    /// <summary>The service's envelope endpoint over HTTP.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageEndpoint { get; set; }

    /// <summary>The transports the service receives messages over.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Transports { get; set; }

    /// <summary>The document's <c>contractHash</c>, as carried.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractHash { get; set; }

    /// <summary>
    /// The document's <c>registrationHash</c>, as carried. A collector trusts it: heartbeats are compared
    /// with this value (mesh.md §5), and a registration is never rejected over it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistrationHash { get; set; }

    /// <summary>The topics the service serves - it is their consumer. Null when absent (unknown).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MeshRegisteredEntry>? Requests { get; set; }

    /// <summary>The events the service publishes - it is their provider. Null when absent (unknown).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MeshRegisteredEntry>? Events { get; set; }

    /// <summary>The messages the service sends to topics others define - it is their provider. Null when absent (unknown).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MeshRegisteredEntry>? Sends { get; set; }

    /// <summary>Every member not modelled above, kept as received.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// Whether <paramref name="member"/> (<see cref="MeshRegistrationMembers"/>) is known: present, and
    /// not named in <see cref="Degraded"/>. An unknown member asserts nothing - no edges, no drift.
    /// </summary>
    public bool IsKnown(string member) => Entries(member) != null && !(Degraded?.Contains(member) ?? false);

    /// <summary>
    /// The entries of a known <paramref name="member"/>, with <c>benzene:</c> entries dropped
    /// (contract-document.md §1: a consumer ignores them); empty for an unknown member.
    /// </summary>
    public IEnumerable<MeshRegisteredEntry> KnownEntries(string member) =>
        IsKnown(member)
            ? Entries(member)!.Where(x => x != null && !string.IsNullOrEmpty(x.Topic)
                                          && !Benzene.Abstractions.BenzeneTopic.IsReserved(x.Topic))
            : Enumerable.Empty<MeshRegisteredEntry>();

    private List<MeshRegisteredEntry>? Entries(string member) => member switch
    {
        MeshRegistrationMembers.Requests => Requests,
        MeshRegistrationMembers.Events => Events,
        MeshRegistrationMembers.Sends => Sends,
        _ => null
    };
}

/// <summary>The contract document's three entry lists, by their member names (contract-document.md §1).</summary>
public static class MeshRegistrationMembers
{
    /// <summary>The topics a service serves.</summary>
    public const string Requests = "requests";

    /// <summary>The events a service publishes.</summary>
    public const string Events = "events";

    /// <summary>The messages a service sends to topics others define.</summary>
    public const string Sends = "sends";
}

/// <summary>One <c>requests</c>, <c>events</c> or <c>sends</c> entry of a registered document.</summary>
public class MeshRegisteredEntry
{
    /// <summary>The topic.</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>The topic version; empty or absent when unversioned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>The request schema (<c>requests</c>, <c>sends</c>), as carried.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Request { get; set; }

    /// <summary>The response schema (<c>requests</c>, <c>sends</c>), as carried.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Response { get; set; }

    /// <summary>The message schema (<c>events</c>), as carried.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Message { get; set; }

    /// <summary>Every other member (<c>httpMappings</c>, <c>example</c>, …), kept as received.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Where a service runs (contract-document.md §1). Region is present only where the platform documents it.</summary>
public class MeshPlacement
{
    /// <summary>The cloud (<c>aws</c>, <c>azure</c>, <c>gcp</c>, <c>self-hosted</c>, …).</summary>
    public string Cloud { get; set; } = string.Empty;

    /// <summary>The region, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }
}

/// <summary>A conformance-profile self-assessment carried on the document (contract-document.md §1).</summary>
public class MeshProfile
{
    /// <summary>The profile's name (<c>cloud-service</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The requirement ids the service's wiring knows it does not meet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Missing { get; set; }
}
