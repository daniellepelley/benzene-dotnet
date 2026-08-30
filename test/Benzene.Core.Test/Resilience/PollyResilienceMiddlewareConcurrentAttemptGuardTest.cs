using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Core;
using Benzene.Resilience.Polly;
using Polly;
using Xunit;

namespace Benzene.Test.Resilience;

// #267: a concurrent-attempt Polly strategy - reachable today via Polly's own public non-generic
// ResiliencePipelineBuilder.AddStrategy(...) extensibility point (a hand-rolled hedge) - used to run
// next() twice for one HandleAsync call, tear the shared ambient CancellationTokenAccessor between
// attempts, and last-write-win on the shared context. All three were proven with a 3/3-passing xUnit
// repro against the unmodified middleware (work/review-round16-performance-2026-08.md, Finding 1).
// The middleware now guards against re-entrant attempts and fails fast instead; these tests assert
// the corrected (fail-fast) behavior. Class name kept from the original red-test recipe for
// traceability back to the review doc.
public class PollyResilienceMiddlewareConcurrentAttemptRedTest
{
    private sealed class MutableResult
    {
        public int Value { get; set; }
    }

    private sealed class ConcurrentDuplicateStrategy : ResilienceStrategy
    {
        protected override async ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
            Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
            ResilienceContext context,
            TState state)
        {
            var first = callback(context, state).AsTask();
            var second = callback(context, state).AsTask();
            var winner = await Task.WhenAny(first, second);
            await Task.WhenAll(first, second); // let the loser finish too, like a real hedge would
            return await winner;
        }
    }

    private sealed class ConcurrentDuplicateStrategyOptions : ResilienceStrategyOptions
    {
    }

    private static ResiliencePipeline ConcurrentDuplicatePipeline() =>
        new ResiliencePipelineBuilder()
            .AddStrategy(_ => new ConcurrentDuplicateStrategy(), new ConcurrentDuplicateStrategyOptions())
            .Build();

    [Fact]
    public async Task HandleAsync_ConcurrentAttemptStrategy_ThrowsNotSupportedException_BeforeCorruptingAnything()
    {
        var activeConcurrently = 0;
        var maxObservedConcurrency = 0;
        var gate = new object();
        var middleware = new PollyResilienceMiddleware<object>(ConcurrentDuplicatePipeline());

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            middleware.HandleAsync(new object(), async () =>
            {
                lock (gate) { activeConcurrently++; maxObservedConcurrency = Math.Max(maxObservedConcurrency, activeConcurrently); }
                await Task.Delay(50);
                lock (gate) { activeConcurrently--; }
            }));

        Assert.Contains("concurrent-attempt", exception.Message, StringComparison.OrdinalIgnoreCase);
        // The guard fires before the second attempt ever calls next() - only the first (winning)
        // attempt's next() runs, so next() is never observed running concurrently with itself.
        Assert.Equal(1, maxObservedConcurrency);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentAttemptStrategy_SharedAccessor_NeverTornBetweenAttempts()
    {
        var accessor = new CancellationTokenAccessor();
        var middleware = new PollyResilienceMiddleware<object>(ConcurrentDuplicatePipeline(), accessor: accessor);
        var mismatchObserved = false;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            middleware.HandleAsync(new object(), async () =>
            {
                var tokenAtEntry = accessor.CancellationToken;
                await Task.Delay(30); // the second (rejected) attempt never reaches this code at all
                if (tokenAtEntry != accessor.CancellationToken) { mismatchObserved = true; }
            }));

        Assert.False(mismatchObserved); // the surviving attempt's token is never torn out from under it
    }

    [Fact]
    public async Task HandleAsync_ConcurrentAttemptStrategy_NextRunsAtMostOnce_NoLastWriteWinsCorruption()
    {
        var writes = 0;
        var context = new MutableResult();
        var middleware = new PollyResilienceMiddleware<MutableResult>(ConcurrentDuplicatePipeline());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            middleware.HandleAsync(context, async () =>
            {
                var mine = Interlocked.Increment(ref writes);
                await Task.Delay(mine == 1 ? 60 : 10);
                context.Value = mine;
            }));

        // next() ran at most once for this HandleAsync call - the second attempt was rejected by the
        // guard before ever calling it, so there is no second write to race against.
        Assert.Equal(1, writes);
    }
}
