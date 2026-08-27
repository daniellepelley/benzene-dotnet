using System.Globalization;
using System.Threading.RateLimiting;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.RateLimiting;

/// <summary>
/// Shared plumbing for <see cref="RateLimitingMiddleware{TContext}"/> and
/// <see cref="PartitionedRateLimitingMiddleware{TContext}"/>: computes and validates the permit
/// cost, acquires via the concrete subclass's limiter, and writes the (possibly logged, possibly
/// <c>Retry-After</c>-bearing) rejection when it isn't granted. Concrete subclasses differ only in
/// how a lease is acquired and how the limiter describes itself for logging.
/// </summary>
/// <typeparam name="TContext">The transport-specific context type.</typeparam>
public abstract class RateLimitingMiddlewareBase<TContext> : IMiddleware<TContext> where TContext : class
{
    private readonly Func<IServiceResolver, TContext, int> _permitCost;
    private readonly ILogger? _logger;

    /// <summary>The current message's scope, used to compute the cost and write the rejection result.</summary>
    protected readonly IServiceResolver ServiceResolver;

    /// <summary>Initializes the shared plumbing over a per-message permit cost and the current scope.</summary>
    /// <param name="permitCost">Computes the permit cost of the current message.</param>
    /// <param name="serviceResolver">The current message's scope.</param>
    /// <param name="logger">Optional; logs a warning when a message is rejected (see <see cref="LimiterDescription"/>).</param>
    protected RateLimitingMiddlewareBase(Func<IServiceResolver, TContext, int> permitCost,
        IServiceResolver serviceResolver, ILogger? logger)
    {
        _permitCost = permitCost;
        ServiceResolver = serviceResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>Attempts to acquire <paramref name="cost"/> permits for <paramref name="context"/>.</summary>
    /// <param name="context">The current message's context.</param>
    /// <param name="cost">The already-validated (non-negative) permit cost.</param>
    /// <returns>The acquired lease (which may report <c>IsAcquired == false</c>).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The cost exceeds what the limiter could ever grant (e.g. a payload bigger than the whole
    /// bucket) - treated as a rejection, not an internal error.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The limiter has already been disposed.</exception>
    protected abstract RateLimitLease Acquire(TContext context, int cost);

    /// <summary>A short, log-friendly description of the limiter (and partition key, if any) that rejected the message.</summary>
    protected abstract string LimiterDescription(TContext context);

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        RateLimitLease? lease = null;
        string? rejectionDetail = null;
        var cost = 0;
        try
        {
            try
            {
                // The cost delegate runs inside this try (not before it) so a delegate that throws -
                // deliberately (e.g. signalling "reject this") or by bug - is handled the same way an
                // out-of-range cost from AttemptAcquire itself is, rather than escaping unhandled and
                // bypassing the limiter entirely.
                cost = _permitCost(ServiceResolver, context);
                if (cost < 0)
                {
                    // A negative cost is a caller bug in the cost delegate, not a valid "free" message -
                    // silently clamping it to 0 (as this used to do) would let it always succeed and hide
                    // the bug. Raising the same exception AttemptAcquire itself would throw for an
                    // out-of-range cost routes it through the identical, already-correct rejection path
                    // below instead of adding a second one.
                    throw new ArgumentOutOfRangeException(nameof(cost), cost,
                        "The permit cost delegate returned a negative value.");
                }

                lease = Acquire(context, cost);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectionDetail =
                    "Rate limit exceeded: the message's cost is invalid, or exceeds the limiter's capacity and can never be granted";
            }
            catch (ObjectDisposedException)
            {
                // #134: a caller-disposed BYO limiter must not crash every subsequent message with an
                // unhandled ObjectDisposedException. Fail CLOSED - the same 429-style rejection as any
                // other denial - rather than failing open (which would silently turn off the protection
                // this middleware exists to provide the moment the limiter is disposed).
                rejectionDetail = "Rate limit exceeded: the rate limiter has already been disposed";
            }

            if (lease is not { IsAcquired: true })
            {
                await RejectAsync(context, lease, cost, rejectionDetail);
                return;
            }

            await next();
        }
        finally
        {
            // Held across next() so a concurrency-style limiter's permits are returned when the
            // message completes; a no-op for window/bucket limiters.
            lease?.Dispose();
        }
    }

    private Task RejectAsync(TContext context, RateLimitLease? lease, int cost, string? detail)
    {
        var error = detail ?? "Rate limit exceeded";
        if (lease != null && lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            error = $"{error}; retry after {retryAfter.TotalSeconds:0}s";
            SetRetryAfterHeader(context, retryAfter);
        }

        _logger?.LogWarning("Rate limit rejected a message (limiter {LimiterDescription}, cost {Cost}): {Detail}",
            LimiterDescription(context), cost, error);

        // Attach the topic's handler definition so the response pipeline writes the problem-details
        // body (it skips definition-less results) - same pattern as Benzene.JsonSchema.
        var topicGetter = ServiceResolver.TryGetService<IMessageTopicGetter<TContext>>();
        var topic = topicGetter?.GetTopic(context);
        var definition = topic != null
            ? ServiceResolver.TryGetService<IMessageHandlerDefinitionLookUp>()?.FindHandler(topic)
            : null;

        var resultSetter = ServiceResolver.GetService<IMessageHandlerResultSetter<TContext>>();
        return resultSetter.SetResultAsync(context,
            new MessageHandlerResult(topic, definition, BenzeneResult.TooManyRequests(error)));
    }

    /// <summary>
    /// Sets the standard <c>Retry-After</c> response header from the lease's metadata, when the
    /// current transport exposes a response adapter (best-effort - not every transport writes an
    /// HTTP-shaped response, and not every limiter supplies the metadata:
    /// <see cref="SlidingWindowRateLimiter"/> does not).
    /// </summary>
    private void SetRetryAfterHeader(TContext context, TimeSpan retryAfter)
    {
        var responseAdapter = ServiceResolver.TryGetService<IBenzeneResponseAdapter<TContext>>();
        if (responseAdapter == null)
        {
            return;
        }

        var seconds = Math.Max(0, (int)Math.Ceiling(retryAfter.TotalSeconds));
        responseAdapter.SetResponseHeader(context, "Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Best-effort, per-instance rate limiting over any <see cref="RateLimiter"/>
/// (System.Threading.RateLimiting). Each message attempts to acquire its permit cost (1 by
/// default; e.g. the payload's byte size for a bytes-per-second token bucket) without queuing;
/// a message the limiter rejects is short-circuited with a <c>TooManyRequests</c> result (HTTP
/// 429 via the standard status mapping). The acquired lease is held across <c>next()</c> so
/// concurrency-style limiters release correctly.
/// </summary>
/// <remarks>
/// This is deliberately simple protection for endpoints a service can't avoid exposing (health
/// checks, spec) — a brake on abuse and runaway serverless cost, not an exact science: the limit
/// is per service instance, so a fleet of N instances admits up to N× the configured rate.
/// Authoritative rate limiting belongs at the gateway in front of all instances. Every caller shares
/// one limiter — one abusive caller can exhaust the whole budget for every other caller; use
/// <see cref="PartitionedRateLimitingMiddleware{TContext}"/> (<c>UsePartitionedRateLimiting</c>) to
/// give each caller (by IP, API key, tenant, ...) its own share instead.
/// </remarks>
/// <typeparam name="TContext">The transport-specific context type.</typeparam>
public class RateLimitingMiddleware<TContext> : RateLimitingMiddlewareBase<TContext>, IAsyncDisposable
    where TContext : class
{
    private readonly RateLimiter _rateLimiter;
    private readonly bool _ownsLimiter;

    /// <summary>Initializes the middleware over a shared limiter and a per-message permit cost.</summary>
    /// <param name="rateLimiter">The limiter, shared across every message on the pipeline (and any other pipeline given the same instance).</param>
    /// <param name="permitCost">Computes the permit cost of the current message (e.g. 1, or the payload size in bytes).</param>
    /// <param name="serviceResolver">The current message's scope, used to compute the cost and write the rejection result.</param>
    /// <param name="ownsLimiter">
    /// Whether this middleware owns <paramref name="rateLimiter"/>'s disposal. <c>false</c> (the
    /// default) for a caller-supplied (bring-your-own) limiter — its disposal always belongs to the
    /// caller, never to this middleware, so a shared BYO limiter is never disposed out from under
    /// another consumer of it. <c>true</c> only for a limiter this package created on the caller's
    /// behalf (the <c>UseFixedWindowRateLimiting</c>/<c>UseTokenBucketRateLimiting</c>/
    /// <c>UsePayloadSizeRateLimiting</c> convenience entry points), where nothing else could ever
    /// dispose it otherwise (see #133 in <c>work/outstanding-bugs.md</c>).
    /// </param>
    /// <param name="logger">Optional; logs a warning naming the limiter and cost when a message is rejected.</param>
    public RateLimitingMiddleware(RateLimiter rateLimiter, Func<IServiceResolver, TContext, int> permitCost,
        IServiceResolver serviceResolver, bool ownsLimiter = false,
        ILogger<RateLimitingMiddleware<TContext>>? logger = null)
        : base(permitCost, serviceResolver, logger)
    {
        _rateLimiter = rateLimiter;
        _ownsLimiter = ownsLimiter;
    }

    /// <inheritdoc />
    public override string Name => "RateLimiting";

    /// <inheritdoc />
    protected override RateLimitLease Acquire(TContext context, int cost) => _rateLimiter.AttemptAcquire(cost);

    /// <inheritdoc />
    protected override string LimiterDescription(TContext context) => _rateLimiter.GetType().Name;

    /// <summary>
    /// Disposes the limiter this middleware owns (<see cref="_ownsLimiter"/>); a no-op for a
    /// caller-supplied limiter, which the caller always owns. Nothing in the pipeline calls this
    /// automatically today — the built-in <c>UseXRateLimiting</c> entry points instead register the
    /// limiter with the DI container (see <c>Extensions.cs</c>), so it is disposed when the
    /// container itself is, the same convention this codebase already uses for other
    /// container-created disposables. This member exists so a caller that manages a
    /// <see cref="RateLimitingMiddleware{TContext}"/> instance's lifetime directly gets correct
    /// disposal semantics too, and so ownership is explicit at the type level.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_ownsLimiter)
        {
            await _rateLimiter.DisposeAsync();
        }
    }
}
