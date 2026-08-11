namespace Benzene.Mesh.Host;

/// <summary>
/// Where the aggregator's generated catalog artifacts live (<c>manifest.json</c>, <c>services/*.json</c>,
/// <c>topology.json</c> and the rest of the artifact set - see <c>Benzene.Mesh.Artifacts</c>'s allow-list).
/// Every field here is mutable with a default, matching <see cref="MeshHostConfig"/>'s own binder-driven
/// style. Modelled as a name plus a loose <see cref="Dictionary{TKey,TValue}"/> of options (mirroring
/// <see cref="MeshHostServiceConfig.SourceOptions"/>'s existing precedent) rather than a typed class per
/// backend, so adding a fifth store later doesn't need a new config type - see
/// <see cref="MeshSourceRegistrar"/> for the valid <see cref="Type"/> values and what each one reads from
/// <see cref="Options"/>.
/// </summary>
public class MeshArtifactStoreConfig
{
    /// <summary>Which backend to use. Defaults to <c>"file"</c> - the local filesystem, rooted at <see cref="MeshHostConfig.ArtifactRootDirectory"/>.</summary>
    public string Type { get; set; } = "file";

    /// <summary>
    /// Backend-specific settings - e.g. <c>{"bucket": "...", "prefix": "..."}</c> for <c>s3</c>/<c>gcs</c>,
    /// or <c>{"blobServiceUri": "...", "container": "...", "prefix": "..."}</c> for <c>azureBlob</c>.
    /// Unused (and safely omitted) for the default <c>file</c> type, which reads
    /// <see cref="MeshHostConfig.ArtifactRootDirectory"/> instead.
    /// </summary>
    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>
/// One <c>usage[]</c> entry - an additional <c>IMeshUsageSource</c> the aggregator reads back into
/// <c>usage.json</c> each run. Unlike <see cref="MeshFleetConfig"/> this is an array:
/// <c>IMeshUsageSource</c> is resolved as <c>IEnumerable&lt;&gt;</c>, so several may be configured at once
/// (e.g. a CloudWatch feed and an Application Insights feed, on a deployment that spans both clouds).
/// </summary>
public class MeshUsageSourceConfig
{
    /// <summary>Which usage source to register. See <see cref="MeshSourceRegistrar"/> for the valid values.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Source-specific settings, e.g. <c>{"namespace": "...", "windowHours": "24"}</c> for <c>cloudwatch</c>.</summary>
    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>
/// The fleet (live traffic) view's data source. Deliberately an object, not an array:
/// <c>Benzene.Mesh.Collector.CompositeMeshFleetReadModel</c> takes a single <c>IMeshTraceSource</c>, not
/// an <c>IEnumerable&lt;&gt;</c>, so only one fleet source can be composed today - see
/// <c>work/enterprise/README.md</c>'s deferred-work note on widening this. Configuring more than one here
/// is a config-shape error (there is nowhere for a second one to plug in), not a supported "combine them"
/// request.
/// </summary>
public class MeshFleetConfig
{
    /// <summary>Which fleet source to register. Defaults to <c>"none"</c> - no live Fleet plane. See <see cref="MeshSourceRegistrar"/> for the valid values.</summary>
    public string Source { get; set; } = "none";

    /// <summary>Source-specific settings, e.g. <c>{"url": "...", "correlationLookbackHours": "24"}</c>. Unused for <c>none</c>.</summary>
    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>The service-graph topology view's data source (the tempo-sourced edges in <c>topology.json</c>, alongside the structural edges the aggregator always derives).</summary>
public class MeshTopologyConfig
{
    /// <summary>Which topology source to register. Defaults to <c>"none"</c>. See <see cref="MeshSourceRegistrar"/> for the valid values.</summary>
    public string Source { get; set; } = "none";

    /// <summary>Source-specific settings, e.g. <c>{"prometheusUrl": "...", "windowMinutes": "5"}</c>. Unused for <c>none</c>.</summary>
    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>
/// Opt-in live dispatch (the <c>mesh:dispatch</c> handler that invokes a registered service's REAL
/// handler with a chosen payload). Off by default: it fires real side-effects (DB writes, downstream
/// calls, the handler's own publishes), so it is a deliberate, non-default choice.
/// </summary>
public class MeshDispatchConfig
{
    /// <summary>Whether dispatch is wired at all. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether dispatch (when <see cref="Enabled"/>) is also permitted in a Production environment. Off
    /// by default - dispatch runs real handlers, so Production requires this explicit second opt-in (an
    /// unset environment counts as Production).
    /// </summary>
    public bool AllowInProduction { get; set; }
}

/// <summary>
/// Reserved for slice 2 (auth in the host) - this slice binds the key but acts on nothing here; every
/// mode but <c>"none"</c> is unimplemented until slice 2 lands. Defaults to <c>"none"</c>, today's only
/// behaviour: the host requires no authentication.
/// </summary>
public class MeshAuthConfig
{
    /// <summary>The auth mode. Only <c>"none"</c> is implemented by this slice.</summary>
    public string Mode { get; set; } = "none";
}
