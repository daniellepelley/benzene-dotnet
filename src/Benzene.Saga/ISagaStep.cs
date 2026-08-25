namespace Benzene.Saga;

/// <summary>
/// A single unit of work in a saga: a forward action paired with an optional compensation that
/// undoes it. Steps are grouped into stages (run concurrently) which are grouped into a
/// <see cref="Saga"/> (run in order). The non-generic surface is what the engine operates on; the
/// result type is captured by <see cref="SagaStep{T}"/>.
/// </summary>
/// <remarks>
/// An <see cref="ISagaStep"/> is an immutable descriptor once built - it carries no per-execution
/// outcome (state/result/exception) of its own. Every method here returns a fresh
/// <see cref="SagaStepOutcome"/> instead of mutating the step, so the same step instance is safe to
/// run concurrently as part of multiple simultaneous <see cref="Saga.RunAsync()"/> calls - see the
/// "immutable and concurrency-safe" contract on <see cref="Saga"/>.
/// </remarks>
public interface ISagaStep
{
    /// <summary>
    /// Runs the forward action, reading any earlier-stage values it needs from <paramref name="context"/>.
    /// Does not publish its own result to the context - that happens via <see cref="Publish"/> once the
    /// whole stage succeeds.
    /// </summary>
    /// <param name="context">The saga context.</param>
    /// <returns>This run's outcome for the step.</returns>
    Task<SagaStepOutcome> ExecuteAsync(SagaContext context);

    /// <summary>
    /// Publishes this step's successful result into <paramref name="context"/> so later stages can
    /// read it. Called only after the step's stage has fully succeeded.
    /// </summary>
    /// <param name="context">The saga context.</param>
    /// <param name="outcome">This run's outcome for the step, from <see cref="ExecuteAsync"/>.</param>
    void Publish(SagaContext context, SagaStepOutcome outcome);

    /// <summary>
    /// Compensates this step during rollback. A no-op (the outcome is returned unchanged) if the step
    /// did not succeed; a succeeded step with no compensation is reported <see cref="SagaStepState.RolledBack"/>.
    /// </summary>
    /// <param name="context">The saga context.</param>
    /// <param name="outcome">This run's outcome for the step, from <see cref="ExecuteAsync"/>.</param>
    /// <returns>The outcome updated to reflect the compensation's result.</returns>
    Task<SagaStepOutcome> CompensateAsync(SagaContext context, SagaStepOutcome outcome);
}
