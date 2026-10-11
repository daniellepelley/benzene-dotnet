namespace Benzene.CloudService.Probe;

/// <summary>
/// The three verdicts an external, black-box probe can honestly reach for one requirement of the
/// Cloud Service Profile (docs/specification/cloud-service-profile.md §2). Unlike
/// <c>Benzene.CloudService.CloudServiceRequirement</c>'s simple bool (a wiring-time self-assessment
/// that trusts the service's own setup code), a probe run from outside the service genuinely cannot
/// always tell "yes" from "no" - the meshed-service claim's M1 (feeds to a collector) and M2 (trace
/// propagation) are not observable over HTTP from a single service at all. Collapsing that into a bool would silently overclaim, which
/// this codebase's "no silent caps" convention rules out. <see cref="Inconclusive"/> exists so the
/// probe can say "I don't know, and here is exactly why" instead of guessing.
/// </summary>
public enum CloudServiceProbeVerdict
{
    /// <summary>The probe independently observed evidence that the requirement holds.</summary>
    Satisfied,

    /// <summary>The probe reached the service and observed evidence that the requirement does not hold.</summary>
    NotSatisfied,

    /// <summary>
    /// The probe cannot determine this requirement from outside the service alone - either the
    /// relevant surface wasn't reachable at all (cascading from another failure), or the
    /// requirement is inherently unobservable by a single-service HTTP probe (see M1's and M2's
    /// reasons). Never treat this as a pass.
    /// </summary>
    Inconclusive
}

/// <summary>
/// One requirement - of the Cloud Service Profile (R1–R5, R7) or of the meshed-service claim
/// (mesh.md §7.1: M1, M2) - as independently assessed by an external <see cref="CloudServiceProbe"/>
/// run, not by the target service's own wiring code.
/// </summary>
public sealed class CloudServiceProbeRequirement
{
    public CloudServiceProbeRequirement(string id, string description, CloudServiceProbeVerdict verdict, string reason)
    {
        Id = id;
        Description = description;
        Verdict = verdict;
        Reason = reason;
    }

    /// <summary>The requirement id: R1–R5 or R7 for the profile, M1 or M2 for the meshed-service claim.</summary>
    public string Id { get; }

    /// <summary>What the requirement mandates, in one line.</summary>
    public string Description { get; }

    /// <summary>The probe's independently-observed verdict for this requirement.</summary>
    public CloudServiceProbeVerdict Verdict { get; }

    /// <summary>
    /// Why the verdict is what it is - always populated (unlike the self-check's optional
    /// <c>Note</c>), because an outside observer explaining its reasoning matters even more when
    /// it doesn't have the service's own word to fall back on.
    /// </summary>
    public string Reason { get; }
}

/// <summary>
/// The result of one <see cref="CloudServiceProbe"/> run against a live service: an external,
/// black-box, tri-state assessment of the Cloud Service Profile's R1–R5 and R7
/// (docs/specification/cloud-service-profile.md §2), plus - reported apart - what the probe can say of
/// the meshed-service claim's M1 and M2 (mesh.md §7.1), built entirely from what the probe itself
/// observed over real HTTP. It never trusts anything the target service claims about itself (e.g. the
/// <c>profile</c> field on its own contract document). This is the distinct, harder half of
/// conformance checking described in the profile spec's §5: the self-check
/// (<c>Benzene.CloudService.CloudServiceProfileReport</c>) answers "what did this service's own setup
/// call provision"; this report answers "what can an outside operator or CI job actually verify by
/// hitting the service".
/// </summary>
public sealed class CloudServiceProbeReport
{
    /// <summary>Creates a report.</summary>
    /// <param name="requirements">The profile's requirements (R1–R5, R7).</param>
    /// <param name="meshRequirements">The meshed-service claim's requirements (M1, M2); none when null.</param>
    public CloudServiceProbeReport(
        IReadOnlyList<CloudServiceProbeRequirement> requirements,
        IReadOnlyList<CloudServiceProbeRequirement>? meshRequirements = null)
    {
        Requirements = requirements;
        MeshRequirements = meshRequirements ?? Array.Empty<CloudServiceProbeRequirement>();
    }

    /// <summary>Every profile requirement with its independently-observed assessment: R1–R5 then R7.</summary>
    public IReadOnlyList<CloudServiceProbeRequirement> Requirements { get; }

    /// <summary>
    /// The meshed-service claim's requirements (mesh.md §7.1): M1 (registration, heartbeats, traces)
    /// then M2 (trace context propagation), as far as the probe can observe them - from a single
    /// service, neither is observable, so both are <see cref="CloudServiceProbeVerdict.Inconclusive"/>
    /// with a reason saying so. Kept apart from <see cref="Requirements"/>: they never feed
    /// <see cref="NotSatisfied"/>, <see cref="Inconclusive"/> or <see cref="IsFullyConformant"/>.
    /// </summary>
    public IReadOnlyList<CloudServiceProbeRequirement> MeshRequirements { get; }

    /// <summary>The profile ids the probe positively observed as unmet.</summary>
    public IReadOnlyList<string> NotSatisfied =>
        Requirements.Where(x => x.Verdict == CloudServiceProbeVerdict.NotSatisfied).Select(x => x.Id).ToArray();

    /// <summary>The profile ids the probe could not determine from outside the service.</summary>
    public IReadOnlyList<string> Inconclusive =>
        Requirements.Where(x => x.Verdict == CloudServiceProbeVerdict.Inconclusive).Select(x => x.Id).ToArray();

    /// <summary>
    /// True when every profile requirement (R1–R5, R7) was independently observed as
    /// <see cref="CloudServiceProbeVerdict.Satisfied"/>. Every one of them is observable over HTTP, so a
    /// conformant service probed at its default paths reaches this. The meshed-service claim
    /// (<see cref="MeshRequirements"/>) does not count: it is a separate claim, and not observable from
    /// one service.
    /// </summary>
    public bool IsFullyConformant => Requirements.All(x => x.Verdict == CloudServiceProbeVerdict.Satisfied);
}
