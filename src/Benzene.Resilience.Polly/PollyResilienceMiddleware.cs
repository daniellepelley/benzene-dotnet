using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Polly;

namespace Benzene.Resilience.Polly;

/// <summary>
/// Middleware that runs the rest of the pipeline (<c>next</c>) through a Polly v8
/// <see cref="ResiliencePipeline"/> - so any strategy the pipeline is built with (retry, circuit
/// breaker, timeout, hedging, fallback, rate limiter, ...) applies to whatever <c>next</c> wraps: a
/// handler dispatch on an inbound pipeline, or a port/service call on an outbound one. The
/// <see cref="ResiliencePipeline"/> is supplied ready-built, so the only per-message cost is
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
/// ambient token in a <c>finally</c> once the attempt finishes - so Timeout/Hedging/RateLimiter
/// (and any other Polly strategy that cancels an attempt) actually reach downstream code, and
/// nested resilience wraps compose. As with <see cref="Benzene.Resilience.TimeoutMiddleware{TContext}"/>,
/// this can only cancel work that <i>observes</i> the ambient token - a <c>next()</c> that never
/// reads <see cref="Benzene.Abstractions.DI.ICancellationTokenAccessor"/> still runs to completion
/// even after Polly abandons the attempt.
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
        try
        {
            await _pipeline.ExecuteAsync(static async (state, attemptToken) =>
            {
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
                }
            }, (next, context, _isFailure, _accessor)).ConfigureAwait(false);
        }
        catch (BenzeneFailureResultException)
        {
            // Retries (etc.) exhausted and the last attempt still produced a failure result. The
            // result is already on the context; do not propagate the sentinel.
        }
    }
}
