namespace Benzene.Saga;

/// <summary>
/// An in-code orchestrator for a distributed transaction: an ordered list of stages, each a group
/// of steps run concurrently. Runs stages in order, threading each stage's results into a shared
/// <see cref="SagaContext"/> for later stages; if any stage fails, every effect created so far is
/// compensated in reverse order, leaving the system back at its starting state so the saga can be
/// retried. It is all-or-nothing: it either completes in full or rolls back in full.
/// </summary>
/// <remarks>
/// <para>
/// <b>A built <see cref="Saga"/> is immutable and safe for concurrent <see cref="RunAsync()"/> calls.</b>
/// <see cref="SagaBuilder.Build"/> produces a <see cref="Saga"/> whose stages and steps are read-only
/// descriptors - no per-execution outcome (a step's state, result, or exception) is ever stored on
/// them. Each <c>RunAsync</c> call creates its own run-scoped <see cref="SagaStepOutcome"/>s (one per
/// step, threaded through as a list local to that call) rather than mutating shared state, so the same
/// built <see cref="Saga"/> instance can be reused - including run concurrently, any number of times -
/// without one run's outcome corrupting another's.
/// </para>
/// <para>
/// <b>The all-or-nothing guarantee also covers a failure of the optional <see cref="ISagaStateStore"/>
/// (see <see cref="SagaRunOptions.StateStore"/>).</b> A store call made <em>after</em> an
/// effect-producing stage has completed (<see cref="ISagaStateStore.RecordStageCompletedAsync"/>, and
/// the final <see cref="ISagaStateStore.RecordFinishedAsync"/> call on a successful run) is treated the
/// same as a step failure: the exception is caught, every completed stage is compensated, and the run
/// returns a <see cref="SagaOutcome.RolledBack"/> (or <see cref="SagaOutcome.PartiallyRolledBack"/>, if
/// a compensation also fails) result carrying the store's exception in
/// <see cref="SagaResult.StateStoreException"/> - never a raw throw out of <c>RunAsync</c>. The one
/// deliberate exception is <see cref="ISagaStateStore.RecordStartedAsync"/>: a failure there happens
/// strictly before any stage has run, so there is nothing yet to compensate, and it is left to
/// propagate raw rather than manufacture a rollback for zero effects.
/// </para>
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

            // #208 edge case (deliberate, documented on the Saga class): nothing has run yet, so
            // there is nothing to compensate. This one call is left to propagate raw; every store
            // call below happens after an effect-producing stage has completed and is guarded.
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
                    try
                    {
                        await store.RecordStageCompletedAsync(sagaId!, attempt, i);
                    }
                    catch (Exception storeEx)
                    {
                        // #208: this happens strictly after a real, effect-producing stage has
                        // completed - the all-or-nothing guarantee means we compensate exactly as a
                        // step failure would, rather than let this propagate raw and orphan the
                        // stage's effects.
                        return await HandleStateStoreFailureAsync(context, completedStages, i, storeEx, store, sagaId!, attempt);
                    }
                }

                continue;
            }

            // Stage i failed. Roll back this stage's concurrently-succeeded steps first, then every
            // completed stage newest-first, so effects are undone in the reverse of the order they
            // were created.
            var (rollbackClean, failedStageOutcomes, compensationFailures) =
                await RollBackAsync(context, completedStages, stage, outcomes);

            // #209: every step in the failing stage that actually failed is surfaced, not just the
            // first - two steps in the same stage can fail concurrently.
            var failures = failedStageOutcomes.Where(o => o.State == SagaStepState.Failed).ToArray();

            var failure = new SagaResult(
                rollbackClean ? SagaOutcome.RolledBack : SagaOutcome.PartiallyRolledBack,
                i,
                failures,
                compensationFailures);

            if (store != null)
            {
                try
                {
                    await store.RecordFinishedAsync(sagaId!, attempt, failure);
                }
                catch
                {
                    // Best-effort: rollback already ran (cleanly or partially) before this call, so
                    // there is nothing further to compensate - a second store failure here must not
                    // discard the already-computed, correct result.
                }
            }

            return failure;
        }

        var success = new SagaResult(SagaOutcome.Succeeded, null, Array.Empty<SagaStepOutcome>(), Array.Empty<SagaStepOutcome>());
        if (store != null)
        {
            try
            {
                await store.RecordFinishedAsync(sagaId!, attempt, success);
            }
            catch (Exception storeEx)
            {
                // #208: every stage has already completed (real effects exist) by the time we reach
                // this final persistence call, so the same all-or-nothing guarantee applies here too.
                return await HandleStateStoreFailureAsync(context, completedStages, _stages.Count - 1, storeEx, store, sagaId!, attempt);
            }
        }

        return success;
    }

    /// <summary>
    /// #208: handles an <see cref="ISagaStateStore"/> failure that happened after at least one
    /// effect-producing stage completed, by compensating every completed stage (last-in, first-out)
    /// and returning a rollback-status result carrying the store's exception, instead of letting it
    /// propagate raw. Best-effort persists that result via <see cref="ISagaStateStore.RecordFinishedAsync"/>
    /// too, swallowing a further store failure there - the store is already known to be failing, and a
    /// second failure must not mask the rollback outcome the caller needs.
    /// </summary>
    private static async Task<SagaResult> HandleStateStoreFailureAsync(
        SagaContext context,
        List<(Stage Stage, IReadOnlyList<SagaStepOutcome> Outcomes)> completedStages,
        int failedStageIndex,
        Exception storeException,
        ISagaStateStore store,
        string sagaId,
        int attempt)
    {
        var (clean, compensationFailures) = await RollBackCompletedStagesAsync(context, completedStages);

        var result = new SagaResult(
            clean ? SagaOutcome.RolledBack : SagaOutcome.PartiallyRolledBack,
            failedStageIndex,
            Array.Empty<SagaStepOutcome>(),
            compensationFailures,
            storeException);

        try
        {
            await store.RecordFinishedAsync(sagaId, attempt, result);
        }
        catch
        {
            // Best-effort - see the method summary.
        }

        return result;
    }

    /// <summary>
    /// Compensates every completed stage, newest-first (last-in, first-out), with no distinguished
    /// "failed stage" - used for a state-store-triggered rollback (see
    /// <see cref="HandleStateStoreFailureAsync"/>), where every stage in <paramref name="completedStages"/>
    /// actually succeeded and the trigger was the store, not a step.
    /// </summary>
    private static async Task<(bool Clean, IReadOnlyList<SagaStepOutcome> CompensationFailures)> RollBackCompletedStagesAsync(
        SagaContext context,
        List<(Stage Stage, IReadOnlyList<SagaStepOutcome> Outcomes)> completedStages)
    {
        var allOutcomes = new List<SagaStepOutcome>();
        for (var j = completedStages.Count - 1; j >= 0; j--)
        {
            var (stage, outcomes) = completedStages[j];
            var compensated = await stage.CompensateAsync(context, outcomes);
            allOutcomes.AddRange(compensated);
        }

        var clean = allOutcomes.All(o => o.State != SagaStepState.CompensationFailed);
        var compensationFailures = allOutcomes.Where(o => o.State == SagaStepState.CompensationFailed).ToArray();

        return (clean, compensationFailures);
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
