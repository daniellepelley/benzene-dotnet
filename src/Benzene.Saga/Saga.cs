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

        // #208/#257: a state-store call failing must never abort the saga's own execution, never skip
        // rollback for effects already applied, and never replace a genuinely successful/rolled-back
        // result with a raw exception - it is surfaced on the returned SagaResult instead (see
        // StateStoreFailure's remarks). Every store call below goes through RecordSafelyAsync for
        // exactly that reason. Only the FIRST failure this attempt is kept (a store failing once is
        // usually failing consistently; the earliest failure is the most informative), but every call
        // is still attempted regardless of an earlier one having failed.
        Exception? stateStoreFailure = null;

        if (store != null)
        {
            sagaId ??= options.SagaId ?? Guid.NewGuid().ToString();
            var ex = await RecordSafelyAsync(() => store.RecordStartedAsync(new SagaRunInfo(sagaId, options.Name, attempt, _stages.Count)));
            stateStoreFailure ??= ex;
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
                    var ex = await RecordSafelyAsync(() => store.RecordStageCompletedAsync(sagaId!, attempt, i));
                    stateStoreFailure ??= ex;
                }

                continue;
            }

            // Stage i failed. Roll back this stage's concurrently-succeeded steps first, then every
            // completed stage newest-first, so effects are undone in the reverse of the order they
            // were created. Runs unconditionally - even if a state-store call already failed above -
            // so a store outage can never suppress compensation for effects genuinely applied (#208).
            var (rollbackClean, failedStageOutcomes, compensationFailures) =
                await RollBackAsync(context, completedStages, stage, outcomes);

            var allFailedOutcomes = failedStageOutcomes.Where(o => o.State == SagaStepState.Failed).ToArray();
            var failedOutcome = allFailedOutcomes.FirstOrDefault();
            var outcome = rollbackClean ? SagaOutcome.RolledBack : SagaOutcome.PartiallyRolledBack;

            if (store != null)
            {
                // Hand the store the outcome as known so far (any earlier store hiccup this attempt
                // included); if THIS call also throws, that's folded into the returned result below
                // rather than propagated - #208's failure-path variant: RecordFinishedAsync itself
                // failing after rollback already ran must not lose CompensationFailures visibility.
                var recordEx = await RecordSafelyAsync(() => store.RecordFinishedAsync(
                    sagaId!, attempt,
                    new SagaResult(outcome, i, failedOutcome?.Result, failedOutcome?.Exception, compensationFailures, allFailedOutcomes, stateStoreFailure)));
                stateStoreFailure ??= recordEx;
            }

            return new SagaResult(outcome, i, failedOutcome?.Result, failedOutcome?.Exception, compensationFailures, allFailedOutcomes, stateStoreFailure);
        }

        if (store != null)
        {
            var recordEx = await RecordSafelyAsync(() => store.RecordFinishedAsync(
                sagaId!, attempt,
                new SagaResult(SagaOutcome.Succeeded, null, null, null, Array.Empty<SagaStepOutcome>(), Array.Empty<SagaStepOutcome>(), stateStoreFailure)));
            stateStoreFailure ??= recordEx;
        }

        // #257: even if RecordFinishedAsync just threw (or an earlier store call did), every stage
        // genuinely succeeded - return that success, with the store failure surfaced, rather than
        // letting a raw exception replace it (which would risk a caller retrying an already-succeeded
        // saga with no compensation and no dedup).
        return new SagaResult(SagaOutcome.Succeeded, null, null, null, Array.Empty<SagaStepOutcome>(), Array.Empty<SagaStepOutcome>(), stateStoreFailure);
    }

    /// <summary>
    /// Runs a state-store call, catching any exception it throws instead of letting it propagate -
    /// see <see cref="RunOnceAsync"/>'s remarks on why a store failure must never abort the saga's own
    /// execution or replace its real outcome.
    /// </summary>
    private static async Task<Exception?> RecordSafelyAsync(Func<Task> storeCall)
    {
        try
        {
            await storeCall();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
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
