using Polly;

namespace Benzene.Resilience.Polly;

/// <summary>
/// Extension helpers for building a cancellation-safe <c>ShouldHandle</c> predicate for any Polly
/// strategy's own outcome-predicate API. This works on the generic <see cref="PredicateBuilder{TResult}"/>
/// Polly itself exposes for every strategy (including generic-only ones like Hedging/Fallback that
/// you build yourself, outside <c>PollyResilienceMiddleware</c> - see that type's remarks for why it
/// only supports sequential-attempt strategies).
/// </summary>
/// <remarks>
/// <para>
/// Polly's own <c>ShouldHandle</c> <b>default</b> (used when a strategy's options leave it unset)
/// already correctly excludes <see cref="OperationCanceledException"/> - a caller-cancelled request
/// does not trip a circuit breaker or exhaust a retry budget under Polly's default configuration.
/// </para>
/// <para>
/// The footgun is copy-paste: this repo's own retry example widens <c>ShouldHandle</c> to
/// <c>new PredicateBuilder().Handle&lt;Exception&gt;()</c> so a returned <see cref="BenzeneFailureResultException"/>
/// (the outcome-aware bridge, see <c>docs/cookbooks/polly-resilience.md</c>) is handled alongside real
/// exceptions. That pattern is safe for retry - <c>RetryMiddleware{TContext}</c>'s own
/// <c>DefaultShouldRetry</c> is exactly <c>ex is not OperationCanceledException</c> - but copying
/// <c>Handle&lt;Exception&gt;()</c> onto a <b>circuit breaker</b>'s <c>ShouldHandle</c> silently
/// reintroduces the cancellation-safety Polly's own default gave you for free: a caller-cancelled
/// request now counts as a breaker failure and can trip it for every other in-flight caller.
/// </para>
/// <para>
/// Use <see cref="ExcludingCancellation{TResult}"/> instead of <c>Handle&lt;Exception&gt;()</c>
/// whenever you widen a strategy's <c>ShouldHandle</c> beyond a specific exception type - it mirrors
/// <c>RetryMiddleware</c>'s documented safe default so cancellation-safety isn't lost in translation
/// from retry to breaker (or any other strategy).
/// </para>
/// </remarks>
public static class CancellationSafePredicateBuilderExtensions
{
    /// <summary>
    /// Adds a predicate that handles any <see cref="Exception"/> <b>except</b>
    /// <see cref="OperationCanceledException"/> (and subclasses, e.g. <see cref="TaskCanceledException"/>) -
    /// the same safe default <c>RetryMiddleware{TContext}</c> documents and Polly's own unset-
    /// <c>ShouldHandle</c> default already applies. Use this in place of
    /// <c>Handle&lt;Exception&gt;()</c> so a caller-cancelled request is never treated as a handled
    /// failure by the strategy this predicate is attached to.
    /// </summary>
    /// <typeparam name="TResult">The strategy's result type (<c>object</c> for exception-only strategies built via the non-generic <see cref="Polly.PredicateBuilder"/>).</typeparam>
    /// <param name="builder">The predicate builder to add the exclusion to.</param>
    /// <returns>The same <see cref="PredicateBuilder{TResult}"/>, for chaining.</returns>
    public static PredicateBuilder<TResult> ExcludingCancellation<TResult>(this PredicateBuilder<TResult> builder)
        => builder.Handle<Exception>(ex => ex is not OperationCanceledException);
}
