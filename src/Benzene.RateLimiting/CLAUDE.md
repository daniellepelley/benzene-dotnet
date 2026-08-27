# Benzene.RateLimiting

## What this package does
Best-effort, **per-instance** rate limiting as pipeline middleware, built directly on
`System.Threading.RateLimiting`'s abstract `RateLimiter`. Its purpose is protection, not traffic
shaping: endpoints a service can't avoid exposing publicly (health checks, spec) shouldn't be a
free denial-of-service or serverless-cost-amplification vector. A rejected message short-circuits
with a `TooManyRequests` result (→ HTTP 429 via the standard status mapping).

**Honesty rule (keep in every doc):** the limit is per service instance — a fleet of N instances
admits up to N× the configured rate, and serverless scale-out multiplies it further. Authoritative
rate limiting belongs at the gateway (API Gateway, APIM, ingress) in front of all instances. This
package documents that loudly (`docs/rate-limiting.md`); never present it as a hard guarantee.

## Key types
- `RateLimitingMiddlewareBase<TContext>` - shared plumbing for the two concrete middlewares below:
  computes and validates the permit cost (throws `ArgumentOutOfRangeException` for a negative cost
  rather than clamping it — a clamp used to hide a caller bug in the cost delegate, see #143),
  invokes the cost delegate *inside* the same try/catch as the acquire call (so a delegate that
  throws is handled consistently, not left to bypass the limiter unhandled), sets the `Retry-After`
  response header from the lease's metadata when present (via `IBenzeneResponseAdapter<TContext>`,
  best-effort — not every transport has one, and `SlidingWindowRateLimiter` never supplies the
  metadata), logs a warning on rejection via an optional `ILogger`, and writes the `TooManyRequests`
  result (same problem-details attachment pattern as `Benzene.JsonSchema`).
- `RateLimitingMiddleware<TContext> : RateLimitingMiddlewareBase<TContext>, IAsyncDisposable` (Name
  `"RateLimiting"`) - attempts `RateLimiter.AttemptAcquire(cost)` (never queues; protection wants
  immediate rejection). The acquired lease is disposed **after** `next()`, so a concurrency-style
  limiter's permits release correctly (a no-op for window/bucket limiters). A cost the limiter could
  never grant (`ArgumentOutOfRangeException`, e.g. a payload bigger than the whole bucket) is a
  rejection with a distinguishing message, not a bare "Rate limit exceeded" (#142); an
  `ObjectDisposedException` from an already-disposed BYO limiter is likewise a rejection, not an
  unhandled crash (#134 — fails CLOSED, documented as the deliberate choice). Carries an
  `ownsLimiter` flag: `true` only for the three internally-created convenience limiters below, whose
  disposal this middleware type is capable of (via `DisposeAsync`) — though in practice the DI
  registration in `Extensions.cs` is what actually disposes them (see below); `false` (the default)
  for every BYO limiter, which this middleware never disposes.
- `PartitionedRateLimitingMiddleware<TContext> : RateLimitingMiddlewareBase<TContext>,
  IAsyncDisposable` (Name `"PartitionedRateLimiting"`) - the same behaviour over a caller-supplied
  `PartitionedRateLimiter<TContext>` (#136): calls `AttemptAcquire(context, cost)`, letting the
  partitioner baked into the limiter (via `PartitionedRateLimiter.Create<TContext,TKey>`) key on
  whatever the caller derives from the message (IP, API key, tenant, ...). Always BYO — there is no
  built-in convenience entry point, since the partition key is inherently caller-specific — so
  `ownsLimiter` defaults to `false`. Takes an optional `Func<TContext,string?>` purely so a
  rejection's log line can name the partition (the limiter itself doesn't expose the key it
  derived).
- `Extensions` - pipeline entry points; call **before** the middleware to protect
  (`UseHealthCheck`/`UseSpec`/`UseMessageHandlers`):
  - `UseRateLimiting(RateLimiter)` / `UseRateLimiting(RateLimiter, Func<IServiceResolver, TContext,
    int>)` - bring-your-own limiter (fixed/sliding window, token bucket, concurrency, custom), 1
    permit per message by default. The caller owns the limiter's lifetime/disposal; this package
    never registers a BYO limiter with DI and never disposes it.
  - `UsePartitionedRateLimiting(PartitionedRateLimiter<TContext>, ...)` - bring-your-own partitioned
    limiter; see the middleware above. **Honesty note (keep wherever this is documented):** a
    client-supplied partition key is spoofable, but strictly better than the one-shared-limiter
    status quo, where a single caller (accidental or malicious) can starve every other caller with
    zero effort.
  - `UseFixedWindowRateLimiting(permitLimit, window)` - N messages per window.
  - `UseTokenBucketRateLimiting(tokenLimit, tokensPerPeriod, replenishmentPeriod)` - smoothed
    message rate with bursts.
  - `UsePayloadSizeRateLimiting(maxBurstBytes, bytesPerPeriod, replenishmentPeriod)` - token
    bucket where each message costs its body's UTF-8 byte size (bodyless costs 1): a
    bytes-per-second budget. A single payload larger than `maxBurstBytes` is always rejected. When
    the transport reports a `Content-Length` header over `maxBurstBytes`, that declared size is used
    as the cost directly (skips reading/measuring the body) — a CPU saving on this middleware's own
    side, **not** a memory bound: see #135 below and the type's XML doc for the full trade-off.

### #133 — who disposes the three internally-created limiters
`UseFixedWindowRateLimiting`/`UseTokenBucketRateLimiting`/`UsePayloadSizeRateLimiting` each create a
`RateLimiter` with `AutoReplenishment = true` (a live `Timer`). Nothing in the pipeline itself calls
`Dispose` on a middleware instance (a fresh `RateLimitingMiddleware<TContext>` wrapper is
constructed per message — see `MiddlewarePipeline<TContext>` — but the underlying `RateLimiter` is
one shared instance), so a middleware-level `ownsLimiter` flag alone cannot be *the* fix: whichever
per-message instance ran `Dispose` first would break every later message. The actual fix registers
the limiter with the DI container via a **factory** registration
(`x.AddSingleton<RateLimiter>(_ => rateLimiter)`, not a pre-built instance) — the same convention
this codebase already relies on for other container-created disposables (`RabbitMqConnectionProvider`,
`MeshAnnouncer`): a compliant container disposes a singleton it constructed itself when the
container is disposed, but never disposes an externally-supplied instance. `UseInternallyOwnedRateLimiting`
(private, in `Extensions.cs`) also guards against two internal `UseX...` calls stacking on one
pipeline (which would otherwise let the second silently shadow the first under the shared
`RateLimiter` DI key) — it throws `InvalidOperationException` instead. Combine limits into one
`RateLimiter` and call `UseRateLimiting`, or use `UsePartitionedRateLimiting`, if more than one
layer is genuinely needed.

### #135 — payload-size limiting is a rate bound, not a memory bound
On ASP.NET Core hosts, `UseBufferedRequestBody()` reads the whole request body into memory
**unconditionally, before any message-pipeline middleware runs** (`BenzeneExtensions.cs` in
`Benzene.AspNet.Core`, outside this package). By the time `UsePayloadSizeRateLimiting`'s cost
delegate runs, the allocation it exists to bound has already happened — it can still reject before
the handler and downstream work run, but not before the buffering. The `Content-Length` pre-check
above is a real, cheap partial mitigation (skips a full-body UTF-8 byte count for the common case of
an honest, oversized `Content-Length`) but does **not** close this gap, and does nothing for a
`Content-Length`-less streamed body. A genuine memory bound needs either an async, stream-aware cost
delegate evaluated before buffering (a larger redesign, out of scope for this fix — it would also
mean revisiting `UseBufferedRequestBody`'s unconditional placement, which is `Benzene.AspNet.Core`'s
concern, not this package's) or a host-level cap in front of Benzene entirely (Kestrel's
`MaxRequestBodySize`, a gateway body-size limit). Document this residual gap wherever the middleware
is documented; do not present it as a memory bound.

## Dependencies
- `Benzene.Abstractions.Pipelines`, `Benzene.Core.MessageHandlers`, `Benzene.Core.Middleware`.
- NuGet: **System.Threading.RateLimiting** (the abstraction this package is deliberately shaped
  around, per the design request - BYO means any of its limiters plug in).
- `Microsoft.Extensions.Logging.Abstractions` reaches this package transitively (via
  `Benzene.Abstractions`) for the optional `ILogger` dependency - no direct `PackageReference` added,
  matching `Benzene.Mesh.Artifacts`'s convention.

## Important conventions
- The limiter instance is shared across every message on the pipeline (and across pipelines if the
  caller passes the same instance) - that's the point; costs/permits are process-wide.
- No queuing (`QueueLimit = 0` on all built-ins): a protective limiter that queues just moves the
  resource exhaustion into memory.
- Rejection status is `BenzeneResultStatus.TooManyRequests` (already in the status vocabulary,
  mapped to HTTP 429 by `DefaultHttpStatusCodeMapper`).
- Only ONE internally-created (`UseFixedWindowRateLimiting`/`UseTokenBucketRateLimiting`/
  `UsePayloadSizeRateLimiting`) limiter is supported per pipeline (see #133 above) — BYO limiters
  (`UseRateLimiting`, `UsePartitionedRateLimiting`) are unaffected and can be combined freely with
  each other or with one internal limiter.

## Tests
- `test/Benzene.Core.Test/Plugins/RateLimiting/RateLimitingPipelineTest.cs` - pass-through under
  the limit, 429 + message over it (+ `Retry-After` header), payload-size budget spend +
  oversized-payload rejection + Content-Length pre-check, BYO concurrency limiter lease release, BYO
  cost function (+ negative-cost rejection, + throwing-delegate propagation), BYO limiter disposed
  before use fails closed, internally-created limiter disposed with the container, stacking two
  internal limiters fails fast, partitioned limiter isolates one abusive partition from another.
