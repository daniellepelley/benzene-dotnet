using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
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
/// <typeparam name="TContext">The pipeline context type.</typeparam>
/// <remarks>
/// <para>
/// <b>#250 - what cancellation actually reaches Polly, and what doesn't.</b> The ambient
/// <see cref="ICancellationTokenAccessor"/> token (host shutdown, client disconnect, an outer
/// <c>UseTimeout</c>/<c>PollyResilienceMiddleware</c> layer's own linked token, ...) IS now passed as
/// <see cref="ResiliencePipeline.ExecuteAsync{TState}(System.Func{TState,System.Threading.CancellationToken,System.Threading.Tasks.ValueTask},TState,System.Threading.CancellationToken)"/>'s
/// overall <c>cancellationToken</c> - Polly observes it between/around attempts (a retry loop stops
/// starting new attempts, a circuit breaker's own waits respect it, etc.), where before this fix it
/// was always <see cref="CancellationToken.None"/> and upstream cancellation could not reach Polly at
/// all.
/// </para>
/// <para>
/// What this does NOT do: make a Polly <c>Timeout</c>/<c>Hedging</c> strategy's own <em>per-attempt</em>
/// token actually cancel <c>next()</c>. <c>next</c> is a plain <see cref="Func{Task}"/> with no
/// <see cref="CancellationToken"/> parameter - Benzene middleware never threads one through
/// <c>HandleAsync</c> - so there is no direct parameter to hand Polly's per-attempt token to; the
/// callback below still receives (and still discards) it for exactly that reason. The candidate fix
/// - re-seeding the scope's <c>Benzene.Core.CancellationTokenAccessor</c> with a linked
/// (ambient + per-attempt) token before <c>next()</c>, restoring it in a <c>finally</c>, the same
/// save/restore pattern <c>Benzene.Resilience.TimeoutMiddleware</c> already uses - was evaluated and
/// **deliberately not implemented**: it is safe for a strategy that invokes the callback once per
/// attempt, sequentially (<c>Timeout</c> alone, <c>Retry</c>, composed nesting of either) but breaks
/// under <b>Hedging</b>, which this same pipeline is documented above to support - Hedging can run
/// several attempts of the callback <em>concurrently</em>, racing them against each other. The
/// accessor is one mutable, scope-shared instance (registered Scoped, one per message - not
/// per-attempt); two concurrently-running attempts each doing their own save/reseed/restore against
/// that single shared instance is a genuine data race, and the failure mode is silent and worse than
/// today's gap - a still-in-flight attempt can observe (or restore into) a completely unrelated
/// attempt's token, so downstream code reading <see cref="ICancellationTokenAccessor.CancellationToken"/>
/// could be cancelled by the wrong attempt's timer, or fail to be cancelled by its own. Nothing in
/// this middleware's inputs can tell whether the supplied <see cref="ResiliencePipeline"/> contains
/// Hedging, so there is no safe way to enable the reseed only for the strategies where it would be
/// correct. <b>The honest answer, and the one to compose around:</b> a Polly-initiated timeout does
/// not itself cancel the wrapped work; compose <c>Benzene.Resilience</c>'s <c>.UseTimeout(...)</c>
/// <em>inside</em> the Polly-wrapped pipeline (i.e. as one of the steps <c>next()</c> reaches) for a
/// deadline that genuinely cancels downstream work - that middleware already owns exactly this
/// save/restore responsibility, sequentially, on the one path where it's actually safe.
/// </para>
/// </remarks>
public class PollyResilienceMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly ResiliencePipeline _pipeline;
    private readonly Func<TContext, bool>? _isFailure;
    private readonly ICancellationTokenAccessor? _cancellation;

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
    /// <param name="cancellation">
    /// Optional; supplies the ambient <see cref="CancellationToken"/> passed into
    /// <see cref="ResiliencePipeline.ExecuteAsync{TState}(System.Func{TState,System.Threading.CancellationToken,System.Threading.Tasks.ValueTask},TState,System.Threading.CancellationToken)"/>
    /// (#250) - the same constructor-optional idiom <c>HttpBenzeneMessageClient</c> uses. <c>null</c>
    /// (the default) observes no cancellation, matching this middleware's behaviour before #250.
    /// </param>
    public PollyResilienceMiddleware(ResiliencePipeline pipeline, Func<TContext, bool>? isFailure = null,
        ICancellationTokenAccessor? cancellation = null)
    {
        _pipeline = pipeline;
        _isFailure = isFailure;
        _cancellation = cancellation;
    }

    /// <inheritdoc />
    public string Name => nameof(PollyResilienceMiddleware<TContext>);

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var token = _cancellation?.CancellationToken ?? CancellationToken.None;
        try
        {
            await _pipeline.ExecuteAsync(static async (state, _) =>
            {
                // The second parameter here is Polly's own per-attempt token (what a Timeout/Hedging
                // strategy arms to cancel THIS attempt) - deliberately unused. next() is a plain
                // Func<Task> with no CancellationToken parameter to hand it to; see this type's XML
                // remarks (#250) for why re-seeding the ambient accessor with it was evaluated and
                // rejected rather than silently attempted here.
                await state.next();

                if (state._isFailure != null && state._isFailure(state.context))
                {
                    // Surface an unsuccessful result to Polly as a handled outcome. Swallowed below
                    // once the pipeline has finished, so the failure result on the context is what
                    // callers see - identical to running without this middleware.
                    throw new BenzeneFailureResultException();
                }
            }, (next, context, _isFailure), token).ConfigureAwait(false);
        }
        catch (BenzeneFailureResultException)
        {
            // Retries (etc.) exhausted and the last attempt still produced a failure result. The
            // result is already on the context; do not propagate the sentinel.
        }
    }
}
