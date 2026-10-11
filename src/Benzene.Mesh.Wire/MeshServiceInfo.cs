namespace Benzene.Mesh.Wire;

/// <summary>
/// The static identity a service stamps on its mesh feeds - traces and issues
/// (<see cref="Extensions.UseMeshTrace{TContext}"/>, <see cref="Extensions.UseMeshIssues{TContext}"/>)
/// and heartbeats (docs/specification/mesh.md §3, §5). Its registration is its contract document
/// (mesh.md §2), which carries its own identity. Every field but <see cref="Service"/> is optional.
/// </summary>
public class MeshServiceInfo
{
    /// <summary>Initializes the identity.</summary>
    /// <param name="service">The service's name.</param>
    /// <param name="serviceVersion">The release this instance runs, or null when it declares none.</param>
    /// <param name="instanceId">This instance's id, or null.</param>
    public MeshServiceInfo(string service, string? serviceVersion = null, string? instanceId = null)
    {
        Service = service;
        ServiceVersion = string.IsNullOrEmpty(serviceVersion) ? null : serviceVersion;
        InstanceId = instanceId;
    }

    /// <summary>The service's name.</summary>
    public string Service { get; }

    /// <summary>The release this instance runs (mesh.md §5: the heartbeat's <c>serviceVersion</c>), or null.</summary>
    public string? ServiceVersion { get; }

    /// <summary>This instance's id, or null.</summary>
    public string? InstanceId { get; }
}
