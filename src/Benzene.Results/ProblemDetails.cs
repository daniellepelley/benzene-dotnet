using System.Text.Json.Serialization;
using Benzene.Abstractions.Results;

namespace Benzene.Results;

/// <summary>
/// An RFC 9457 ("Problem Details for HTTP APIs") problem document - the wire body Benzene emits in
/// place of the payload whenever a result is unsuccessful, on every transport (see
/// <c>docs/specification/wire-contracts.md</c> §1.3/§3.1 in the cross-language Benzene spec repo).
/// Build one from a result with <see cref="ProblemTypes.From"/> rather than constructing it by hand.
/// </summary>
/// <remarks>
/// <para>
/// Every member is optional per RFC 9457 (even <c>type</c>, which defaults to <c>about:blank</c> when
/// absent) and is annotated <c>[JsonIgnore(Condition = WhenWritingNull)]</c> so a member that doesn't
/// apply is omitted from the wire rather than emitted as JSON <c>null</c> - load-bearing for
/// <see cref="Status"/> specifically, since a problem document produced off an HTTP transport (an
/// envelope over a queue, direct invocation) MUST NOT carry a fabricated HTTP status number; a future
/// conformance fixture pins that omission via a <c>bodyExclude</c> assertion.
/// </para>
/// <para>
/// A plain settable-property class with a public parameterless constructor - not a positional record
/// - so it round-trips through every serializer Benzene ships (the same "five-serializer
/// compatibility" shape used elsewhere in this package). Deliberately has no
/// <c>[JsonExtensionData]</c> bag: an application that needs its own extension members should
/// subclass <see cref="ProblemDetails"/> (the pattern the retired <c>ErrorPayload</c> established),
/// which still round-trips through every serializer; a typed extensions dictionary is parked.
/// </para>
/// </remarks>
public class ProblemDetails
{
    /// <summary>
    /// A URI reference identifying the problem type. Framework-produced problems use the registry
    /// URI for the result's status (see <see cref="ProblemTypes"/>); application-authored problems
    /// should use their own absolute URI. Readers must treat this as an opaque identifier - compare
    /// by string equality, never dereference.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>
    /// A short, human-readable summary of the problem type, fixed per <see cref="Type"/> for
    /// framework-produced problems (the registry value). Never asserted for exact wording by
    /// conformance fixtures.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>
    /// The HTTP response status code, for HTTP bindings only. Must equal the actual HTTP response
    /// status when present, and must be omitted (not <c>null</c> on the wire - see the type-level
    /// remarks) where no HTTP response exists, e.g. a BenzeneMessage envelope over a queue. Benzene
    /// clients must not classify success/failure from this member - use the envelope/result status
    /// instead.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Status { get; set; }

    /// <summary>
    /// A human-readable explanation specific to this occurrence of the problem - the result's error
    /// messages joined with <c>", "</c>. The compatibility member: every existing reader of the old
    /// <c>ErrorPayload</c> shape used this member and keeps working unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    /// <summary>
    /// A URI reference identifying this specific occurrence of the problem. Optional and
    /// application-owned - the framework never fabricates a value for this member.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instance { get; set; }

    /// <summary>
    /// The Benzene result status string (<c>docs/specification/wire-contracts.md</c> §3), mirroring
    /// the envelope's <c>statusCode</c> - the transport-neutral discriminator every reader should
    /// classify on, since <see cref="Status"/> only exists on HTTP bindings. Framework-produced
    /// problem documents always set this member; it is nullable here only so an application building
    /// a <see cref="ProblemDetails"/> by hand isn't forced to.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BenzeneStatus { get; set; }

    /// <summary>
    /// The result's structured errors, when present - authoritative and ordered. <c>null</c> (and
    /// omitted from the wire) when the result carries no structured errors; readers without this
    /// member should treat <see cref="Detail"/> as a single opaque message.
    /// </summary>
    /// <remarks>
    /// Declared as an array, not <see cref="IBenzeneResult.Errors"/>'s own <c>IReadOnlyList&lt;BenzeneError&gt;</c>
    /// - <c>System.Xml.Serialization.XmlSerializer</c> (<c>Benzene.Xml</c>, one of the five serializers
    /// this type must round-trip through) refuses to reflect a member whose declared type is an
    /// interface ("Cannot serialize member ... because it is an interface"), so the wire-facing
    /// property has to be a concrete collection type even though the result-model member it's built
    /// from is an interface. <see cref="ProblemTypes.From"/> projects with <c>.ToArray()</c>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenzeneError[]? Errors { get; set; }
}
