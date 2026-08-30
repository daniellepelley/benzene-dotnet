using Benzene.Abstractions.Results;

namespace Benzene.Saga;

/// <summary>
/// The outcome of running a <see cref="Saga"/>: whether it succeeded, and if not, which stage
/// failed, why, and whether rollback was clean.
/// </summary>
public class SagaResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SagaResult"/> class.
    /// </summary>
    /// <param name="outcome">The overall outcome.</param>
    /// <param name="failedStageIndex">The zero-based index of the stage that failed, or <c>null</c> on success.</param>
    /// <param name="failures">
    /// Every failed step's outcome in the failing stage (see <see cref="Failures"/>). Empty on
    /// success and also empty when the rollback was triggered by a state-store failure rather than a
    /// step failure - see <paramref name="stateStoreException"/>.
    /// </param>
    /// <param name="compensationFailures">The outcomes of steps whose compensation itself failed during rollback.</param>
    /// <param name="stateStoreException">
    /// The exception an <see cref="ISagaStateStore"/> threw, when that persistence failure - not a
    /// step failure - is what triggered this rollback. <c>null</c> otherwise.
    /// </param>
    public SagaResult(SagaOutcome outcome, int? failedStageIndex, IReadOnlyList<SagaStepOutcome> failures,
        IReadOnlyList<SagaStepOutcome> compensationFailures, Exception? stateStoreException = null)
    {
        Outcome = outcome;
        FailedStageIndex = failedStageIndex;
        Failures = failures;
        CompensationFailures = compensationFailures;
        StateStoreException = stateStoreException;
    }

    /// <summary>Gets the overall outcome.</summary>
    public SagaOutcome Outcome { get; }

    /// <summary>Gets whether the saga completed successfully.</summary>
    public bool IsSuccess => Outcome == SagaOutcome.Succeeded;

    /// <summary>Gets the zero-based index of the stage that failed, or <c>null</c> if the saga succeeded.</summary>
    public int? FailedStageIndex { get; }

    /// <summary>
    /// Gets every failed step's outcome in the failing stage. Concurrent steps within one stage can
    /// fail together (e.g. two downstream calls both timing out) - every one of them is represented
    /// here, not just the first. Empty on success, and also empty when the rollback was triggered by
    /// a state-store failure rather than a step failure (see <see cref="StateStoreException"/> for
    /// that case).
    /// </summary>
    public IReadOnlyList<SagaStepOutcome> Failures { get; }

    /// <summary>
    /// Gets the first failed step's result, or <c>null</c> if the saga succeeded or the rollback was
    /// triggered by a state-store failure rather than a step failure. A convenience view over the
    /// first entry of <see cref="Failures"/>, kept for backward compatibility and the common case of
    /// a single failing step - mirrors how <see cref="CompensationFailures"/> is the full list.
    /// Inspect <see cref="Failures"/> directly when more than one step in the failing stage can fail.
    /// </summary>
    public IBenzeneResult? Failure => Failures.Count > 0 ? Failures[0].Result : null;

    /// <summary>
    /// Gets the exception the first failed step threw, if it threw rather than returning a failed
    /// result. A convenience view over the first entry of <see cref="Failures"/> - see
    /// <see cref="Failure"/>. <c>null</c> if the saga succeeded, no failed step threw, or the
    /// rollback was triggered by a state-store failure instead (see <see cref="StateStoreException"/>).
    /// </summary>
    public Exception? FailureException => Failures.Count > 0 ? Failures[0].Exception : null;

    /// <summary>
    /// Gets the outcomes of steps whose compensation itself failed during rollback - non-empty only
    /// when <see cref="Outcome"/> is <see cref="SagaOutcome.PartiallyRolledBack"/>. Their effects may
    /// still exist and need manual attention.
    /// </summary>
    public IReadOnlyList<SagaStepOutcome> CompensationFailures { get; }

    /// <summary>
    /// Gets the exception an <see cref="ISagaStateStore"/> threw when persisting saga progress, for a
    /// rollback that a state-store failure triggered rather than a step failure. <c>null</c> for
    /// every other outcome, including a rollback triggered by an ordinary step failure (see
    /// <see cref="Failures"/> for that case instead).
    /// </summary>
    public Exception? StateStoreException { get; }
}
