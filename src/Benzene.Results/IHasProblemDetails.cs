namespace Benzene.Results;

/// <summary>
/// A capability interface a concrete <see cref="Abstractions.Results.IBenzeneResult"/>/
/// <c>IBenzeneResult&lt;T&gt;</c> implementation can carry a deliberately-attached
/// <see cref="ProblemDetails"/> document on - deliberately NOT a member of <c>IBenzeneResult</c>
/// itself (that interface is frozen; see <c>work/benzene-result-errors-ruling.md</c> R7 and
/// <c>work/archive/problem-details-plan-2026-08.md</c> decision 3). Two producers attach a document through this
/// capability:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>a handler authoring a rich, deliberate problem via <see cref="BenzeneResult.Problem{T}"/>
/// (custom <see cref="ProblemDetails.Type"/>/<see cref="ProblemDetails.Instance"/>, or an
/// application subclass of <see cref="ProblemDetails"/> carrying extension members);</item>
/// <item>a Benzene client that deserialized a problem document off the wire on a failure response
/// (<c>Benzene.Clients.Common.ClientResultExtensions</c>), so the caller can recover exactly what
/// was received.</item>
/// </list>
/// <para>
/// Read this via the <see cref="BenzeneResultExtensions.GetProblem"/> extension, not by casting
/// directly - it also covers the (far more common) case of a result with no attached document by
/// synthesizing one, so callers never have to null-check.
/// </para>
/// </remarks>
public interface IHasProblemDetails
{
    /// <summary>
    /// The problem document attached to this result, or <c>null</c> when none was deliberately
    /// attached (the overwhelming majority of results - use <see cref="BenzeneResultExtensions.GetProblem"/>
    /// rather than reading this directly if a non-null document is what you actually want).
    /// </summary>
    ProblemDetails? Problem { get; }
}
