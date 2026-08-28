using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.Results;
using Benzene.Saga;
using Xunit;

namespace Benzene.Test.Saga;

// The §7 fast-follows: an optional whole-saga retry policy and a pluggable ISagaStateStore.
public class SagaRetryAndStateStoreTest
{
    private static Task<IBenzeneResult<string>> Ok(string value) => Task.FromResult(BenzeneResult.Ok(value));
    private static Task<IBenzeneResult<string>> Fail() => Task.FromResult(BenzeneResult.ServiceUnavailable<string>());
    private static Task<IBenzeneResult> Undo() => Task.FromResult(BenzeneResult.Ok());

    // ---- Retry -----------------------------------------------------------------------------------

    [Fact]
    public async Task Retry_ReRunsAfterCleanRollback_AndSucceedsOnceTheFlakyStepRecovers()
    {
        var attempts = 0;
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ => Ok("a")).Compensate((_, _) => Undo())))
            .Stage(s => s.Step<string>(step => step.Do(_ =>
            {
                attempts++;
                return attempts < 2 ? Fail() : Ok("b"); // fails first attempt, succeeds on the second
            })))
            .Build();

        var result = await saga.RunAsync(new SagaRunOptions
        {
            RetryPolicy = new SagaRetryPolicy(maxAttempts: 3, delay: _ => Task.CompletedTask)
        });

        Assert.Equal(SagaOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Retry_ExhaustsAttempts_ReturnsRolledBack()
    {
        var attempts = 0;
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ => { attempts++; return Fail(); })))
            .Build();

        var result = await saga.RunAsync(new SagaRunOptions
        {
            RetryPolicy = new SagaRetryPolicy(maxAttempts: 3, delay: _ => Task.CompletedTask)
        });

        Assert.Equal(SagaOutcome.RolledBack, result.Outcome);
        Assert.Equal(3, attempts); // tried the configured maximum
    }

    [Fact]
    public async Task Retry_DoesNotRetry_OnPartiallyRolledBack()
    {
        // Stage 1 succeeds but its compensation fails; stage 2 fails -> rollback is not clean.
        var forwardAttempts = 0;
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step
                .Do(_ => Ok("a"))
                .Compensate((_, _) => Task.FromResult(BenzeneResult.ServiceUnavailable())))) // compensation fails
            .Stage(s => s.Step<string>(step => step.Do(_ => { forwardAttempts++; return Fail(); })))
            .Build();

        var result = await saga.RunAsync(new SagaRunOptions
        {
            RetryPolicy = new SagaRetryPolicy(maxAttempts: 5, delay: _ => Task.CompletedTask)
        });

        Assert.Equal(SagaOutcome.PartiallyRolledBack, result.Outcome);
        Assert.Equal(1, forwardAttempts); // not retried - orphaned effects must not be re-applied
    }

    // ---- State store -----------------------------------------------------------------------------

    [Fact]
    public async Task StateStore_RecordsStart_EachStageCompletion_AndSuccessfulFinish()
    {
        var store = new InMemorySagaStateStore();
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ => Ok("a"))))
            .Stage(s => s.Step<string>(step => step.Do(_ => Ok("b"))))
            .Build();

        await saga.RunAsync(new SagaRunOptions { SagaId = "saga-1", StateStore = store });

        var kinds = store.EventsFor("saga-1").Select(e => e.Kind).ToArray();
        Assert.Equal(new[]
        {
            SagaStateEventKind.Started,
            SagaStateEventKind.StageCompleted,
            SagaStateEventKind.StageCompleted,
            SagaStateEventKind.Finished
        }, kinds);

        var finished = store.EventsFor("saga-1").Single(e => e.Kind == SagaStateEventKind.Finished);
        Assert.Equal(SagaOutcome.Succeeded, finished.Result!.Outcome);
    }

    [Fact]
    public async Task StateStore_OnFailure_RecordsOnlyCompletedStages_AndRolledBackFinish()
    {
        var store = new InMemorySagaStateStore();
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ => Ok("a")).Compensate((_, _) => Undo())))
            .Stage(s => s.Step<string>(step => step.Do(_ => Fail())))
            .Build();

        await saga.RunAsync(new SagaRunOptions { SagaId = "saga-2", StateStore = store });

        var events = store.EventsFor("saga-2");
        Assert.Single(events.Where(e => e.Kind == SagaStateEventKind.StageCompleted)); // only stage 0
        Assert.Equal(0, events.Single(e => e.Kind == SagaStateEventKind.StageCompleted).StageIndex);
        Assert.Equal(SagaOutcome.RolledBack, events.Single(e => e.Kind == SagaStateEventKind.Finished).Result!.Outcome);
    }

    [Fact]
    public async Task StateStore_RecordsEachRetryAttempt()
    {
        var store = new InMemorySagaStateStore();
        var attempts = 0;
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ =>
            {
                attempts++;
                return attempts < 2 ? Fail() : Ok("a");
            })))
            .Build();

        await saga.RunAsync(new SagaRunOptions
        {
            SagaId = "saga-3",
            StateStore = store,
            RetryPolicy = new SagaRetryPolicy(maxAttempts: 3, delay: _ => Task.CompletedTask)
        });

        var startedAttempts = store.EventsFor("saga-3")
            .Where(e => e.Kind == SagaStateEventKind.Started)
            .Select(e => e.Attempt)
            .ToArray();
        Assert.Equal(new[] { 1, 2 }, startedAttempts); // one Started per attempt, sharing the saga id
    }

    [Fact]
    public async Task StateStore_GeneratesSagaId_WhenNoneSupplied()
    {
        var store = new InMemorySagaStateStore();
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ => Ok("a"))))
            .Build();

        await saga.RunAsync(new SagaRunOptions { StateStore = store });

        Assert.NotEmpty(store.Events);
        Assert.False(string.IsNullOrEmpty(store.Events[0].SagaId));
    }

    // ---- State-store failure handling (#208, #257) ------------------------------------------------

    // Wraps a real InMemorySagaStateStore so a test can make one specific call throw (a real store
    // failure, not the store simply being absent) while every other call still records normally -
    // used to prove #208/#257's fix: the saga's own outcome/rollback must never be lost or aborted by
    // a state-store failure, and the failure itself must be surfaced via SagaResult.StateStoreFailure
    // rather than propagating as a raw exception out of RunAsync.
    private sealed class ThrowingSagaStateStore : ISagaStateStore
    {
        private readonly InMemorySagaStateStore _inner = new();

        public bool ThrowOnRecordStageCompleted { get; set; }
        public bool ThrowOnRecordFinished { get; set; }

        public IReadOnlyList<SagaStateEvent> Events => _inner.Events;

        public Task RecordStartedAsync(SagaRunInfo run, CancellationToken cancellationToken = default)
            => _inner.RecordStartedAsync(run, cancellationToken);

        public Task RecordStageCompletedAsync(string sagaId, int attempt, int stageIndex, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRecordStageCompleted)
            {
                throw new InvalidOperationException("simulated state store failure recording stage completion");
            }

            return _inner.RecordStageCompletedAsync(sagaId, attempt, stageIndex, cancellationToken);
        }

        public Task RecordFinishedAsync(string sagaId, int attempt, SagaResult result, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRecordFinished)
            {
                throw new InvalidOperationException("simulated state store failure recording finish");
            }

            return _inner.RecordFinishedAsync(sagaId, attempt, result, cancellationToken);
        }
    }

    /// <summary>
    /// #208: a state-store failure occurring right after an effect-producing stage completes must not
    /// abort the run with zero rollback - a later stage's failure must still compensate the earlier
    /// stage's genuinely-applied effect, and the store failure is surfaced on the result rather than
    /// thrown.
    /// </summary>
    [Fact]
    public async Task StateStoreThrows_AfterAnEffectProducingStageCompletes_StillRollsBack_AndSurfacesTheStoreFailure()
    {
        var log = new List<string>();
        var store = new ThrowingSagaStateStore { ThrowOnRecordStageCompleted = true };
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step
                .Do(_ => { log.Add("s1"); return Ok("a"); })
                .Compensate((_, _) => { log.Add("undo-s1"); return Undo(); })))
            .Stage(s => s.Step<string>(step => step.Do(_ => Fail())))
            .Build();

        var result = await saga.RunAsync(new SagaRunOptions { SagaId = "saga-208", StateStore = store });

        // The saga's own outcome is unaffected by the store failure - rollback still ran for the
        // stage that genuinely completed.
        Assert.Equal(SagaOutcome.RolledBack, result.Outcome);
        Assert.Contains("undo-s1", log);

        // The store failure is surfaced, not swallowed and not thrown.
        Assert.NotNull(result.StateStoreFailure);
        Assert.IsType<InvalidOperationException>(result.StateStoreFailure);
    }

    /// <summary>
    /// #208's failure-path variant: <c>RecordFinishedAsync</c> itself throwing after rollback already
    /// ran must not lose <see cref="SagaResult.CompensationFailures"/> visibility - the computed
    /// result (including compensation failures) must still come back, with the store failure added.
    /// </summary>
    [Fact]
    public async Task StateStoreThrows_OnRecordFinished_AfterRollback_StillReturnsCompensationFailures()
    {
        var store = new ThrowingSagaStateStore { ThrowOnRecordFinished = true };
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step
                .Do(_ => Ok("a"))
                .Compensate((_, _) => Task.FromResult(BenzeneResult.ServiceUnavailable())))) // undo fails
            .Stage(s => s.Step<string>(step => step.Do(_ => Fail())))
            .Build();

        var result = await saga.RunAsync(new SagaRunOptions { SagaId = "saga-208b", StateStore = store });

        Assert.Equal(SagaOutcome.PartiallyRolledBack, result.Outcome);
        Assert.Single(result.CompensationFailures); // not lost despite the store also failing
        Assert.NotNull(result.StateStoreFailure);
    }

    /// <summary>
    /// #257: <c>RecordFinishedAsync</c> throwing after every stage genuinely succeeded must not
    /// discard the successful <see cref="SagaResult"/> - the caller must still learn the saga
    /// succeeded (so it does not blindly retry an already-completed saga), with
    /// <see cref="SagaResult.StateStoreFailure"/> populated to show the store didn't durably record it.
    /// </summary>
    [Fact]
    public async Task StateStoreThrows_OnRecordFinished_AfterFullSuccess_StillReturnsSucceeded()
    {
        var store = new ThrowingSagaStateStore { ThrowOnRecordFinished = true };
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ => Ok("a"))))
            .Build();

        var result = await saga.RunAsync(new SagaRunOptions { SagaId = "saga-257", StateStore = store });

        Assert.Equal(SagaOutcome.Succeeded, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.StateStoreFailure);
        Assert.IsType<InvalidOperationException>(result.StateStoreFailure);
    }

    /// <summary>A configured retry policy must not re-run an already-succeeded saga just because the store failed to record it.</summary>
    [Fact]
    public async Task StateStoreThrows_OnRecordFinished_AfterFullSuccess_DoesNotTriggerARetry()
    {
        var store = new ThrowingSagaStateStore { ThrowOnRecordFinished = true };
        var attempts = 0;
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ => { attempts++; return Ok("a"); })))
            .Build();

        var result = await saga.RunAsync(new SagaRunOptions
        {
            SagaId = "saga-257b",
            StateStore = store,
            RetryPolicy = new SagaRetryPolicy(maxAttempts: 3, delay: _ => Task.CompletedTask)
        });

        Assert.Equal(SagaOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, attempts); // succeeded on the first attempt - retry policy only fires on RolledBack
    }

    [Fact]
    public async Task ParameterlessRun_TouchesNoStore_AndBehavesAsBefore()
    {
        var saga = new SagaBuilder()
            .Stage(s => s.Step<string>(step => step.Do(_ => Ok("a"))))
            .Build();

        var result = await saga.RunAsync();

        Assert.Equal(SagaOutcome.Succeeded, result.Outcome);
    }
}
