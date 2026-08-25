using Benzene.Abstractions.Results;

namespace Benzene.Saga;

/// <summary>
/// An immutable, per-run snapshot of one step's outcome for a single execution of a
/// <see cref="Saga"/>: its <see cref="State"/>, its forward/compensation <see cref="Result"/>, and (if
/// it threw) the <see cref="Exception"/>.
/// </summary>
/// <remarks>
/// A built <see cref="Saga"/>'s steps and stages are immutable descriptors - see the "immutable and
/// concurrency-safe" contract on <see cref="Saga"/>. No execution outcome is ever stored back onto a
/// step or stage instance; instead, each <see cref="ISagaStep.ExecuteAsync"/>/
/// <see cref="ISagaStep.CompensateAsync"/> call returns a fresh <see cref="SagaStepOutcome"/>, and the
/// engine threads these through a run-scoped list/array (keyed by step index) that lives only for the
/// duration of one <c>RunAsync</c> call. This is what makes concurrent <c>RunAsync</c> calls on the
/// same built <see cref="Saga"/> safe: there is no shared mutable state for them to race over.
/// </remarks>
public sealed class SagaStepOutcome
{
    /// <summary>Initializes a new instance of the <see cref="SagaStepOutcome"/> class.</summary>
    /// <param name="step">The step descriptor this outcome belongs to.</param>
    /// <param name="state">This run's lifecycle state for the step.</param>
    /// <param name="result">The forward (or compensation) action's result, or <c>null</c> before the step has run.</param>
    /// <param name="exception">The exception the step threw during this run, if any; otherwise <c>null</c>.</param>
    public SagaStepOutcome(ISagaStep step, SagaStepState state, IBenzeneResult? result, Exception? exception)
    {
        Step = step;
        State = state;
        Result = result;
        Exception = exception;
    }

    /// <summary>Gets the step descriptor this outcome belongs to.</summary>
    public ISagaStep Step { get; }

    /// <summary>Gets this run's lifecycle state for the step.</summary>
    public SagaStepState State { get; }

    /// <summary>Gets the forward (or compensation) action's result for this run, or <c>null</c> before the step has run.</summary>
    public IBenzeneResult? Result { get; }

    /// <summary>Gets the exception the step threw during this run, if any; otherwise <c>null</c>.</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Returns a copy of this outcome with a different <see cref="State"/> (and, optionally, a
    /// different <see cref="Exception"/>) - used to record a compensation's outcome without mutating
    /// the original (forward) outcome in place.
    /// </summary>
    internal SagaStepOutcome WithState(SagaStepState state, Exception? exception = null)
        => new(Step, state, Result, exception);
}
