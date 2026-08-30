using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Resilience.Polly;
using Polly;
using Polly.Retry;
using Xunit;

namespace Benzene.Test.Resilience;

// Task board #288 (round 17, WP-A, work/bug-fix-plan-round17-2026-08.md): round 16's #267
// re-entrancy guard (see PollyResilienceMiddlewareConcurrentAttemptRedTest) does
// Interlocked.Increment *before* the try/finally that decrements it. When the guard fires (a
// genuinely concurrent second attempt), the method throws NotSupportedException *outside* that
// try/finally, so the increment is never paired with a decrement - the per-call counter is left
// permanently at 1. A later, fully sequential attempt in the same HandleAsync call (e.g. an outer
// Retry retrying after the rejected race) then increments the poisoned counter from 1 to 2 and is
// wrongly rejected by the same guard, even though nothing overlapped with it at all.
//
// This test reproduces the review's exact scenario: a Retry pipeline wraps a strategy that races
// two concurrent sub-attempts on round 1 (tripping the guard and leaking the counter) and forwards
// a single, purely sequential attempt on round 2+ (exactly what every out-of-the-box Polly
// strategy does). Green requires round 2's sequential next() call to actually run.
public class PollyGuardCounterLeakTest
{
    // Round 1: fires two concurrent sub-attempts (like a hand-rolled hedge) so the guard trips on
    // one of them, leaking the counter. Round 2+: forwards a single, purely sequential attempt -
    // exactly what every out-of-the-box Polly strategy (Retry/Timeout/CircuitBreaker/RateLimiter)
    // does today.
    private sealed class ConcurrentOnFirstRoundStrategy : ResilienceStrategy
    {
        private int _round;

        protected override async ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
            Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
            ResilienceContext context,
            TState state)
        {
            var round = Interlocked.Increment(ref _round);
            if (round == 1)
            {
                // Polly v8 strategies communicate failure via Outcome<TResult>.Exception, not by
                // faulting the ValueTask itself - inspect both outcomes explicitly and return
                // whichever failed, mimicking a real hedge surfacing a rejected attempt's failure.
                var firstTask = callback(context, state).AsTask();
                var secondTask = callback(context, state).AsTask();
                await Task.WhenAll(firstTask, secondTask);
                var first = await firstTask;
                var second = await secondTask;
                return second.Exception != null ? second : first;
            }
            return await callback(context, state); // later rounds: single, sequential, no overlap
        }
    }

    private sealed class ConcurrentOnFirstRoundStrategyOptions : ResilienceStrategyOptions
    {
    }

    [Fact]
    public async Task HandleAsync_SequentialRetryAfterEarlierConcurrentRace_RunsAndDoesNotThrowNotSupported()
    {
        var sequentialAttemptRan = false;

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            })
            .AddStrategy(_ => new ConcurrentOnFirstRoundStrategy(), new ConcurrentOnFirstRoundStrategyOptions())
            .Build();

        var middleware = new PollyResilienceMiddleware<object>(pipeline);
        var nextCallCount = 0;

        var ex = await Record.ExceptionAsync(() => middleware.HandleAsync(new object(), async () =>
        {
            var call = Interlocked.Increment(ref nextCallCount);
            if (call == 1) { await Task.Delay(50); } // force a genuine async gap so round 1's two
                                                        // sub-attempts actually overlap
            else { sequentialAttemptRan = true; }
        }));

        // Round 1's rejected race is allowed to surface as its own NotSupportedException (Retry
        // semantics), but round 2's purely sequential attempt must actually run - it must never be
        // rejected by a counter poisoned by round 1's guard trip.
        Assert.True(sequentialAttemptRan, "round 2's sequential attempt never ran - the guard counter leaked from round 1's rejection");
    }
}
