using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.Results;
using Benzene.Saga;
using Xunit;

namespace Benzene.Test.Saga;

public class SagaTest
{
    private static Task<IBenzeneResult<string>> Ok(List<string> log, string tag, string value)
    {
        log.Add(tag);
        return Task.FromResult(BenzeneResult.Ok(value));
    }

    private static Task<IBenzeneResult<string>> Fail(List<string> log, string tag)
    {
        log.Add(tag);
        return Task.FromResult(BenzeneResult.ServiceUnavailable<string>());
    }

    private static Task<IBenzeneResult> Undo(List<string> log, string tag, bool succeeds = true)
    {
        log.Add(tag);
        return Task.FromResult(succeeds ? BenzeneResult.Ok() : BenzeneResult.ServiceUnavailable());
    }

    [Fact]
    public async Task RunAsync_AllStagesSucceed_ReturnsSucceeded_AndThreadsContextForward()
    {
        var log = new List<string>();

        var saga = new SagaBuilder()
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Ok(log, "create-tenant", "tenant-1"))
                .Compensate((_, r) => Undo(log, $"undo-tenant:{r}"))))
            .Stage(stage => stage.Step<string>(step => step
                .Do(ctx => Ok(log, $"create-user:{ctx.Get<string>()}", "user-1"))))
            .Build();

        var result = await saga.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(SagaOutcome.Succeeded, result.Outcome);
        // stage 2 read stage 1's published result; no compensation ran.
        Assert.Equal(new[] { "create-tenant", "create-user:tenant-1" }, log);
    }

    [Fact]
    public async Task RunAsync_ConcurrentStepsRunInParallelWithinAStage()
    {
        var barrier = new TaskCompletionSource();
        var bothStarted = 0;

        async Task<IBenzeneResult<string>> Waiter()
        {
            if (Interlocked.Increment(ref bothStarted) == 2)
            {
                barrier.SetResult();
            }
            await barrier.Task;
            return BenzeneResult.Ok("done");
        }

        var saga = new SagaBuilder()
            .Stage(stage => stage
                .Step<string>(step => step.Do(_ => Waiter()))
                .Step<string>(step => step.Do(_ => Waiter())))
            .Build();

        // If the two steps ran sequentially, the first would await a barrier only the second can
        // release, and this would deadlock/time out. Completing proves they ran concurrently.
        var completed = await Task.WhenAny(saga.RunAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.IsType<Task<SagaResult>>(completed);
        Assert.True(((Task<SagaResult>)completed).Result.IsSuccess);
    }

    [Fact]
    public async Task RunAsync_StepFailsWithinStage_CompensatesSucceededSiblings_AndRollsBack()
    {
        var log = new List<string>();

        var saga = new SagaBuilder()
            .Stage(stage => stage
                .Step<string>(step => step
                    .Do(_ => Ok(log, "create-a", "a-1"))
                    .Compensate((_, r) => Undo(log, $"undo-a:{r}")))
                .Step<string>(step => step
                    .Do(_ => Fail(log, "create-b"))))
            .Build();

        var result = await saga.RunAsync();

        Assert.Equal(SagaOutcome.RolledBack, result.Outcome);
        Assert.Equal(0, result.FailedStageIndex);
        Assert.Contains("undo-a:a-1", log); // the succeeded sibling was compensated
    }

    [Fact]
    public async Task RunAsync_LaterStageFails_CompensatesCompletedStagesInReverseOrder()
    {
        var log = new List<string>();

        var saga = new SagaBuilder()
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Ok(log, "s1", "1"))
                .Compensate((_, r) => Undo(log, "undo-s1"))))
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Ok(log, "s2", "2"))
                .Compensate((_, r) => Undo(log, "undo-s2"))))
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Fail(log, "s3"))))
            .Build();

        var result = await saga.RunAsync();

        Assert.Equal(SagaOutcome.RolledBack, result.Outcome);
        Assert.Equal(2, result.FailedStageIndex);
        // LIFO: s3 fails, then s2 undone, then s1 undone.
        Assert.Equal(new[] { "s1", "s2", "s3", "undo-s2", "undo-s1" }, log);
    }

    [Fact]
    public async Task RunAsync_CompensationItselfFails_ReturnsPartiallyRolledBack()
    {
        var saga = new SagaBuilder()
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Task.FromResult(BenzeneResult.Ok("1")))
                .Compensate((_, _) => Task.FromResult(BenzeneResult.ServiceUnavailable())))) // undo fails
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Task.FromResult(BenzeneResult.ServiceUnavailable<string>())))) // triggers rollback
            .Build();

        var result = await saga.RunAsync();

        Assert.Equal(SagaOutcome.PartiallyRolledBack, result.Outcome);
        Assert.Single(result.CompensationFailures);
        Assert.Equal(SagaStepState.CompensationFailed, result.CompensationFailures[0].State);
    }

    [Fact]
    public async Task RunAsync_ForwardThrows_TreatedAsFailure_AndRollsBackPriorStages()
    {
        var log = new List<string>();

        var saga = new SagaBuilder()
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Ok(log, "s1", "1"))
                .Compensate((_, r) => Undo(log, "undo-s1"))))
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => throw new InvalidOperationException("boom"))))
            .Build();

        var result = await saga.RunAsync();

        Assert.Equal(SagaOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, result.FailedStageIndex);
        Assert.IsType<InvalidOperationException>(result.FailureException);
        Assert.Contains("undo-s1", log);
    }

    [Fact]
    public async Task RunAsync_SucceededStepWithNoCompensation_RollsBackCleanly()
    {
        // A read-only/no-effect step that succeeds has no compensation; a later failure should still
        // report a clean RolledBack (nothing to undo for that step).
        var saga = new SagaBuilder()
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Task.FromResult(BenzeneResult.Ok("read")))))
            .Stage(stage => stage.Step<string>(step => step
                .Do(_ => Task.FromResult(BenzeneResult.ServiceUnavailable<string>()))))
            .Build();

        var result = await saga.RunAsync();

        Assert.Equal(SagaOutcome.RolledBack, result.Outcome);
        Assert.Empty(result.CompensationFailures);
    }

    // #209: two steps in the same stage can fail concurrently (a normal production scenario - two
    // downstream calls both timing out). Before the fix, SagaResult surfaced only one of them via
    // Failure/FailureException; the other had no representation anywhere on the result.
    [Fact]
    public async Task RunAsync_TwoStepsFailConcurrentlyInSameStage_SurfacesBothInFailures()
    {
        var saga = new SagaBuilder()
            .Stage(stage => stage
                .Step<string>(step => step.Do(_ => Task.FromResult(BenzeneResult.ServiceUnavailable<string>())))
                .Step<string>(step => step.Do(_ => throw new InvalidOperationException("boom"))))
            .Build();

        var result = await saga.RunAsync();

        Assert.Equal(SagaOutcome.RolledBack, result.Outcome);
        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, f => Assert.Equal(SagaStepState.Failed, f.State));
        Assert.Contains(result.Failures, f => f.Exception is InvalidOperationException);
        Assert.Contains(result.Failures, f => f.Exception == null && f.Result is { IsSuccessful: false });

        // Failure/FailureException remain a backward-compatible view over the first entry.
        Assert.Same(result.Failures[0].Result, result.Failure);
        Assert.Same(result.Failures[0].Exception, result.FailureException);
    }

    [Fact]
    public void Build_WithNoStages_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new SagaBuilder().Build());
    }

    [Fact]
    public void Build_StepWithNoForward_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SagaBuilder().Stage(stage => stage.Step<string>(_ => { })).Build());
    }

    // WP-7 #15: a built Saga's steps/stages must be immutable descriptors, safe for concurrent
    // RunAsync() calls - no per-execution outcome may be stored on the shared step/stage instances.
    // Before the fix, SagaStep<T> stored its forward result/state/exception on itself; two concurrent
    // RunAsync() calls sharing the same built Saga (and so the same step instances) could race on
    // those fields, so one run's Publish could read back a DIFFERENT run's value out of the SAME
    // step's shared field (the round-5 finding reproduced 6/300 corrupted runs this way). This test
    // reproduces that scenario directly and asserts 0/N corrupted runs.
    [Fact]
    public async Task RunAsync_ManyConcurrentRunsOnOneBuiltSaga_NeverCrossContaminate()
    {
        // AsyncLocal correctly follows this particular logical call chain (including every await
        // inside saga.RunAsync()) regardless of which OS thread actually executes it - so it is
        // ground truth for "which concurrent run is this", independent of (and unaffected by) any
        // race on the step's own fields. This is what lets the test detect cross-run contamination
        // unambiguously rather than merely suspecting it from a flaky final answer.
        var runId = new AsyncLocal<int>();
        var crossContaminated = 0;

        var saga = new SagaBuilder()
            .Stage(stage => stage.Step<int>(step => step
                .Do(async _ =>
                {
                    var mine = runId.Value;
                    // Widen the race window: without this, concurrent runs might not actually
                    // interleave their ExecuteAsync/Publish calls on the shared step instance.
                    await Task.Yield();
                    return BenzeneResult.Ok(mine);
                })))
            .Stage(stage => stage.Step<int>(step => step
                .Do(async ctx =>
                {
                    var mine = runId.Value;
                    var publishedByStage1 = ctx.Get<int>();
                    if (publishedByStage1 != mine)
                    {
                        Interlocked.Increment(ref crossContaminated);
                    }

                    await Task.Yield();
                    return BenzeneResult.Ok(publishedByStage1);
                })))
            .Build();

        const int concurrentRuns = 300;
        var tasks = new Task[concurrentRuns];
        for (var i = 0; i < concurrentRuns; i++)
        {
            var id = i;
            tasks[i] = Task.Run(async () =>
            {
                runId.Value = id;
                var result = await saga.RunAsync();
                Assert.True(result.IsSuccess);
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(0, crossContaminated);
    }
}
