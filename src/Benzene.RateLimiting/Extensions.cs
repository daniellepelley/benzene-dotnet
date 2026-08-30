using System.Text;
using System.Threading.RateLimiting;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.RateLimiting;

/// <summary>
/// Pipeline extensions for best-effort, per-instance rate limiting. Place the call <b>before</b>
/// the middleware it should protect (e.g. before <c>UseHealthCheck</c>/<c>UseSpec</c>/
/// <c>UseMessageHandlers</c>). The limit is per service instance — authoritative limiting belongs
/// at the gateway; see docs/rate-limiting.md.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Rate-limits the pipeline with a caller-supplied (bring-your-own) <see cref="RateLimiter"/>,
    /// costing one permit per message. The limiter instance is shared for the pipeline's lifetime —
    /// the caller owns its disposal (for a process-lifetime pipeline that is process exit).
    /// </summary>
    /// <typeparam name="TContext">The pipeline's context type.</typeparam>
    /// <param name="app">The pipeline builder.</param>
    /// <param name="rateLimiter">Any limiter: fixed/sliding window, token bucket, concurrency, or custom.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseRateLimiting<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, RateLimiter rateLimiter)
        where TContext : class
    {
        return app.UseRateLimiting(rateLimiter, (_, _) => 1);
    }

    /// <summary>
    /// Rate-limits the pipeline with a caller-supplied <see cref="RateLimiter"/> and a
    /// caller-supplied per-message permit cost (e.g. a payload-derived weight).
    /// </summary>
    /// <typeparam name="TContext">The pipeline's context type.</typeparam>
    /// <param name="app">The pipeline builder.</param>
    /// <param name="rateLimiter">Any limiter; shared for the pipeline's lifetime, disposal owned by the caller.</param>
    /// <param name="permitCost">Computes the current message's permit cost from the message scope and context.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseRateLimiting<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, RateLimiter rateLimiter,
        Func<IServiceResolver, TContext, int> permitCost)
        where TContext : class
    {
        return app.Use(resolver => new RateLimitingMiddleware<TContext>(rateLimiter, permitCost, resolver,
            ownsLimiter: false,
            logger: resolver.TryGetService<ILogger<RateLimitingMiddleware<TContext>>>()));
    }

    /// <summary>
    /// Rate-limits the pipeline <b>per partition</b> — each caller draws from its own share of
    /// permits instead of every caller sharing one limiter (see
    /// <see cref="PartitionedRateLimitingMiddleware{TContext}"/> for the full trade-off, including
    /// the "a client-supplied key is spoofable" honesty note) — costing one permit per message.
    /// The partition key is whatever <paramref name="partitionedLimiter"/>'s own partitioner
    /// derives from the message's <typeparamref name="TContext"/>; the limiter instance is shared
    /// for the pipeline's lifetime and its disposal is owned by the caller.
    /// </summary>
    /// <typeparam name="TContext">The pipeline's context type, also the limiter's partition resource type.</typeparam>
    /// <param name="app">The pipeline builder.</param>
    /// <param name="partitionedLimiter">
    /// A limiter built via <see cref="PartitionedRateLimiter.Create{TResource,TKey}"/> with
    /// <typeparamref name="TContext"/> as the resource type, e.g.
    /// <c>PartitionedRateLimiter.Create&lt;TContext, string&gt;(context =&gt;
    /// RateLimitPartition.GetTokenBucketLimiter(KeyOf(context), _ =&gt; new TokenBucketRateLimiterOptions { ... }))</c>.
    /// </param>
    /// <param name="partitionKeyForLogging">Optional; see the middleware's constructor for why this is separate from the limiter itself.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UsePartitionedRateLimiting<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, PartitionedRateLimiter<TContext> partitionedLimiter,
        Func<TContext, string?>? partitionKeyForLogging = null)
        where TContext : class
    {
        return app.UsePartitionedRateLimiting(partitionedLimiter, (_, _) => 1, partitionKeyForLogging);
    }

    /// <summary>
    /// Rate-limits the pipeline <b>per partition</b> with a caller-supplied per-message permit cost.
    /// See the single-cost overload and <see cref="PartitionedRateLimitingMiddleware{TContext}"/>.
    /// </summary>
    /// <typeparam name="TContext">The pipeline's context type, also the limiter's partition resource type.</typeparam>
    /// <param name="app">The pipeline builder.</param>
    /// <param name="partitionedLimiter">A limiter built via <see cref="PartitionedRateLimiter.Create{TResource,TKey}"/> with <typeparamref name="TContext"/> as the resource type.</param>
    /// <param name="permitCost">Computes the current message's permit cost from the message scope and context.</param>
    /// <param name="partitionKeyForLogging">Optional; see the middleware's constructor for why this is separate from the limiter itself.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UsePartitionedRateLimiting<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, PartitionedRateLimiter<TContext> partitionedLimiter,
        Func<IServiceResolver, TContext, int> permitCost, Func<TContext, string?>? partitionKeyForLogging = null)
        where TContext : class
    {
        return app.Use(resolver => new PartitionedRateLimitingMiddleware<TContext>(
            partitionedLimiter, permitCost, resolver, partitionKeyForLogging,
            ownsLimiter: false,
            logger: resolver.TryGetService<ILogger<PartitionedRateLimitingMiddleware<TContext>>>()));
    }

    /// <summary>
    /// Rate-limits to at most <paramref name="permitLimit"/> messages per <paramref name="window"/>
    /// (a <see cref="FixedWindowRateLimiter"/>; no queuing — excess messages get
    /// <c>TooManyRequests</c> immediately). The simple guard for utility endpoints like health checks.
    /// </summary>
    /// <typeparam name="TContext">The pipeline's context type.</typeparam>
    /// <param name="app">The pipeline builder.</param>
    /// <param name="permitLimit">Messages allowed per window.</param>
    /// <param name="window">The window length.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseFixedWindowRateLimiting<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, int permitLimit, TimeSpan window)
        where TContext : class
    {
        return app.UseInternallyOwnedRateLimiting(new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        }), static (_, _) => 1);
    }

    /// <summary>
    /// Rate-limits messages through a <see cref="TokenBucketRateLimiter"/>: bursts up to
    /// <paramref name="tokenLimit"/>, refilled with <paramref name="tokensPerPeriod"/> every
    /// <paramref name="replenishmentPeriod"/>. One token per message; no queuing.
    /// </summary>
    /// <typeparam name="TContext">The pipeline's context type.</typeparam>
    /// <param name="app">The pipeline builder.</param>
    /// <param name="tokenLimit">The bucket size (maximum burst).</param>
    /// <param name="tokensPerPeriod">Tokens restored each period.</param>
    /// <param name="replenishmentPeriod">How often tokens are restored.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseTokenBucketRateLimiting<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, int tokenLimit, int tokensPerPeriod,
        TimeSpan replenishmentPeriod)
        where TContext : class
    {
        return app.UseInternallyOwnedRateLimiting(
            CreateTokenBucket(tokenLimit, tokensPerPeriod, replenishmentPeriod), static (_, _) => 1);
    }

    /// <summary>
    /// Rate-limits by <b>payload size</b>: a token bucket where each message costs its request
    /// body's size in UTF-8 bytes (a bodyless message costs 1), allowing up to
    /// <paramref name="bytesPerPeriod"/> bytes every <paramref name="replenishmentPeriod"/> with
    /// bursts up to <paramref name="maxBurstBytes"/>. A single payload larger than
    /// <paramref name="maxBurstBytes"/> is always rejected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a rate bound, not a memory bound — read this before relying on it to cap memory.</b>
    /// On ASP.NET Core hosts, Benzene's own <c>UseBufferedRequestBody()</c> reads the request body
    /// into memory unconditionally, before any message-pipeline middleware (this one included) runs
    /// — so by the time this middleware's cost delegate sees the body, the allocation this middleware
    /// exists to bound has already happened. What this middleware bounds is the <em>rate</em> of
    /// oversized/many payloads reaching the handler and further downstream work, not the peak memory
    /// a single oversized request costs the process.
    /// </para>
    /// <para>
    /// This does include one cheap mitigation: when the transport exposes a <c>Content-Length</c>
    /// header (via <see cref="IMessageHeadersGetter{TContext}"/>) and it already exceeds
    /// <paramref name="maxBurstBytes"/>, the cost delegate rejects on that declared size directly,
    /// without reading/measuring the (already-buffered) body at all. That is strictly a CPU saving
    /// (skips the full-body UTF-8 byte count) on this middleware's own side of the pipeline, not a
    /// memory saving — and it does nothing for a request with no <c>Content-Length</c> (chunked
    /// transfer, or a transport that doesn't set one), which still gets fully buffered upstream
    /// before this middleware ever runs.
    /// </para>
    /// <para>
    /// A genuine memory bound needs either an async, stream-aware cost delegate evaluated
    /// <em>before</em> buffering (a larger redesign of this middleware and of
    /// <c>UseBufferedRequestBody</c>'s unconditional placement, out of scope for this fix), or a
    /// host-level cap upstream of Benzene entirely (e.g. Kestrel's own
    /// <c>MaxRequestBodySize</c>/<c>IHttpMaxRequestBodySizeFeature</c>, or a gateway body-size limit)
    /// — put one of those in front of any endpoint where payload size is a real memory-exhaustion
    /// concern. See #135 in <c>work/outstanding-bugs.md</c> for the full trade-off discussion.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The pipeline's context type.</typeparam>
    /// <param name="app">The pipeline builder.</param>
    /// <param name="maxBurstBytes">The bucket size — the most bytes admissible at once.</param>
    /// <param name="bytesPerPeriod">Bytes restored each period (the sustained rate).</param>
    /// <param name="replenishmentPeriod">How often the byte budget is restored.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UsePayloadSizeRateLimiting<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, int maxBurstBytes, int bytesPerPeriod,
        TimeSpan replenishmentPeriod)
        where TContext : class
    {
        return app.UseInternallyOwnedRateLimiting(
            CreateTokenBucket(maxBurstBytes, bytesPerPeriod, replenishmentPeriod),
            (resolver, context) =>
            {
                var declaredLength = TryGetDeclaredContentLength(resolver, context);
                if (declaredLength.HasValue && declaredLength.Value > maxBurstBytes)
                {
                    return declaredLength.Value;
                }

                var body = resolver.TryGetService<IMessageBodyGetter<TContext>>()?.GetBody(context);
                return string.IsNullOrEmpty(body) ? 1 : Encoding.UTF8.GetByteCount(body);
            });
    }

    /// <summary>
    /// Reads a <c>Content-Length</c> header via <see cref="IMessageHeadersGetter{TContext}"/>, when
    /// the transport registers one and the request carries the header. Best-effort and read-only —
    /// see <see cref="UsePayloadSizeRateLimiting{TContext}"/>'s remarks for what this can and can't
    /// protect against.
    /// </summary>
    private static int? TryGetDeclaredContentLength<TContext>(IServiceResolver resolver, TContext context)
    {
        var headers = resolver.TryGetService<IMessageHeadersGetter<TContext>>()?.GetHeaders(context);
        if (headers == null)
        {
            return null;
        }

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(header.Value, out var length))
            {
                return length;
            }
        }

        return null;
    }

    /// <summary>
    /// Wires an internally-created limiter (one of the <c>UseXRateLimiting</c> convenience entry
    /// points, never a caller-supplied BYO one) so the DI container owns its disposal.
    /// </summary>
    /// <remarks>
    /// The limiter is registered with the container via a <em>factory</em> registration (not a
    /// pre-built instance) — the same convention this codebase already relies on for other
    /// container-created disposables (e.g. <c>RabbitMqConnectionProvider</c>, <c>MeshAnnouncer</c>):
    /// a compliant DI container disposes a singleton <em>it</em> constructed (via a type or factory
    /// registration) when the container itself is disposed, but never disposes a pre-built instance
    /// handed to it — that convention is exactly the caller-owns-BYO / container-owns-internal split
    /// this fixes #133 with. The registered limiter is then resolved (not re-created — it's a
    /// singleton) once per message, which is what makes the container actually construct-and-track
    /// it for disposal the first time any message flows through.
    /// <para>
    /// Only ONE internally-created limiter is supported <b>per pipeline</b>: stacking two
    /// <c>UseXRateLimiting</c> calls on the same pipeline builder would otherwise silently let the
    /// second shadow the first, so this fails fast with a clear exception instead. A caller needing
    /// more than one layer of protection should combine the limits into one <see cref="RateLimiter"/>
    /// and use <c>UseRateLimiting</c>, or use <c>UsePartitionedRateLimiting</c>.
    /// </para>
    /// <para>
    /// #200: the registration (and the guard above) is keyed on
    /// <see cref="InternallyOwnedRateLimiterHolder{TContext}"/> — closed over <typeparamref name="TContext"/>,
    /// not on the bare <see cref="RateLimiter"/> type — because
    /// <see cref="IMiddlewarePipelineBuilder{TContext}.Create{TNewContext}"/> deliberately shares one
    /// container across a service's sibling pipelines for different transports (e.g. the AwsMesh
    /// examples' API Gateway + BenzeneMessage + SQS + SNS + EventBridge pipelines, each its own
    /// context type, off one <c>IBenzeneApplicationBuilder</c>). Keying on the bare <c>RateLimiter</c>
    /// type made every sibling pipeline's internally-created limiter collide on the same DI
    /// registration: the guard above tripped on the second sibling even though it is a genuinely
    /// different pipeline, and (had the guard been bypassed) the two limiters would have shadowed
    /// each other under one ambient registration. <typeparamref name="TContext"/> is what already
    /// distinguishes sibling pipelines at registration time in this codebase (see
    /// <c>MiddlewarePipelineBuilder{TContext}.Build()</c>'s own <c>PipelineDescriptor</c>, keyed the
    /// same way) — two calls sharing one pipeline builder (and so the same <typeparamref name="TContext"/>)
    /// still collide on the same key, so double-registration <b>within</b> one pipeline still fails
    /// fast exactly as before.
    /// </para>
    /// </remarks>
    private static IMiddlewarePipelineBuilder<TContext> UseInternallyOwnedRateLimiting<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, RateLimiter rateLimiter,
        Func<IServiceResolver, TContext, int> permitCost)
        where TContext : class
    {
        app.Register(x =>
        {
            if (x.IsTypeRegistered<InternallyOwnedRateLimiterHolder<TContext>>())
            {
                throw new InvalidOperationException(
                    "Only one internally-created rate limiter (UseFixedWindowRateLimiting / " +
                    "UseTokenBucketRateLimiting / UsePayloadSizeRateLimiting) is supported per pipeline. " +
                    "Combine your limits into one RateLimiter and call UseRateLimiting(RateLimiter, ...) " +
                    "instead, or use UsePartitionedRateLimiting.");
            }

            x.AddSingleton<InternallyOwnedRateLimiterHolder<TContext>>(_ => new InternallyOwnedRateLimiterHolder<TContext>(rateLimiter));
        });

        return app.Use(resolver => new RateLimitingMiddleware<TContext>(
            resolver.GetService<InternallyOwnedRateLimiterHolder<TContext>>().RateLimiter, permitCost, resolver,
            ownsLimiter: true,
            logger: resolver.TryGetService<ILogger<RateLimitingMiddleware<TContext>>>()));
    }

    /// <summary>
    /// Wraps an internally-created <see cref="RateLimiter"/> under a DI key unique to the pipeline
    /// that created it (its <typeparamref name="TContext"/> — see the #200 remarks on
    /// <see cref="UseInternallyOwnedRateLimiting{TContext}"/>), so sibling pipelines sharing one
    /// container each get their own container-owned limiter instead of colliding on a single ambient
    /// registration. Still a container-owned factory singleton — implementing
    /// <see cref="IAsyncDisposable"/> here (forwarding to the wrapped limiter) is what keeps disposal
    /// working through this extra layer of indirection: the container only disposes what it resolved
    /// (this holder), not fields buried inside it, so #133's fix (the limiter must be disposed when
    /// the container is) would otherwise silently stop working the moment the registration is wrapped.
    /// Also implements <see cref="IDisposable"/>: a synchronous container disposal (e.g. the Microsoft
    /// DI adapter's <c>ServiceProviderEngineScope.Dispose()</c>) throws if a resolved singleton implements
    /// only <see cref="IAsyncDisposable"/>, so the sync path bridges to the async one, matching the same
    /// pattern used by <c>MeshAnnouncer.Dispose()</c>.
    /// </summary>
    internal sealed class InternallyOwnedRateLimiterHolder<TContext> : IAsyncDisposable, IDisposable
    {
        public RateLimiter RateLimiter { get; }

        public InternallyOwnedRateLimiterHolder(RateLimiter rateLimiter)
        {
            RateLimiter = rateLimiter;
        }

        public ValueTask DisposeAsync() => RateLimiter.DisposeAsync();

        public void Dispose()
        {
            try
            {
                DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Best-effort: the underlying RateLimiter's DisposeAsync is expected to complete
                // promptly and without faulting under normal disposal.
            }
        }
    }

    private static TokenBucketRateLimiter CreateTokenBucket(int tokenLimit, int tokensPerPeriod,
        TimeSpan replenishmentPeriod)
    {
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = replenishmentPeriod,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}
