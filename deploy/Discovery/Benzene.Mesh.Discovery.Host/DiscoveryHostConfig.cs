using Benzene.Mesh.Contracts;

namespace Benzene.Mesh.Discovery.Host;

/// <summary>
/// The config-bound shape of <c>discovery.json</c> (or equivalent environment variables, via .NET's
/// standard double-underscore binding, e.g. <c>Filter__TagKey</c>) - mirrors
/// <c>deploy/Mesh/Benzene.Mesh.Host/MeshHostConfig.cs</c>'s binder-driven style (mutable properties,
/// not the constructor-based immutable pattern the rest of the mesh feature uses), for the same
/// reason: <c>IConfiguration.Get&lt;T&gt;()</c> needs settable properties to bind into.
/// </summary>
public class DiscoveryHostConfig
{
    /// <summary>
    /// Which discovery providers to run, by name - see <see cref="DiscoveryProviderFactory.ValidProviderNames"/>
    /// for the valid values. Empty by default: a discovery job with no providers configured writes an
    /// empty registry document rather than guessing at one - the operator's mistake to notice, not
    /// this job's to paper over.
    /// </summary>
    public string[] Providers { get; set; } = Array.Empty<string>();

    /// <summary>The tag/label filter every configured provider is run with.</summary>
    public DiscoveryFilterConfig Filter { get; set; } = new();

    /// <summary>Where the registry document is written when <see cref="ArtifactStore"/>'s <c>type</c> is <c>"file"</c>.</summary>
    public string ArtifactRootDirectory { get; set; } = "discovery-artifacts";

    /// <summary>
    /// Where the discovered registry document is written - the same backend shape as
    /// <c>MeshHostConfig.ArtifactStore</c> (a name plus a loose options dictionary), so a document
    /// this job writes to (e.g.) S3 is read from the same bucket by
    /// <c>Benzene.Mesh.Host</c>'s <c>registryDocuments</c> (work/enterprise/slice-3-discovery.md task
    /// 3.1) with no new credential path. Defaults to <c>"file"</c> (local disk, useful for local
    /// testing) - a real deployment should always set this to a durable, shared backend.
    /// </summary>
    public DiscoveryArtifactStoreConfig ArtifactStore { get; set; } = new();

    /// <summary>The relative path (within <see cref="ArtifactStore"/>) the registry document is written to.</summary>
    public string OutputPath { get; set; } = "registry.json";
}

/// <summary>The tag/label filter (<see cref="MeshDiscoveryFilter"/>) surfaced as config - task 3.3.</summary>
public class DiscoveryFilterConfig
{
    /// <summary>
    /// The tag/label key a resource must carry to be discovered (value ignored - presence only).
    /// Defaults to <see cref="MeshDiscoveryFilter.DefaultTagKey"/> (<c>"benzene"</c>).
    /// </summary>
    public string TagKey { get; set; } = MeshDiscoveryFilter.DefaultTagKey;

    /// <summary>Optional region scoping (AWS/Azure). Unset means the provider's ambient/default region(s).</summary>
    public string[]? Regions { get; set; }

    /// <summary>Optional namespace scoping (Kubernetes). Unset means the provider default (all namespaces).</summary>
    public string? Namespace { get; set; }

    /// <summary>Converts this config entry to the <see cref="MeshDiscoveryFilter"/> every provider is run with.</summary>
    public MeshDiscoveryFilter ToFilter() => new(
        tags: new Dictionary<string, string?> { [TagKey] = null },
        regions: Regions,
        @namespace: Namespace);
}

/// <summary>
/// Where the discovered registry document is written - mirrors
/// <c>deploy/Mesh/Benzene.Mesh.Host/MeshHostConfigSections.cs</c>'s <c>MeshArtifactStoreConfig</c>
/// shape exactly (a name plus a loose options dictionary), deliberately not shared code between the
/// two deployables - see <c>CLAUDE.md</c>'s "Deliberately no shared code with Benzene.Mesh.Host".
/// </summary>
public class DiscoveryArtifactStoreConfig
{
    /// <summary>Which backend to use. Defaults to <c>"file"</c> - the local filesystem, rooted at <see cref="DiscoveryHostConfig.ArtifactRootDirectory"/>.</summary>
    public string Type { get; set; } = "file";

    /// <summary>
    /// Backend-specific settings - e.g. <c>{"bucket": "...", "prefix": "..."}</c> for <c>s3</c>/<c>gcs</c>,
    /// or <c>{"blobServiceUri": "...", "container": "...", "prefix": "..."}</c> for <c>azureBlob</c>.
    /// Unused (and safely omitted) for the default <c>file</c> type.
    /// </summary>
    public Dictionary<string, string>? Options { get; set; }
}
