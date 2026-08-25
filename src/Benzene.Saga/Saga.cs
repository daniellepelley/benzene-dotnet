namespace Benzene.Saga;

/// <summary>
/// An in-code orchestrator for a distributed transaction: an ordered list of stages, each a group
/// of steps run concurrently. Runs stages in order, threading each stage's results into a shared
/// <see cref="SagaContext"/> for later stages; if any stage fails, every effect created so far is
/// compensated in reverse order, leaving the system back at its starting state so the saga can be
/// retried. It is all-or-nothing: it either completes in full or rolls back in full.
/// </summary>
/// <remarks>
/// <b>A built <see cref="Saga"/> is immutable and safe for concurrent <see cref="RunAsync()"/> calls.</b>
/// <see cref="SagaBuilder.Build"/> produces a <see cref="Saga"/> whose stages and steps are read-only
/// descriptors - no per-execution outcome (a step's state, result, or exception) is ever stored on
/// them. Each <c>RunAsync</c> call creates its own run-scoped <see cref="SagaStepOutcome"/>s (one per
/// step, threaded through as a list local to that call) rather than mutating shared state, so the same
/// built <see cref="Saga"/> instance can be reused - including run concurrently, any number of times -
/// without one run's outcome corrupting another's.
/// </remarks>
public class Saga
{
    private readonly IReadOnlyList<Stage> _stages;

    internal Saga(IReadOnlyList<Stage> stages)
    {
        _stages = stages;
    }

    /// <summary>
    /// Runs the saga. Executes each stage in order; on the first stage failure, compensates every
    /// completed effect in reverse (last-in, first-out) order and returns a rolled-back result.
    /// </summary>
    /// <returns>The saga's outcome.</returns>
    public Task<SagaResult> RunAsync() => RunOnceAsync(new SagaRunOptions(), attempt: 1);

    /// <summary>
    /// Runs the saga with the given options — an optional durable <see cref="ISagaStateStore"/> and
    /// an optional <see cref="SagaRetryPolicy"/>. With a retry policy, a <em>clean</em> rollback is
    /// re-run (from scratch) up to the policy's attempt limit; a success, or a
    /// <see cref="SagaOutcome.PartiallyRolledBack"/> outcome (which may have left effects), is never
    /// retried.
    /// </summary>
    /// <param name="options">The run options.</param>
    /// <returns>The saga's outcome (of the final attempt).</returns>
    public async Task<SagaResult> RunAsync(SagaRunOptions options)
    {
        var policy = options.RetryPolicy;
        var maxAttempts = policy?.MaxAttempts ?? 1;
        var delay = policy?.InitialDelay ?? TimeSpan.Zero;

        // A single, stable id shared across every attempt of this run.
        var sagaId = options.SagaId ?? (options.StateStore != null ? Guid.NewGuid().ToString() : null);

        SagaResult result = null!;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            result = await RunOnceAsync(options, attempt, sagaId);

            // Only a clean rollback is safe to retry; stop otherwise or when attempts are exhausted.
            if (result.Outcome != SagaOutcome.RolledBack || attempt == maxAttempts)
            {
                return result;
            }

            if (delay > TimeSpan.Zero)
            {
                await policy!.Delay(delay);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * policy.BackoffFactor);
            }
        }

        return result;
    }

    private async Task<SagaResult> RunOnceAsync(SagaRunOptions options, int attempt, string? sagaId = null)
    {
        var store = options.StateStore;
        if (store != null)
        {
            sagaId ??= options.SagaId ?? Guid.NewGuid().ToString();
            await store.RecordStartedAsync(new SagaRunInfo(sagaId, options.Name, attempt, _stages.Count));
        }

        // Run-scoped: lives only for this one attempt, so nothing here is ever shared across
        // concurrent or retried runs of the same built Saga.
        var context = new SagaContext();
        var completedStages = new List<(Stage Stage, IReadOnlyList<SagaStepOutcome> Outcomes)>();

        for (var i = 0; i < _stages.Count; i++)
        {
            var stage = _stages[i];
            var outcomes = await stage.ExecuteAsync(context);

            if (outcomes.All(o => o.State == SagaStepState.Succeeded))
            {
                stage.Publish(context, outcomes);
                completedStages.Add((stage, outcomes));
                if (store != null)
                {
                    await store.RecordStageCompletedAsync(sagaId!, attempt, i);
                }

                continue;
            }

            // Stage i failed. Roll back this stage's concurrently-succeeded steps first, then every
            // completed stage newest-first, so effects are undone in the reverse of the order they
            // were created.
            var (rollbackClean, failedStageOutcomes, compensationFailures) =
                await RollBackAsync(context, completedStages, stage, outcomes);

            var failedOutcome = failedStageOutcomes.FirstOrDefault(o => o.State == SagaStepState.Failed);

            var failure = new SagaResult(
                rollbackClean ? SagaOutcome.RolledBack : SagaOutcome.PartiallyRolledBack,
                i,
                failedOutcome?.Result,
                failedOutcome?.Exception,
                compensationFailures);

            if (store != null)
            {
                await store.RecordFinishedAsync(sagaId!, attempt, failure);
            }

            return failure;
        }

        var success = new SagaResult(SagaOutcome.Succeeded, null, null, null, Array.Empty<SagaStepOutcome>());
        if (store != null)
        {
            await store.RecordFinishedAsync(sagaId!, attempt, success);
        }

        return success;
    }

    private static async Task<(bool Clean, IReadOnlyList<SagaStepOutcome> FailedStageOutcomes, IReadOnlyList<SagaStepOutcome> CompensationFailures)> RollBackAsync(
        SagaContext context,
        List<(Stage Stage, IReadOnlyList<SagaStepOutcome> Outcomes)> completedStages,
        Stage failedStage,
        IReadOnlyList<SagaStepOutcome> failedStageOutcomes)
    {
        var compensatedFailedStage = await failedStage.CompensateAsync(context, failedStageOutcomes);
        var allOutcomes = new List<SagaStepOutcome>(compensatedFailedStage);

        for (var j = completedStages.Count - 1; j >= 0; j--)
        {
            var (stage, outcomes) = completedStages[j];
            var compensated = await stage.CompensateAsync(context, outcomes);
            allOutcomes.AddRange(compensated);
        }

        var clean = allOutcomes.All(o => o.State != SagaStepState.CompensationFailed);
        var compensationFailures = allOutcomes.Where(o => o.State == SagaStepState.CompensationFailed).ToArray();

        return (clean, compensatedFailedStage, compensationFailures);
    }
}
