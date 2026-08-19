using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Benzene.Mesh.Wire;

/// <summary>
/// The mesh ServiceDescriptor wire shape (docs/specification/mesh.md §2): the service's
/// self-description, derived at startup from the message-handler registry - never hand-maintained.
/// Also the body of a <c>mesh:register</c> message (§4). Wire field names are camelCase; use
/// <see cref="MeshJson.Options"/> (or any camelCase serializer) on the wire.
/// </summary>
public class MeshServiceDescriptor
{
    public string Service { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstanceId { get; set; }

    public string Runtime { get; set; } = "dotnet";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Binding { get; set; }

    public MeshPlacement Placement { get; set; } = new();

    public List<MeshTopicDescriptor> Topics { get; set; } = new();

    /// <summary>
    /// Every registered outbound topic (spec §2, §2.3): what this service <b>produces</b>, derived
    /// from its outbound registration - never from scanning call sites. Same shape and
    /// schema-derivation rules as <see cref="Topics"/>. This is what a collector reads to build
    /// <b>provider</b> edges (spec §4) - a topic absent here is not produced by this service,
    /// regardless of what traffic has or hasn't flowed.
    /// </summary>
    /// <remarks>
    /// Named <c>produces</c>, and paired with <see cref="Topics"/> meaning what this service
    /// consumes, since the 2026-08 role inversion (spec §4, mesh.md): a service that registers a
    /// handler for a topic is that topic's CONSUMER, which is how every broker in the field
    /// (Kafka, SQS/SNS, EventBridge, Pub/Sub) uses the word. Before that this field was
    /// <c>consumes</c> and the roles were the other way round.
    /// </remarks>
    public List<MeshTopicDescriptor> Produces { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DescriptorHash { get; set; }

    /// <summary>
    /// Names the feeds that were unavailable when the descriptor was built (spec §2: "registry" for
    /// <see cref="Topics"/>, "outbound-registry" for <see cref="Produces"/>), so a reduced descriptor
    /// is distinguishable from a service that produces/consumes nothing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Degraded { get; set; }

    /// <summary>
    /// The service's self-assessed conformance profile (spec §2), when it claims one - e.g. the
    /// Cloud Service Profile (docs/specification/cloud-service-profile.md). Optional; omitted by
    /// services that don't self-assess. Like <see cref="Degraded"/>, this is self-description
    /// status rather than contract, so it is excluded from the descriptor hash.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MeshProfile? Profile { get; set; }
}

/// <summary>
/// A named conformance-profile claim carried on the descriptor (spec §2's <c>profile</c> field).
/// <see cref="Missing"/> lists the profile's requirement ids the service knows it does not satisfy;
/// null or empty means the service self-assesses as fully conformant to <see cref="Name"/>.
/// </summary>
public class MeshProfile
{
    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Missing { get; set; }
}

/// <summary>One registered topic in a descriptor (spec §2), with the §2.1-derived payload schemas.</summary>
public class MeshTopicDescriptor
{
    public string Id { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? RequestSchema { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? ResponseSchema { get; set; }
}

/// <summary>Where a service instance runs (spec §2). Region is emitted only when actually known.</summary>
public class MeshPlacement
{
    public string Cloud { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }
}
