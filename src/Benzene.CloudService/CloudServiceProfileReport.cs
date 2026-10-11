using Benzene.Mesh.Wire;
using Benzene.Schema.OpenApi.EventService;

namespace Benzene.CloudService;

/// <summary>
/// One requirement as self-assessed at wire-up: either a Cloud Service Profile requirement
/// (docs/specification/cloud-service-profile.md §2 - R1–R5, R7) or a requirement of the meshed-service
/// claim (mesh.md §7.1 - M1, M2).
/// </summary>
public sealed class CloudServiceRequirement
{
    public CloudServiceRequirement(string id, string description, bool satisfied, string? note = null)
    {
        Id = id;
        Description = description;
        Satisfied = satisfied;
        Note = note;
    }

    /// <summary>The requirement id: R1–R5 or R7 for the profile, M1 or M2 for the meshed-service claim.</summary>
    public string Id { get; }

    /// <summary>What the requirement mandates, in one line.</summary>
    public string Description { get; }

    /// <summary>Whether this service's wiring satisfies the requirement.</summary>
    public bool Satisfied { get; }

    /// <summary>Why the requirement is (or isn't) satisfied, when that needs saying.</summary>
    public string? Note { get; }
}

/// <summary>
/// The wiring-time self-assessment of a service against the Cloud Service Profile
/// (docs/specification/cloud-service-profile.md) and, separately, the meshed-service claim
/// (mesh.md §7.1). Produced by <c>UseBenzeneCloudService</c> from what was actually wired - defaults
/// kept, surfaces declined, paths relocated.
///
/// <para>
/// The <b>profile</b> (<see cref="Requirements"/>: R1–R5, R7) is what any tool can pull from the
/// service itself, with no collector. Its result is carried on the service's contract document as
/// contract-document.md §1's <c>profile</c> field - <c>{"name": "cloud-service", "missing": [...]}</c>,
/// the document <c>benzene:spec</c> serves and the service registers with a collector - so any tool
/// that can reach <c>benzene:spec</c> can ask a running service whether it claims the profile.
/// </para>
/// <para>
/// The <b>meshed-service claim</b> (<see cref="MeshRequirements"/>: M1 feeds, M2 trace propagation -
/// formerly the profile's R6 and R8, whose ids are not reused) needs a collector, so it is reported
/// apart: M1 and M2 never appear in <see cref="Missing"/> or on the contract document's
/// <c>profile.missing</c>.
/// </para>
/// <para>
/// This is a self-assessment of provisioning, not a runtime probe: it reflects what the builder
/// wired, and runtime degradation (an unreachable collector, a failing check) never changes it -
/// per the profile's §4, runtime degradation is not a conformance failure.
/// </para>
/// </summary>
public sealed class CloudServiceProfileReport
{
    /// <summary>The profile name carried on the wire (contract-document.md §1 <c>profile.name</c>).</summary>
    public const string ProfileName = "cloud-service";

    private CloudServiceProfileReport(
        IReadOnlyList<CloudServiceRequirement> requirements,
        IReadOnlyList<CloudServiceRequirement> meshRequirements)
    {
        Requirements = requirements;
        MeshRequirements = meshRequirements;
    }

    /// <summary>Every Cloud Service Profile requirement with its assessment: R1–R5 then R7.</summary>
    public IReadOnlyList<CloudServiceRequirement> Requirements { get; }

    /// <summary>
    /// The ids of the profile requirements this service's wiring does not satisfy (from R1–R5, R7 only);
    /// empty when conformant. This is the contract document's <c>profile.missing</c>.
    /// </summary>
    public IReadOnlyList<string> Missing => Requirements.Where(x => !x.Satisfied).Select(x => x.Id).ToArray();

    /// <summary>True when every profile requirement is satisfied - the service claims the Cloud Service Profile.</summary>
    public bool IsConformant => Requirements.All(x => x.Satisfied);

    /// <summary>
    /// The meshed-service claim's own requirements (mesh.md §7.1): M1 (registration, heartbeats and
    /// traces) then M2 (W3C trace context joined inbound and forwarded outbound). Reported separately
    /// from the profile, never in <see cref="Missing"/>.
    /// </summary>
    public IReadOnlyList<CloudServiceRequirement> MeshRequirements { get; }

    /// <summary>The ids of the meshed-service requirements (M1, M2) this service's wiring does not satisfy.</summary>
    public IReadOnlyList<string> MeshMissing => MeshRequirements.Where(x => !x.Satisfied).Select(x => x.Id).ToArray();

    /// <summary>
    /// True when the service claims to be meshed (mesh.md §7.1): it is a Cloud Service
    /// (<see cref="IsConformant"/>) and its wiring satisfies M1 and M2.
    /// </summary>
    public bool IsMeshed => IsConformant && MeshRequirements.All(x => x.Satisfied);

    /// <summary>The contract document's <c>profile</c> field (contract-document.md §1): the profile's missing ids only.</summary>
    public ContractDocumentProfile ToProfile() => new(ProfileName, Missing);

    /// <summary>
    /// Evaluates the profile and the meshed-service claim. <paramref name="traceExporter"/> must be the
    /// exact value <c>UseBenzeneCloudService</c> resolves and wires <c>UseMeshTrace</c> with (or null when
    /// it wires nothing) - M2 reads that same value rather than re-deriving mesh/collector wiring itself,
    /// so the report can never claim trace propagation the pipeline didn't actually wire (#167; see
    /// Extensions.cs, where the two share this one resolved value).
    /// </summary>
    /// <summary>
    /// The known .NET M2 gap, carried on M2's note when the inbound join is wired: the self-check sees the
    /// inbound half only. Outbound forwarding is the outbound route's <c>UseW3CTraceContext()</c> - sends and
    /// published events alike, since <c>Benzene.ResponseEvents</c> publishes through the same
    /// <c>IBenzeneMessageSender</c> routes - which the cloud service does not own and stamps from
    /// <c>Activity.Current</c>, not from the mesh span (<c>MeshSpan.Current</c>).
    /// </summary>
    internal const string OutboundPropagationGap =
        "inbound traceparent is joined (UseMeshTrace); outbound forwarding - on sends and published events alike, " +
        "which share the IBenzeneMessageSender routes - is each outbound route's UseW3CTraceContext(), which this " +
        "self-check cannot see and which stamps Activity.Current rather than the mesh span (MeshSpan.Current): " +
        "wire it on every route, or forward MeshSpan.Current.ToTraceparent() yourself";

    internal static CloudServiceProfileReport Evaluate(CloudServiceBuilder builder, IMeshTraceExporter? traceExporter)
    {
        var mesh = builder.MeshEnabled;
        var collector = builder.CollectorEnvelopeUrl != null;

        var profile = new[]
        {
            new CloudServiceRequirement("R1", "Hosted middleware pipeline", true,
                "wired by UseBenzeneCloudService on a hosted HTTP pipeline"),
            new CloudServiceRequirement("R2", "Message handlers via the registry", true,
                builder.HandlerTypes != null
                    ? $"{builder.HandlerTypes.Length} handler type(s) registered explicitly"
                    : "handlers taken from the container's registrations; the contract document's requests are the runtime truth"),
            new CloudServiceRequirement("R3", "Health checks (reserved topic + HTTP surface)", true,
                builder.HealthChecks.Count == 0 ? "no checks added; the aggregate reports healthy" : null),
            new CloudServiceRequirement("R4", "Wire-envelope invocability", true),
            new CloudServiceRequirement("R5", "Derived spec: the benzene:spec topic wherever the transport carries a reply, and the HTTP spec surface", true,
                $"the derived contract document answers benzene:spec through the handler registry - on the envelope ({builder.InvokePath}) and HTTP pipelines, and on any other reply-carrying transport routed through the same registry (a one-way binding cannot answer and is not required to) - and is served at {builder.SpecPath}"),
            new CloudServiceRequirement("R7", "Default service standard paths", builder.UsesDefaultPaths,
                builder.UsesDefaultPaths ? null : "one or more surfaces relocated from their /benzene/ defaults; fleet tooling assumes the defaults")
        };

        var meshed = new[]
        {
            new CloudServiceRequirement("M1", "Service-side mesh feeds (registration, heartbeats, traces)", mesh && collector,
                !mesh ? "mesh declined via WithoutMesh(): no registration, heartbeats or traces"
                    : !collector ? "no collector configured (WithCollector): the registration, heartbeat and trace feeds have no destination"
                    : null),
            new CloudServiceRequirement("M2", "Trace context join and propagation (inbound join; traceparent forwarded on every outbound send and published event)", mesh && traceExporter != null,
                !mesh ? "mesh declined via WithoutMesh(): the trace middleware that joins traceparent is not wired"
                    : traceExporter == null ? "no trace exporter configured (WithTraceExporter) and no collector URL: UseMeshTrace is not wired"
                    : OutboundPropagationGap)
        };

        return new CloudServiceProfileReport(profile, meshed);
    }
}
