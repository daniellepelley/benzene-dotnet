using System;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Resilience.Polly;
using Polly;
using Polly.CircuitBreaker;
using Xunit;

namespace Benzene.Test.Resilience;

/// <summary>
/// #63: Polly's own unset <c>ShouldHandle</c> default already excludes
/// <see cref="OperationCanceledException"/> from tripping a circuit breaker, but this repo's own
/// retry-oriented doc/test pattern (<c>new PredicateBuilder().Handle&lt;Exception&gt;()</c>)
/// reintroduces the bug if copy-pasted onto a breaker's <c>ShouldHandle</c>. Proves the new
/// <see cref="CancellationSafePredicateBuilderExtensions.ExcludingCancellation{TResult}"/> helper is
/// cancellation-safe on a breaker, contrasted against the unsafe pattern it replaces.
/// </summary>
public class CancellationSafePredicateBuilderExtensionsTest
{
    private sealed class TestContext
    {
    }

    private static ResiliencePipeline BreakerPipeline(PredicateBuilder<object> shouldHandle)
    {
        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = shouldHandle,
                FailureRatio = 1.0,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 2,
                BreakDuration = TimeSpan.FromSeconds(30),
            })
            .Build();
    }

    private static Func<Task> CancelledCall(CancellationTokenSource cts) => () =>
    {
        cts.Cancel();
        throw new OperationCanceledException(cts.Token);
    };

    [Fact]
    public async Task ExcludingCancellation_TwoCallerCancelledRequests_DoesNotTripTheBreaker()
    {
        var middleware = new PollyResilienceMiddleware<TestContext>(
            BreakerPipeline(new PredicateBuilder().ExcludingCancellation()));

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            middleware.HandleAsync(new TestContext(), CancelledCall(cts1)));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            middleware.HandleAsync(new TestContext(), CancelledCall(cts2)));

        // The breaker never recorded either cancellation as a handled failure, so it's still closed -
        // a normal (non-cancelled) third call reaches the delegate instead of short-circuiting with
        // BrokenCircuitException.
        var ran = false;
        await middleware.HandleAsync(new TestContext(), () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
    }

    // Negative case / "this is why": the old copy-pasted-from-retry pattern DOES trip the breaker on
    // caller cancellation, because Handle<Exception>() treats OperationCanceledException as a handled
    // failure just like any other exception.
    [Fact]
    public async Task HandleException_TwoCallerCancelledRequests_TripsTheBreaker()
    {
        var middleware = new PollyResilienceMiddleware<TestContext>(
            BreakerPipeline(new PredicateBuilder().Handle<Exception>()));

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            middleware.HandleAsync(new TestContext(), CancelledCall(cts1)));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            middleware.HandleAsync(new TestContext(), CancelledCall(cts2)));

        // The breaker is now open purely from caller cancellations - a normal, uncancelled third call
        // is short-circuited without ever reaching the delegate.
        var ran = false;
        await Assert.ThrowsAsync<BrokenCircuitException>(() =>
            middleware.HandleAsync(new TestContext(), () =>
            {
                ran = true;
                return Task.CompletedTask;
            }));

        Assert.False(ran);
    }
}
