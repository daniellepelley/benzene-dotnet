using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Core.Middleware;
using Xunit;

namespace Benzene.Test.Core.Middleware;

/// <summary>
/// Coverage for <see cref="BoundedFanOut"/>, the shared primitive behind every batch application's
/// opt-in <c>MaxDegreeOfParallelism</c> knob. Verifies the two properties callers depend on: a
/// positive cap is actually enforced (never more than N run at once), an absent/non-positive cap
/// stays unbounded (the original behavior), results come back in source order regardless of the cap
/// or of completion order, and faults propagate.
/// </summary>
public class BoundedFanOutTest
{
    private sealed class ConcurrencyProbe
    {
        private readonly object _gate = new();
        private int _current;
        public int MaxObserved { get; private set; }

        public async Task RunAsync()
        {
            lock (_gate)
            {
                _current++;
                MaxObserved = Math.Max(MaxObserved, _current);
            }

            await Task.Delay(50);

            lock (_gate)
            {
                _current--;
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task WhenAllAsync_WithPositiveCap_NeverRunsMoreThanTheCapConcurrently(int maxDegreeOfParallelism)
    {
        var probe = new ConcurrencyProbe();

        await BoundedFanOut.WhenAllAsync(Enumerable.Range(0, 40), _ => probe.RunAsync(), maxDegreeOfParallelism);

        Assert.True(probe.MaxObserved <= maxDegreeOfParallelism,
            $"Expected at most {maxDegreeOfParallelism} concurrent, observed {probe.MaxObserved}.");
        Assert.True(probe.MaxObserved == maxDegreeOfParallelism,
            $"Expected the cap of {maxDegreeOfParallelism} to actually be reached, observed {probe.MaxObserved}.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WhenAllAsync_WithNoOrNonPositiveCap_RunsEveryItemConcurrently(int? maxDegreeOfParallelism)
    {
        const int itemCount = 30;
        var probe = new ConcurrencyProbe();

        await BoundedFanOut.WhenAllAsync(Enumerable.Range(0, itemCount), _ => probe.RunAsync(), maxDegreeOfParallelism);

        // Unbounded: every item's delay overlaps, so all of them run at once.
        Assert.Equal(itemCount, probe.MaxObserved);
    }

    [Fact]
    public async Task WhenAllAsync_WithResult_ReturnsResultsInSourceOrder_RegardlessOfCompletionOrder()
    {
        // Later items finish first (descending delay), so a result set ordered by completion would be
        // reversed. BoundedFanOut must still return them in source order.
        var source = Enumerable.Range(0, 20).ToArray();

        var results = await BoundedFanOut.WhenAllAsync(source, async i =>
        {
            await Task.Delay(10 * (source.Length - i));
            return i * 10;
        }, maxDegreeOfParallelism: 4);

        Assert.Equal(source.Select(i => i * 10).ToArray(), results);
    }

    [Fact]
    public async Task WhenAllAsync_Unbounded_ReturnsResultsInSourceOrder()
    {
        var source = Enumerable.Range(0, 20).ToArray();

        var results = await BoundedFanOut.WhenAllAsync(source, async i =>
        {
            await Task.Delay(10 * (source.Length - i));
            return i * 10;
        }, maxDegreeOfParallelism: null);

        Assert.Equal(source.Select(i => i * 10).ToArray(), results);
    }

    [Fact]
    public async Task WhenAllAsync_EmptySource_CompletesWithEmptyResult()
    {
        var results = await BoundedFanOut.WhenAllAsync(Array.Empty<int>(), i => Task.FromResult(i), maxDegreeOfParallelism: 4);
        Assert.Empty(results);
    }

    [Fact]
    public async Task WhenAllAsync_Bounded_RunsEveryItemExactlyOnce()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();

        await BoundedFanOut.WhenAllAsync(Enumerable.Range(0, 100), async i =>
        {
            await Task.Yield();
            seen.Add(i);
        }, maxDegreeOfParallelism: 8);

        Assert.Equal(Enumerable.Range(0, 100), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task WhenAllAsync_Bounded_WhenABodyThrows_TheExceptionPropagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedFanOut.WhenAllAsync(Enumerable.Range(0, 10), async i =>
            {
                await Task.Yield();
                if (i == 5)
                {
                    throw new InvalidOperationException("boom");
                }
            }, maxDegreeOfParallelism: 3));
    }

    [Fact]
    public async Task WhenAllAsync_Void_Bounded_NeverRunsMoreThanTheCapConcurrently()
    {
        var probe = new ConcurrencyProbe();

        // The void overload delegates to the result overload; prove the cap still holds through it.
        await BoundedFanOut.WhenAllAsync(Enumerable.Range(0, 40), (Func<int, Task>)(_ => probe.RunAsync()), 3);

        Assert.True(probe.MaxObserved <= 3, $"Expected at most 3 concurrent, observed {probe.MaxObserved}.");
        Assert.True(probe.MaxObserved == 3, $"Expected the cap of 3 to actually be reached, observed {probe.MaxObserved}.");
    }

    /// <summary>
    /// Regression coverage for #230: an item still queued behind the semaphore's
    /// <c>MaxDegreeOfParallelism</c> gate must observe cancellation instead of waiting indefinitely
    /// for a free slot. The review's exact probe: cap of 1, three items, item 0 sleeps 300ms, cancel at
    /// 50ms - before the fix all three ran to completion (~300ms+, no OCE); after the fix items 1/2
    /// never start and the call surfaces cancellation quickly.
    /// </summary>
    [Fact]
    public async Task WhenAllAsync_Bounded_ItemQueuedBehindTheSemaphore_ObservesCancellation()
    {
        var started = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var task = BoundedFanOut.WhenAllAsync(new[] { 0, 1, 2 }, async i =>
        {
            started.Add(i);
            await Task.Delay(300);
            return i;
        }, maxDegreeOfParallelism: 1, cts.Token);

        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        sw.Stop();

        // Items 1 and 2 were still queued behind the cap when cancellation fired - they must never
        // start. Only item 0 (already running) gets to. This is the load-bearing assertion: it is
        // timing-independent (unlike a wall-clock bound, which is unreliable on a loaded CI box) and
        // fails unambiguously under the pre-fix behavior, where every item ran to completion.
        Assert.Equal(new[] { 0 }, started.OrderBy(x => x));

        // Loose sanity check that cancellation wasn't drained through the full backlog (3 items x
        // 300ms, ~900ms+ pre-fix) - generous enough to tolerate scheduling jitter on a busy host
        // without becoming a source of flakiness; the "started" assertion above is the real guard.
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected cancellation well before the full unbounded drain, took {sw.ElapsedMilliseconds}ms.");
    }

    /// <summary>
    /// The void overload must forward the token through to the same gated path - regression coverage
    /// alongside the result-returning overload's cancellation test.
    /// </summary>
    [Fact]
    public async Task WhenAllAsync_Void_Bounded_ItemQueuedBehindTheSemaphore_ObservesCancellation()
    {
        var started = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var cts = new CancellationTokenSource();

        var task = BoundedFanOut.WhenAllAsync(new[] { 0, 1, 2 }, async i =>
        {
            started.Add(i);
            await Task.Delay(300);
        }, maxDegreeOfParallelism: 1, cts.Token);

        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(new[] { 0 }, started.OrderBy(x => x));
    }

    /// <summary>
    /// An already-running item (not queued) must still be awaited to completion rather than abandoned
    /// when a sibling's queued wait is cancelled - <see cref="Task.WhenAll(Task[])"/> awaits every task
    /// before this call returns/throws, so nothing is left running un-observed.
    /// </summary>
    [Fact]
    public async Task WhenAllAsync_Bounded_OnCancellation_AlreadyStartedItemStillRunsToCompletion()
    {
        var completed = false;
        using var cts = new CancellationTokenSource();

        var task = BoundedFanOut.WhenAllAsync(new[] { 0, 1 }, async i =>
        {
            if (i == 0)
            {
                await Task.Delay(150);
                completed = true;
            }
            else
            {
                // Item 1 is queued behind the cap of 1 and never gets to run before cancellation.
                await Task.Delay(1000);
            }

            return i;
        }, maxDegreeOfParallelism: 1, cts.Token);

        cts.CancelAfter(20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // By the time WhenAllAsync's task has completed (with cancellation), item 0's own 150ms delay
        // must already have finished - it was awaited to completion, not abandoned.
        Assert.True(completed, "Expected the already-started item to run to completion before cancellation surfaced.");
    }
}
