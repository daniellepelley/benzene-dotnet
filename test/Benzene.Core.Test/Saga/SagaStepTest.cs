using System;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.Results;
using Benzene.Saga;
using Xunit;

namespace Benzene.Test.Saga;

public class SagaStepTest
{
    [Fact]
    public async Task ExecuteAsync_ReusedAcrossAttempts_DoesNotLeakAnEarlierAttemptsException()
    {
        // A saga retries by re-running the same step instance across attempts (WP-7 #15: the step
        // itself carries no per-execution state at all - each ExecuteAsync call returns a fresh
        // SagaStepOutcome). If attempt 1 THREW and attempt 2 fails by RETURNING a failed result, the
        // second attempt's outcome must not still report attempt 1's exception - that would make
        // SagaResult.FailureException claim the final attempt threw when it did not.
        var calls = 0;
        var step = new SagaStep<string>(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("attempt-1-threw");
            }

            return Task.FromResult(BenzeneResult.ServiceUnavailable<string>());
        });

        var firstOutcome = await step.ExecuteAsync(new SagaContext());
        Assert.NotNull(firstOutcome.Exception);

        var secondOutcome = await step.ExecuteAsync(new SagaContext());

        Assert.Equal(SagaStepState.Failed, secondOutcome.State);
        Assert.Null(secondOutcome.Exception);
    }

    [Fact]
    public async Task ExecuteAsync_SameStepInstance_ConcurrentCalls_EachGetsItsOwnIndependentOutcome()
    {
        // The core WP-7 #15 guarantee at the step level: a single SagaStep<T> instance (as reused by a
        // built Saga's shared, immutable step descriptors) returns an independent SagaStepOutcome per
        // call - concurrent calls never share or corrupt each other's state.
        var step = new SagaStep<int>(async ctx =>
        {
            var n = ctx.Get<int>();
            await Task.Delay(1);
            return n % 2 == 0 ? BenzeneResult.Ok(n) : BenzeneResult.ServiceUnavailable<int>();
        });

        var tasks = Enumerable.Range(0, 100).Select(async n =>
        {
            var context = new SagaContext();
            context.Set(n);
            var outcome = await step.ExecuteAsync(context);
            return (n, outcome);
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            var expected = r.n % 2 == 0 ? SagaStepState.Succeeded : SagaStepState.Failed;
            Assert.Equal(expected, r.outcome.State);
        });
    }
}
