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
    /// <param name="failure">The failing step's result, or <c>null</c> on success.</param>
    /// <param name="failureException">The exception the failing step threw, if any.</param>
    /// <param name="compensationFailures">The outcomes of steps whose compensation itself failed during rollback.</param>
    /// <param name="failures">
    /// Every step outcome that failed within the failing stage (see <see cref="Failures"/>). Optional
    /// and additive - defaults to empty when not supplied, e.g. by a caller built against the
    /// pre-#209 constructor shape.
    /// </param>
    /// <param name="stateStoreFailure">
    /// The exception a configured <see cref="ISagaStateStore"/> call threw during this attempt, if any
    /// (see <see cref="StateStoreFailure"/>). Optional and additive - defaults to <c>null</c>.
    /// </param>
    public SagaResult(
        SagaOutcome outcome,
        int? failedStageIndex,
        IBenzeneResult? failure,
        Exception? failureException,
        IReadOnlyList<SagaStepOutcome> compensationFailures,
        IReadOnlyList<SagaStepOutcome>? failures = null,
        Exception? stateStoreFailure = null)
    {
        Outcome = outcome;
        FailedStageIndex = failedStageIndex;
        Failure = failure;
        FailureException = failureException;
        CompensationFailures = compensationFailures;
        Failures = failures ?? Array.Empty<SagaStepOutcome>();
        StateStoreFailure = stateStoreFailure;
    }

    /// <summary>Gets the overall outcome.</summary>
    public SagaOutcome Outcome { get; }

    /// <summary>Gets whether the saga completed successfully.</summary>
    public bool IsSuccess => Outcome == SagaOutcome.Succeeded;

    /// <summary>Gets the zero-based index of the stage that failed, or <c>null</c> if the saga succeeded.</summary>
    public int? FailedStageIndex { get; }

    /// <summary>Gets the failing step's result, or <c>null</c> if the saga succeeded.</summary>
    public IBenzeneResult? Failure { get; }

    /// <summary>Gets the exception the failing step threw, if it threw rather than returning a failed result.</summary>
    public Exception? FailureException { get; }

    /// <summary>
    /// Gets the outcomes of steps whose compensation itself failed during rollback - non-empty only
    /// when <see cref="Outcome"/> is <see cref="SagaOutcome.PartiallyRolledBack"/>. Their effects may
    /// still exist and need manual attention.
    /// </summary>
    public IReadOnlyList<SagaStepOutcome> CompensationFailures { get; }

    /// <summary>
    /// Gets every step outcome that failed within the failing stage - non-empty only when the saga
    /// failed (<see cref="Outcome"/> is <see cref="SagaOutcome.RolledBack"/> or
    /// <see cref="SagaOutcome.PartiallyRolledBack"/>), and containing more than one entry exactly when
    /// more than one step in that stage failed concurrently (a normal outcome - a stage's steps all
    /// run concurrently and are all awaited before the stage is judged failed). Mirrors how
    /// <see cref="CompensationFailures"/> already surfaces every relevant outcome as a list, rather
    /// than only one.
    /// </summary>
    /// <remarks>
    /// <see cref="Failure"/>/<see cref="FailureException"/> mirror this list's first item and remain
    /// populated exactly as before - kept for source/binary compatibility with code written against
    /// the single-failure shape. Prefer this list when more than one step in the same stage can fail
    /// concurrently and every failure matters, not just the first one observed.
    /// </remarks>
    public IReadOnlyList<SagaStepOutcome> Failures { get; }

    /// <summary>
    /// Gets the exception a configured <see cref="ISagaStateStore"/> call threw during this attempt
    /// (recording the start, a stage completion, or the finish), or <see langword="null"/> if no store
    /// is configured or every store call this attempt succeeded.
    /// </summary>
    /// <remarks>
    /// A populated value here does NOT mean the saga's own steps failed - <see cref="Outcome"/>,
    /// <see cref="Failures"/>, and <see cref="CompensationFailures"/> all reflect the saga's real
    /// forward/rollback progress independent of whether the store durably recorded it. A state-store
    /// failure never aborts the saga's own execution and never suppresses rollback for effects already
    /// applied - see <see cref="Saga"/>'s remarks. In particular, a <see cref="SagaOutcome.Succeeded"/>
    /// result with this populated means the saga genuinely succeeded but that outcome was not durably
    /// recorded - a caller that blindly retries on any thrown exception would otherwise have re-run an
    /// already-succeeded saga with no compensation and no dedup.
    /// </remarks>
    public Exception? StateStoreFailure { get; }
}
