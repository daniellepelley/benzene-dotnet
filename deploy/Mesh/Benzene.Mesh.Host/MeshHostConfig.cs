using Benzene.Mesh.Contracts;

namespace Benzene.Mesh.Host;

/// <summary>
/// The config-bound shape of <c>mesh.json</c> (or equivalent environment variables, via .NET's
/// standard double-underscore binding, e.g. <c>Services__0__Name</c>) - this repo's first use of
/// <c>IConfiguration.Get&lt;T&gt;()</c> binding a list of objects, flagged in
/// <c>work/service-mesh-roadmap-1.0.md</c> as genuinely new territory, not an established Benzene
/// convention being reused. Mutable properties (not the constructor-based immutable pattern the
/// rest of <c>Benzene.Mesh.Contracts</c> uses) are required for the configuration binder.
/// </summary>
public class MeshHostConfig
{
    /// <summary>Where generated catalog artifacts are written - bind-mount a volume here for persistence across container restarts.</summary>
    public string ArtifactRootDirectory { get; set; } = "mesh-artifacts";

    /// <summary>How often the background poll loop runs a full aggregation pass.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>The services to poll each pass.</summary>
    public MeshHostServiceConfig[] Services { get; set; } = Array.Empty<MeshHostServiceConfig>();

    /// <summary>
    /// Locations of discovery-generated registry documents (see
    /// <c>work/enterprise/slice-3-discovery.md</c>) - each a relative path resolved through
    /// <see cref="ArtifactStore"/> and read with
    /// <c>Benzene.Mesh.Aggregator.IMeshArtifactStore.TryReadAsync</c>, so a document a separate
    /// discovery job wrote (e.g. to S3) is read back from that same store, with no new credential
    /// path. Read once at startup and unioned with <see cref="Services"/> - <see cref="Services"/>
    /// always wins a name clash, so an operator can always override a discovered entry by naming it
    /// explicitly ("discovery proposes, config disposes"). Empty by default: no documents, today's
    /// behaviour (services alone) unchanged.
    /// </summary>
    public string[] RegistryDocuments { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Where the aggregator's generated catalog artifacts live. Defaults to the local filesystem
    /// (<see cref="ArtifactRootDirectory"/>) - see <see cref="MeshSourceRegistrar"/> for the other
    /// backends this can select and what each one reads from <see cref="MeshArtifactStoreConfig.Options"/>.
    /// </summary>
    public MeshArtifactStoreConfig ArtifactStore { get; set; } = new();

    /// <summary>
    /// Additional usage (per-topic traffic) sources the aggregator reads back into <c>usage.json</c>
    /// each run - zero or more, since <c>IMeshUsageSource</c> is resolved as <c>IEnumerable&lt;&gt;</c>.
    /// Empty by default (no usage feed) - see <see cref="MeshSourceRegistrar"/> for the valid values.
    /// </summary>
    public MeshUsageSourceConfig[] Usage { get; set; } = Array.Empty<MeshUsageSourceConfig>();

    /// <summary>
    /// The fleet (live traffic) view's data source. An object, not an array - see
    /// <see cref="MeshFleetConfig"/> for why only one may be configured. Defaults to <c>"none"</c>: no
    /// live Fleet plane, the dashboard shows only the declared catalog.
    /// </summary>
    public MeshFleetConfig Fleet { get; set; } = new();

    /// <summary>The service-graph topology view's data source. Defaults to <c>"none"</c> (no <c>topology.json</c> beyond the structural edges the aggregator itself derives).</summary>
    public MeshTopologyConfig Topology { get; set; } = new();

    /// <summary>
    /// Opt in to the live dispatch feature (the <c>mesh:dispatch</c> handler that invokes a registered
    /// service's REAL handler with a chosen payload). Off by default: it fires real side-effects, so it
    /// is a deliberate, non-default choice.
    /// </summary>
    public MeshDispatchConfig Dispatch { get; set; } = new();

    /// <summary>
    /// Auth in the host (work/enterprise/slice-2-auth.md) - see <see cref="MeshAuthConfig"/> and
    /// <see cref="MeshAuthGate"/>. Defaults to <c>"none"</c>, today's only pre-slice-2 behaviour: the
    /// host requires no authentication.
    /// </summary>
    public MeshAuthConfig Auth { get; set; } = new();
}

/// <summary>One <c>mesh.json</c> service entry, converted to a <see cref="MeshServiceRegistryEntry"/> via <see cref="ToEntry"/>.</summary>
public class MeshHostServiceConfig
{
    /// <summary>The service's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The service's spec URL - required for <see cref="MeshServiceSource.Http"/>, optional (display-only) for other sources.</summary>
    public string? SpecUrl { get; set; }

    /// <summary>The service's health URL - required for <see cref="MeshServiceSource.Http"/>, optional (display-only) for other sources.</summary>
    public string? HealthUrl { get; set; }

    /// <summary>Which <c>IMeshServiceSource</c> fetches this entry - see <see cref="MeshServiceSource"/>. Defaults to <see cref="MeshServiceSource.Http"/>.</summary>
    public string Source { get; set; } = MeshServiceSource.Http;

    /// <summary>Source-specific configuration (e.g. <c>{"functionName": "...", "region": "..."}"</c> for <see cref="MeshServiceSource.AwsLambdaInvoke"/>).</summary>
    public Dictionary<string, string>? SourceOptions { get; set; }

    /// <summary>The team or individual to contact about this service, if known - see <see cref="MeshServiceRegistryEntry.OwningTeam"/>.</summary>
    public string? OwningTeam { get; set; }

    /// <summary>Converts this config entry to the registry shape <see cref="Benzene.Mesh.Aggregator.MeshAggregator"/> consumes.</summary>
    public MeshServiceRegistryEntry ToEntry() => new(Name, SpecUrl ?? string.Empty, HealthUrl ?? string.Empty, Source, SourceOptions, OwningTeam);
}
