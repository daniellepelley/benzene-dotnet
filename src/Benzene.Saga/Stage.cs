namespace Benzene.Saga;

/// <summary>
/// A group of <see cref="ISagaStep"/>s that run concurrently as one all-or-nothing unit within a
/// <see cref="Saga"/>. The stage succeeds only if every step succeeds.
/// </summary>
/// <remarks>
/// Immutable once constructed - it holds only its <see cref="Steps"/> list, never a per-execution
/// outcome. Every run's per-step outcomes are threaded through as a run-scoped
/// <see cref="IReadOnlyList{SagaStepOutcome}"/> (indexed the same as <see cref="Steps"/>), created fresh
/// by the caller for each <c>RunAsync</c> call - see the "immutable and concurrency-safe" contract on
/// <see cref="Saga"/>.
/// </remarks>
public class Stage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Stage"/> class.
    /// </summary>
    /// <param name="steps">The steps that make up this stage.</param>
    public Stage(IReadOnlyList<ISagaStep> steps)
    {
        Steps = steps;
    }

    /// <summary>Gets the steps in this stage.</summary>
    public IReadOnlyList<ISagaStep> Steps { get; }

    /// <summary>
    /// Runs every step's forward action concurrently (awaiting them all, even if one fails early).
    /// </summary>
    /// <param name="context">The saga context.</param>
    /// <returns>Each step's outcome for this run, in the same order as <see cref="Steps"/>. The stage succeeded iff every outcome's state is <see cref="SagaStepState.Succeeded"/>.</returns>
    public async Task<IReadOnlyList<SagaStepOutcome>> ExecuteAsync(SagaContext context)
    {
        return await Task.WhenAll(Steps.Select(step => step.ExecuteAsync(context)));
    }

    /// <summary>Publishes every succeeded step's result into the context. Call only after the stage fully succeeds.</summary>
    /// <param name="context">The saga context.</param>
    /// <param name="outcomes">This run's outcomes for <see cref="Steps"/>, from <see cref="ExecuteAsync"/> (same order/length).</param>
    public void Publish(SagaContext context, IReadOnlyList<SagaStepOutcome> outcomes)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Publish(context, outcomes[i]);
        }
    }

    /// <summary>
    /// Compensates this stage's steps concurrently (best effort - every compensation is attempted
    /// regardless of whether an earlier one failed). A step whose outcome was not
    /// <see cref="SagaStepState.Succeeded"/> is a no-op (its outcome is returned unchanged).
    /// </summary>
    /// <param name="context">The saga context.</param>
    /// <param name="outcomes">This run's outcomes for <see cref="Steps"/>, from <see cref="ExecuteAsync"/> (same order/length).</param>
    /// <returns>Each step's outcome updated to reflect its compensation result, in the same order as <see cref="Steps"/>.</returns>
    public async Task<IReadOnlyList<SagaStepOutcome>> CompensateAsync(SagaContext context, IReadOnlyList<SagaStepOutcome> outcomes)
    {
        return await Task.WhenAll(Steps.Select((step, i) => step.CompensateAsync(context, outcomes[i])));
    }
}
