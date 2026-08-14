using Benzene.Abstractions.Middleware;
using Benzene.Core;

namespace Benzene.Resilience;

public static class Extensions
{
    /// <summary>
    /// Adds a deadline around the downstream pipeline: if <c>next()</c> has not completed within
    /// <paramref name="timeout"/>, the ambient cancellation token (see
    /// <see cref="Benzene.Abstractions.DI.ICancellationTokenAccessor"/>) fires and the middleware
    /// translates that into a service-side <see cref="TimeoutException"/> rather than a genuine host
    /// cancellation. See <see cref="TimeoutMiddleware{TContext}"/> for the full save/restore and
    /// timeout-vs-cancellation semantics.
    /// </summary>
    /// <param name="app">The pipeline builder to add the middleware to.</param>
    /// <param name="timeout">The maximum duration to allow the downstream pipeline to run.</param>
    public static IMiddlewarePipelineBuilder<TContext> UseTimeout<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app,
        TimeSpan timeout)
    {
        return app.Use(resolver => new TimeoutMiddleware<TContext>(resolver.GetService<CancellationTokenAccessor>(), timeout));
    }

    public static IMiddlewarePipelineBuilder<TContext> UseRetry<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app,
        int numberOfRetries = 3,
        TimeSpan? initialDelay = null,
        double backoffFactor = 2.0,
        TimeSpan? maxDelay = null,
        Func<Exception, bool>? shouldRetry = null,
        Func<TContext, bool>? shouldRetryContext = null,
        Func<TimeSpan, TimeSpan>? jitter = null,
        Func<TimeSpan, Task>? delay = null)
    {
        return app.Use(_ => new RetryMiddleware<TContext>(
            numberOfRetries, initialDelay, backoffFactor, maxDelay, shouldRetry, shouldRetryContext, jitter, delay));
    }
}
