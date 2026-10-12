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

    /// <summary>M2's note when the inbound join is wired and every outbound route forwards the mesh span.</summary>
    internal const string OutboundForwardingWired =
        "inbound traceparent joined (UseMeshTrace); the mesh span (MeshSpan.Current, the span each TraceEvent records) " +
        "is forwarded as traceparent/tracestate on every outbound route - sends and published events alike, which share " +
        "the IBenzeneMessageSender routes - by OutboundRoutingBuilder.UseMeshTraceContext(); a raw IBenzeneMessageClient " +
        "call outside the routes must set it itself (MeshSpan.Current.ToTraceparent())";

    /// <summary>M2's note when outbound routes are registered but do not all forward the mesh span.</summary>
    internal const string OutboundForwardingMissing =
        "inbound traceparent is joined (UseMeshTrace), but the outbound routes do not forward the mesh span: call " +
        "UseMeshTraceContext() on the OutboundRoutingBuilder (AddOutboundRouting(routing => routing.UseMeshTraceContext()...)) " +
        "so every send and published event carries the span each TraceEvent records. UseW3CTraceContext() forwards " +
        "OpenTelemetry's Activity.Current, a different span, and a per-route UseMeshTraceContext() is not visible to this self-check";

    /// <summary>M2's note when the inbound join is wired and the container had no outbound routes when the service was wired.</summary>
    internal const string NoOutboundRoutes =
        "inbound traceparent joined (UseMeshTrace); no outbound routes were registered when the cloud service was wired, so " +
        "there is nothing to forward - register routes before UseBenzeneCloudService, with " +
        "OutboundRoutingBuilder.UseMeshTraceContext(), so this self-check sees them";

    /// <summary>
    /// Evaluates the profile and the meshed-service claim. <paramref name="traceExporter"/> must be the
    /// exact value <c>UseBenzeneCloudService</c> resolves and wires <c>UseMeshTrace</c> with (or null when
    /// it wires nothing) - M2 reads that same value rather than re-deriving mesh/collector wiring itself,
    /// so the report can never claim trace propagation the pipeline didn't actually wire (#167; see
    /// Extensions.cs, where the two share this one resolved value).
    /// </summary>
    internal static CloudServiceProfileReport Evaluate(CloudServiceBuilder builder, IMeshTraceExporter? traceExporter,
        OutboundTraceForwarding outbound = OutboundTraceForwarding.NoRoutes)
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
            new CloudServiceRequirement("M2", "Trace context join and propagation (inbound join; traceparent forwarded on every outbound send and published event)",
                mesh && traceExporter != null && outbound != OutboundTraceForwarding.RoutesWithoutMeshForwarding,
                !mesh ? "mesh declined via WithoutMesh(): the trace middleware that joins traceparent is not wired"
                    : traceExporter == null ? "no trace exporter configured (WithTraceExporter) and no collector URL: UseMeshTrace is not wired"
                    : outbound switch
                    {
                        OutboundTraceForwarding.MeshForwardingOnEveryRoute => OutboundForwardingWired,
                        OutboundTraceForwarding.RoutesWithoutMeshForwarding => OutboundForwardingMissing,
                        _ => NoOutboundRoutes
                    })
        };

        return new CloudServiceProfileReport(profile, meshed);
    }
}

/// <summary>What the container's outbound routing forwards, as the M2 self-check sees it when the service is wired.</summary>
internal enum OutboundTraceForwarding
{
    /// <summary>No outbound routes are registered: nothing to forward.</summary>
    NoRoutes,

    /// <summary>Outbound routes are registered without the routing-wide <c>UseMeshTraceContext()</c>.</summary>
    RoutesWithoutMeshForwarding,

    /// <summary><c>OutboundRoutingBuilder.UseMeshTraceContext()</c>: every route forwards the mesh span.</summary>
    MeshForwardingOnEveryRoute
}
