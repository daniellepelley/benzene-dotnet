using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Benzene.Abstractions.MessageHandlers;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Mesh.Wire;

/// <summary>
/// Builds the ServiceDescriptor (docs/specification/mesh.md §2) from the live message-handler
/// registry (what the service provides), the outbound registry (§2.3, what it consumes) plus static
/// identity. Call it after registration is complete (registration is a startup activity). A null
/// lookup is not an error: per the spec's degradation rule (§6) the descriptor is built without the
/// corresponding list and the missing feed is recorded in <see cref="MeshServiceDescriptor.Degraded"/>,
/// so a service whose registry (or outbound registry) isn't wired up still participates in the mesh
/// reduced, rather than not at all.
/// </summary>
public static class MeshDescriptorFactory
{
    public const string RegistryFeed = "registry";
    public const string OutboundRegistryFeed = "outbound-registry";

    public static MeshServiceDescriptor Create(
        IMessageHandlerDefinitionLookUp? lookUp,
        MeshServiceInfo info,
        IMeshOutboundDefinitionLookUp? outboundLookUp = null)
    {
        var descriptor = new MeshServiceDescriptor
        {
            Service = info.Service,
            ServiceVersion = info.ServiceVersion,
            InstanceId = info.InstanceId,
            Binding = info.Binding,
            Placement = info.Placement == null
                ? new MeshPlacement { Cloud = "self-hosted" }
                : new MeshPlacement { Cloud = info.Placement.Cloud, Region = info.Placement.Region }
        };

        var degraded = new List<string>();

        if (lookUp == null)
        {
            degraded.Add(RegistryFeed);
        }
        else
        {
            descriptor.Topics = lookUp.GetAllHandlers()
                .OrderBy(x => x.Topic.Id, StringComparer.Ordinal)
                .ThenBy(x => x.Topic.Version, StringComparer.Ordinal)
                .Select(definition => new MeshTopicDescriptor
                {
                    Id = definition.Topic.Id,
                    Version = string.IsNullOrEmpty(definition.Topic.Version) ? null : definition.Topic.Version,
                    RequestSchema = MeshSchemaGenerator.Derive(definition.RequestType),
                    ResponseSchema = MeshSchemaGenerator.Derive(definition.ResponseType)
                })
                .ToList();
        }

        if (outboundLookUp == null)
        {
            degraded.Add(OutboundRegistryFeed);
        }
        else
        {
            descriptor.Consumes = outboundLookUp.GetAllDefinitions()
                .OrderBy(x => x.Topic.Id, StringComparer.Ordinal)
                .ThenBy(x => x.Topic.Version, StringComparer.Ordinal)
                .Select(definition => new MeshTopicDescriptor
                {
                    Id = definition.Topic.Id,
                    Version = string.IsNullOrEmpty(definition.Topic.Version) ? null : definition.Topic.Version,
                    RequestSchema = MeshSchemaGenerator.Derive(definition.RequestType),
                    // spec §2: responseSchema is {} (unconstrained) when the sender doesn't declare an
                    // expected response type - Void is the "declared nothing" sentinel, not a real shape.
                    ResponseSchema = definition.ResponseType == typeof(Void)
                        ? new JsonObject()
                        : MeshSchemaGenerator.Derive(definition.ResponseType)
                })
                .ToList();
        }

        if (degraded.Count > 0)
        {
            descriptor.Degraded = degraded;
        }

        descriptor.DescriptorHash = MeshDescriptorHashing.ComputeHash(descriptor);
        return descriptor;
    }
}

/// <summary>
/// The contract hash of docs/specification/mesh.md §2.2: SHA-256 over the descriptor's canonical
/// JSON with the per-instance (<c>instanceId</c>), transient (<c>degraded</c>), self-described
/// (<c>profile</c>), and self-referential (<c>descriptorHash</c>) fields blanked - two instances of
/// one build hash identically, and the hash changes exactly when the contract changes - including
/// <c>consumes</c> (§2.2: "adding, removing, or re-typing a consumed topic is a contract change
/// exactly as ... a provided one is"). Notably <c>profile</c> MUST NOT participate in the hash
/// (§2.2), so a profile change never re-hashes the contract. Distinct from
/// <c>Benzene.Mesh.Contracts.MeshHashing</c> (HMAC over raw OpenAPI text), which serves the
/// aggregator's artifact-drift feature; this one is the spec's wire-level hash.
/// </summary>
public static class MeshDescriptorHashing
{
    public static string ComputeHash(MeshServiceDescriptor descriptor)
    {
        var canonical = new MeshServiceDescriptor
        {
            Service = descriptor.Service,
            ServiceVersion = descriptor.ServiceVersion,
            InstanceId = null,
            Runtime = descriptor.Runtime,
            Binding = descriptor.Binding,
            Placement = descriptor.Placement,
            Topics = descriptor.Topics,
            Consumes = descriptor.Consumes,
            DescriptorHash = null,
            Degraded = null,
            Profile = null
        };
        var json = MeshJson.Serialize(canonical);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
