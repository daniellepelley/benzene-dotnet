using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Core;
using Benzene.Resilience.Polly;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using Xunit;

namespace Benzene.Test.Resilience;

public class PollyResilienceMiddlewareTest
{
    private sealed class TestContext
    {
        public bool Failed { get; set; }
    }

    private static ResiliencePipeline RetryPipeline(int retries, PredicateBuilder<object> shouldHandle)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = retries,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = shouldHandle,
            })
            .Build();
    }

    [Fact]
    public async Task HandleAsync_Success_RunsNextOnce()
    {
        var attempts = 0;
        var middleware = new PollyResilienceMiddleware<TestContext>(
            RetryPipeline(3, new PredicateBuilder().Handle<Exception>()));

        await middleware.HandleAsync(new TestContext(), () =>
        {
            attempts++;
            return Task.CompletedTask;
        });

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task HandleAsync_ThrowsThenSucceeds_RetriesUntilSuccess()
    {
        var attempts = 0;
        var middleware = new PollyResilienceMiddleware<TestContext>(
            RetryPipeline(3, new PredicateBuilder().Handle<InvalidOperationException>()));

        await middleware.HandleAsync(new TestContext(), () =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.CompletedTask;
        });

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task HandleAsync_AlwaysThrows_ExhaustsRetriesThenPropagatesRealException()
    {
        var attempts = 0;
        var middleware = new PollyResilienceMiddleware<TestContext>(
            RetryPipeline(2, new PredicateBuilder().Handle<InvalidOperationException>()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.HandleAsync(new TestContext(), () =>
            {
                attempts++;
                throw new InvalidOperationException("always");
            }));

        Assert.Equal(3, attempts); // initial + 2 retries
    }

    [Fact]
    public async Task HandleAsync_FailureResult_WithIsFailure_RetriesOnTheResult()
    {
        var attempts = 0;
        // Pipeline retries on the sentinel the middleware throws for a failure result.
        var middleware = new PollyResilienceMiddleware<TestContext>(
            RetryPipeline(3, new PredicateBuilder().Handle<BenzeneFailureResultException>()),
            isFailure: ctx => ctx.Failed);

        var context = new TestContext();
        await middleware.HandleAsync(context, () =>
        {
            attempts++;
            context.Failed = attempts < 3; // fails (as a result, not a throw) the first two attempts
            return Task.CompletedTask;
        });

        Assert.Equal(3, attempts);
        Assert.False(context.Failed);
    }

    [Fact]
    public async Task HandleAsync_FailureResult_RetriesExhausted_SwallowsSentinel_LeavesResultOnContext()
    {
        var attempts = 0;
        var middleware = new PollyResilienceMiddleware<TestContext>(
            RetryPipeline(2, new PredicateBuilder().Handle<BenzeneFailureResultException>()),
            isFailure: ctx => ctx.Failed);

        var context = new TestContext();

        // No exception escapes even though every attempt "failed": the sentinel is swallowed and the
        // failure result remains observable on the context.
        await middleware.HandleAsync(context, () =>
        {
            attempts++;
            context.Failed = true;
            return Task.CompletedTask;
        });

        Assert.Equal(3, attempts);
        Assert.True(context.Failed);
    }

    [Fact]
    public async Task HandleAsync_NoIsFailure_DoesNotRetryOnFailureResult()
    {
        var attempts = 0;
        var middleware = new PollyResilienceMiddleware<TestContext>(
            RetryPipeline(3, new PredicateBuilder().Handle<Exception>()));

        var context = new TestContext();
        await middleware.HandleAsync(context, () =>
        {
            attempts++;
            context.Failed = true; // a failure result, but no predicate wired -> not retried
            return Task.CompletedTask;
        });

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task HandleAsync_AmbientTokenAlreadyCancelled_PropagatesIntoPollyWithoutRunningNext()
    {
        // #250(a): the ambient ICancellationTokenAccessor token now reaches
        // ResiliencePipeline.ExecuteAsync's own cancellationToken parameter - before this fix it was
        // always CancellationToken.None, so upstream cancellation (host shutdown, an outer
        // .UseTimeout(...) layer, ...) could never reach Polly at all. An already-cancelled ambient
        // token is observed before Polly ever invokes the callback - next() never runs, and the
        // documented "which token reaches the mocked transport" style assertion (per the ruling) is
        // that it's THIS token, not some other one, that stopped it.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var middleware = new PollyResilienceMiddleware<TestContext>(
            RetryPipeline(3, new PredicateBuilder().Handle<Exception>()),
            cancellation: accessor);

        var attempts = 0;
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            middleware.HandleAsync(new TestContext(), () =>
            {
                attempts++;
                return Task.CompletedTask;
            }));

        Assert.Equal(cts.Token, thrown.CancellationToken);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task HandleAsync_TimeoutStrategy_FiresOnSchedule_ButDoesNotCancelTheWrappedWork()
    {
        // #250(c) - deliberately NOT implemented (see PollyResilienceMiddleware's own XML remarks for
        // the full reasoning: a save/restore re-seed of the ambient accessor is safe for a strategy
        // that invokes the callback once per attempt sequentially, but breaks under Hedging, which
        // this same pipeline supports, via concurrent writes to one scope-shared mutable accessor).
        // This pins the HONEST documented behaviour instead: Polly's own Timeout strategy still fires
        // TimeoutRejectedException on schedule (Polly races its own timer against the callback task),
        // but next() itself - never handed any token - keeps running to completion afterwards,
        // uncoordinated. Compose Benzene.Resilience's .UseTimeout(...) INSIDE the Polly-wrapped
        // pipeline for a deadline that genuinely cancels downstream work.
        var pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromMilliseconds(50))
            .Build();
        var middleware = new PollyResilienceMiddleware<TestContext>(pipeline);

        var nextCompleted = new TaskCompletionSource<bool>();
        var handleTask = middleware.HandleAsync(new TestContext(), async () =>
        {
            // No CancellationToken available to observe - next() has no parameter to receive one on.
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            nextCompleted.SetResult(true);
        });

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => handleTask);

        // The timeout fired well before next()'s own 400ms delay elapsed.
        Assert.False(nextCompleted.Task.IsCompleted);

        // ...and next() was never told to stop, so it keeps running and completes on its own.
        var winner = await Task.WhenAny(nextCompleted.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(nextCompleted.Task, winner);
    }
}
