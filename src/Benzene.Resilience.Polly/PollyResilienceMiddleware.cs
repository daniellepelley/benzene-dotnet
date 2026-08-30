using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Polly;

namespace Benzene.Resilience.Polly;

/// <summary>
/// Middleware that runs the rest of the pipeline (<c>next</c>) through a Polly v8
/// <see cref="ResiliencePipeline"/> - so any <i>sequential-attempt</i> strategy the pipeline is
/// built with (retry, timeout, circuit breaker, rate limiter, ...) applies to whatever
/// <c>next</c> wraps: a handler dispatch on an inbound pipeline, or a port/service call on an
/// outbound one. The <see cref="ResiliencePipeline"/> is supplied ready-built, so the only
/// per-message cost is
/// <see cref="ResiliencePipeline.ExecuteAsync{TState}(System.Func{TState,System.Threading.CancellationToken,System.Threading.Tasks.ValueTask},TState,System.Threading.CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <b>Cancellation.</b> <see cref="IMiddleware{TContext}"/>'s <c>next</c> delegate carries no
/// <see cref="CancellationToken"/> parameter, so this middleware cannot pass Polly's per-attempt
/// token to <c>next</c> directly. Instead - exactly the pattern
/// <see cref="Benzene.Resilience.TimeoutMiddleware{TContext}"/> uses - it exposes that token to
/// whatever <c>next</c> wraps via the ambient <see cref="CancellationTokenAccessor"/>: for the
/// duration of each Polly attempt it links the attempt's token with whatever ambient token was
/// already set (so an outer <c>UseTimeout</c>, or any other seeded host token, is never lost),
/// sets the accessor to the linked token before invoking <c>next()</c>, and restores the prior
/// ambient token in a <c>finally</c> once the attempt finishes - so Timeout/RateLimiter (and any
/// other sequential-attempt Polly strategy that cancels an attempt) actually reach downstream
/// code, and nested resilience wraps compose. As with
/// <see cref="Benzene.Resilience.TimeoutMiddleware{TContext}"/>, this can only cancel work that
/// <i>observes</i> the ambient token - a <c>next()</c> that never reads
/// <see cref="Benzene.Abstractions.DI.ICancellationTokenAccessor"/> still runs to completion
/// even after Polly abandons the attempt.
/// <para>
/// <b>Concurrent-attempt strategies are not supported.</b> This middleware shares one
/// <c>next</c> closure, one <typeparamref name="TContext"/> instance, and one ambient
/// <see cref="CancellationTokenAccessor"/> across every Polly attempt - safe only when Polly
/// invokes the callback strictly one attempt at a time (true for Retry, Timeout, CircuitBreaker,
/// RateLimiter). Hedging and Fallback are Polly strategies that run (or can run) more than one
/// attempt concurrently for a single execution; they are also, in Polly.Core 8.5.0, defined only
/// on the <i>generic</i> <c>ResiliencePipelineBuilder&lt;TResult&gt;</c>, which this middleware's
/// non-generic <see cref="ResiliencePipeline"/> cannot build in the first place. But the
/// non-generic builder's own public <c>AddStrategy(...)</c> extensibility point can still be used
/// to hand-roll a concurrent-attempt strategy, and this middleware has no way to distinguish that
/// from a sequential one. If a second attempt starts while one is already in flight for the same
/// <see cref="HandleAsync"/> call, the middleware throws <see cref="NotSupportedException"/>
/// rather than running <c>next()</c> twice and corrupting the shared context/token - see
/// <c>docs/cookbooks/polly-resilience.md</c> for the full explanation.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The pipeline context type.</typeparam>
public class PollyResilienceMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly ResiliencePipeline _pipeline;
    private readonly Func<TContext, bool>? _isFailure;
    private readonly CancellationTokenAccessor _accessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="PollyResilienceMiddleware{TContext}"/> class.
    /// </summary>
    /// <param name="pipeline">The Polly resilience pipeline to execute <c>next</c> through.</param>
    /// <param name="isFailure">
    /// Optional predicate reporting whether the pipeline produced an unsuccessful result after
    /// <c>next</c> ran (e.g. <c>ctx =&gt; ctx.MessageResult?.IsSuccessful == false</c>). When supplied
    /// and it returns <c>true</c>, the middleware throws <see cref="BenzeneFailureResultException"/>
    /// so the Polly pipeline can treat the failure result as a handled outcome; the exception never
    /// escapes (see that type's docs). When <c>null</c> (the default), only thrown exceptions drive
    /// the pipeline's strategies.
    /// </param>
    /// <param name="accessor">
    /// The ambient <see cref="CancellationTokenAccessor"/> to expose each Polly attempt's token
    /// through (see the cancellation remarks on this type). When constructed via
    /// <c>.UseResiliencePipeline(...)</c> this is resolved from the same DI scope as the rest of the
    /// pipeline, so downstream components see the same accessor instance. When <c>null</c> (e.g.
    /// constructing the middleware directly in a test with nothing else sharing the scope) a private
    /// accessor is created; cancellation still works but nothing else observes it.
    /// </param>
    public PollyResilienceMiddleware(ResiliencePipeline pipeline, Func<TContext, bool>? isFailure = null, CancellationTokenAccessor? accessor = null)
    {
        _pipeline = pipeline;
        _isFailure = isFailure;
        _accessor = accessor ?? new CancellationTokenAccessor();
    }

    /// <inheritdoc />
    public string Name => nameof(PollyResilienceMiddleware<TContext>);

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        // Re-entrancy guard (see the "Concurrent-attempt strategies are not supported" remarks
        // above): one counter allocated per HandleAsync call, shared via the state tuple below.
        // Retry/Timeout/CircuitBreaker/RateLimiter all invoke the callback strictly one attempt at
        // a time, so this never goes above 1 for them - zero behavioural change, one field's worth
        // of extra allocation per call. A concurrent-attempt strategy (reachable today only via
        // Polly's own public non-generic AddStrategy(...) extensibility point) drives it above 1,
        // which fails fast instead of running next() twice against the shared context/token.
        var inFlight = new int[1];

        try
        {
            await _pipeline.ExecuteAsync(static async (state, attemptToken) =>
            {
                if (Interlocked.Increment(ref state.inFlight[0]) > 1)
                {
                    throw new NotSupportedException(
                        "A concurrent-attempt resilience strategy (e.g. a custom hedge) is not " +
                        "supported by PollyResilienceMiddleware<TContext>: attempts share the " +
                        "message's pipeline, context, and ambient cancellation token - run " +
                        "attempts sequentially, or hedge at a different layer.");
                }

                var original = state._accessor.CancellationToken;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(original, attemptToken);
                state._accessor.CancellationToken = cts.Token;
                try
                {
                    await state.next();

                    if (state._isFailure != null && state._isFailure(state.context))
                    {
                        // Surface an unsuccessful result to Polly as a handled outcome. Swallowed
                        // below once the pipeline has finished, so the failure result on the context
                        // is what callers see - identical to running without this middleware.
                        throw new BenzeneFailureResultException();
                    }
                }
                finally
                {
                    state._accessor.CancellationToken = original;
                    Interlocked.Decrement(ref state.inFlight[0]);
                }
            }, (next, context, _isFailure, _accessor, inFlight)).ConfigureAwait(false);
        }
        catch (BenzeneFailureResultException)
        {
            // Retries (etc.) exhausted and the last attempt still produced a failure result. The
            // result is already on the context; do not propagate the sentinel.
        }
    }
}
