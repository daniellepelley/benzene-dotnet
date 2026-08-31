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

    // --- Cancellation (#237): Polly's per-attempt token used to be discarded, silently defeating
    // Timeout/Hedging/RateLimiter strategies. The middleware now exposes the (possibly linked)
    // per-attempt token to next() via the ambient CancellationTokenAccessor - exactly the cookbook's
    // "Testing" sample, which this test runs verbatim (bar constructing the accessor it now needs).

    [Fact]
    public async Task HandleAsync_TimeoutStrategy_NextObservesAmbientToken_ThrowsTimeoutRejectedException()
    {
        var accessor = new CancellationTokenAccessor();
        var pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromMilliseconds(50))
            .Build();
        var middleware = new PollyResilienceMiddleware<object>(pipeline, accessor: accessor);

        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            middleware.HandleAsync(new object(), () => Task.Delay(TimeSpan.FromSeconds(5), accessor.CancellationToken)));
    }

    [Fact]
    public async Task HandleAsync_NextIgnoresToken_RunsToCompletion_NoTimeoutExceptionEvenThoughDeadlineExceeded()
    {
        // The documented caveat: the middleware (like Polly itself) can only cancel work that
        // OBSERVES the ambient token - it cannot forcibly abort a running Task. A next() that never
        // reads the accessor just keeps running past the configured deadline, and since it never
        // throws OperationCanceledException on the timeout token, Polly never raises
        // TimeoutRejectedException either - it simply awaits next() to completion.
        var accessor = new CancellationTokenAccessor();
        var pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromMilliseconds(50))
            .Build();
        var middleware = new PollyResilienceMiddleware<object>(pipeline, accessor: accessor);
        var completed = false;

        var exception = await Record.ExceptionAsync(() => middleware.HandleAsync(new object(), async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200)); // ignores accessor.CancellationToken
            completed = true;
        }));

        Assert.Null(exception);
        Assert.True(completed);
    }

    [Fact]
    public async Task HandleAsync_RestoresAccessor_AfterEachAttempt()
    {
        var accessor = new CancellationTokenAccessor();
        var original = accessor.CancellationToken;
        var pipeline = new ResiliencePipelineBuilder().Build();
        var middleware = new PollyResilienceMiddleware<object>(pipeline, accessor: accessor);
        var observedDuring = default(CancellationToken);

        await middleware.HandleAsync(new object(), () =>
        {
            observedDuring = accessor.CancellationToken;
            return Task.CompletedTask;
        });

        // While inside next(), the accessor was wrapped with a (linked) token distinct from the
        // untouched original - proving the middleware actually set something.
        Assert.NotEqual(original, observedDuring);
        // Restored once the attempt finished.
        Assert.Equal(original, accessor.CancellationToken);
    }

    [Fact]
    public async Task HandleAsync_OuterAmbientTokenCancelled_DownstreamObservesItThroughTheMiddlewareUnchanged()
    {
        // Linked-token test: an outer ambient token (e.g. seeded by a host, or set by an outer
        // UseTimeout) must not be lost when the middleware links in Polly's own per-attempt token.
        using var hostCts = new CancellationTokenSource();
        var accessor = new CancellationTokenAccessor { CancellationToken = hostCts.Token };
        var pipeline = new ResiliencePipelineBuilder().Build(); // no strategies - a pure pass-through
        var middleware = new PollyResilienceMiddleware<object>(pipeline, accessor: accessor);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.HandleAsync(new object(), () =>
        {
            hostCts.Cancel();
            // Downstream code observes the wrapped ambient token, exactly as real code would - it
            // has no way to know it's "really" the host token underneath (mirrors
            // TimeoutMiddlewareTest's equivalent case (c)).
            throw new OperationCanceledException(accessor.CancellationToken);
        }));

        Assert.True(thrown.CancellationToken.IsCancellationRequested);
        // Restored to the (now-cancelled) host token, not left on the by-then-disposed linked token.
        Assert.Equal(hostCts.Token, accessor.CancellationToken);
    }

    [Fact]
    public async Task HandleAsync_NoAccessorSupplied_StillRunsCorrectly()
    {
        // Constructing directly without an accessor (e.g. the DIY/no-DI case) must not regress -
        // a private accessor is created internally so the middleware still runs correctly. next()
        // here has no way to observe that private accessor's token (nothing outside the middleware
        // shares it), so - same as any next() that doesn't cooperate - Polly cannot forcibly abort
        // it; it just waits for next() to finish and returns normally.
        var pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromMilliseconds(50))
            .Build();
        var middleware = new PollyResilienceMiddleware<object>(pipeline);
        var completed = false;

        var exception = await Record.ExceptionAsync(() => middleware.HandleAsync(new object(), async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            completed = true;
        }));

        Assert.Null(exception);
        Assert.True(completed);
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
}
