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
  `ObjectDisposedException` is likewise a rejection, not an unhandled crash (#134 — fails CLOSED,
  documented as the deliberate choice) - and, since #202, the cost delegate and `Acquire()` each have
  their own catch with a source-accurate message ("a dependency used by the permit-cost delegate has
  already been disposed" vs. "the rate limiter has already been disposed"), so a disposed scoped
  resource the cost delegate closed over is never misattributed to the limiter itself. Carries an
  `ownsLimiter` flag, defaulting to `false`: a caller-supplied BYO limiter is never disposed by this
  middleware, and (since #249) **neither is a limiter one of the three internally-created convenience
  entry points below created** — those pass `ownsLimiter: false` too, because the DI container owns
  that limiter's disposal now (see #133/#200/#249 below). `ownsLimiter: true` (and this type's
  `DisposeAsync` actually doing something) is reachable only for a caller who constructs this
  middleware directly, bypassing `UseXRateLimiting` entirely.
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

### #133/#200/#249 — who disposes the three internally-created limiters
`UseFixedWindowRateLimiting`/`UseTokenBucketRateLimiting`/`UsePayloadSizeRateLimiting` each create a
`RateLimiter` with `AutoReplenishment = true` (a live `Timer`) that something must eventually
dispose. Three acts:
- **#133 (round 11):** registered the limiter with the DI container via a factory registration
  (`x.AddSingleton<RateLimiter>(_ => rateLimiter)`) so the container's own singleton disposal would
  dispose it, plus a same-pipeline stacking guard (`IsTypeRegistered<RateLimiter>()`, throwing
  `InvalidOperationException` on a second internal call).
- **#200 (round 12–13) removed both.** `IBenzeneServiceContainer` registrations are shared by every
  sibling pipeline built off the same container — several transport pipelines sharing one container
  is this framework's supported multi-transport pattern (see `Benzene.Abstractions.Middleware/CLAUDE.md`'s
  `Create<TNewContext>()`) — so two independent `UseXRateLimiting` calls, even on entirely different
  pipelines, silently collided under that one shared `RateLimiter` DI key: whichever call registered
  last "won" resolution for every message on every affected pipeline, throttling an earlier
  pipeline's messages by a later pipeline's limiter. The stacking guard only detected the collision
  on one pipeline; it neither fixed the cross-pipeline case nor allowed the legitimate
  same-pipeline one. The fix captured the created limiter directly in the middleware factory's
  closure (`ownsLimiter: true`) instead of resolving it from DI — nothing registered with the
  container, so the collision became structurally impossible, and stacking multiple
  internally-created limiters (one pipeline or across siblings) became fully supported.
- **#249 (round 14–15): #200's disposal path was structurally unreachable.** `MiddlewarePipeline<TContext>.CreateChain`
  constructs a fresh middleware instance from the factory on **every message** and never retains or
  disposes one, and none of the three public `UseXRateLimiting` methods return any handle to the
  created limiter or middleware — so no caller using only the documented public API could ever reach
  `RateLimitingMiddleware<TContext>.DisposeAsync`. Every doc example's limiter leaked its `Timer` for
  the process's life, worse than pre-#200 in one respect (the collision-prone DI registration at
  least disposed on shutdown). **Fix: restore reachable disposal without reopening #200's
  collision.** The created limiter is still captured directly in the closure for USE
  (`ownsLimiter: false` now — the container owns disposal, not the middleware), so which limiter a
  call's messages are charged against stays exactly as collision-proof as #200 left it. Separately,
  the limiter is wrapped in a tiny `OwnedRateLimiter : IAsyncDisposable` and registered as a DI
  **factory** singleton: `x.AddSingleton<OwnedRateLimiter>(_ => owned)`. This is deliberately a
  factory registration, not a pre-built-instance one — exactly the distinction the pre-#200 code
  relied on: Microsoft.Extensions.DependencyInjection and Autofac both only container-dispose a
  singleton *they constructed* (a type or factory registration), never an already-built instance
  handed to `AddSingleton(instance)`, since the container didn't create it and can't assume it owns
  it. A factory singleton is only actually instantiated (and disposal-tracked) once something
  resolves it, so the middleware factory closure forces that once, on the first message it runs for
  (gated by a flag so it isn't paid per message): `resolver.GetServices<OwnedRateLimiter>()`. Verified
  against both DI adapters directly (see `Extensions.cs`'s `UseInternallyOwnedRateLimiting` XML doc
  for the full trace through `MicrosoftServiceResolverAdapter`/`AutofacServiceResolverAdapter`).
  Disposal ownership for an internally-created limiter now lives on the DI container again — the
  caller's ordinary shutdown path (disposing the root `IServiceProvider`/Autofac container) tears
  down the `Timer`, with no public-API reach-in required. `ownsLimiter: true` on
  `RateLimitingMiddleware<TContext>` (and its `DisposeAsync` actually doing something) is now
  reachable only for a caller that constructs the middleware directly, bypassing `UseXRateLimiting`.
  Combining limits into one `RateLimiter` and calling `UseRateLimiting`, or using
  `UsePartitionedRateLimiting`, remain available for a caller that wants one limiter object instead
  of several independent ones.

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

### #202 — the cost delegate and the limiter each get their own disposed-dependency message
`RateLimitingMiddlewareBase<TContext>.HandleAsync` used to run the cost delegate and `Acquire()`
inside one shared `try`/`catch (ObjectDisposedException)`, so a disposed dependency the cost delegate
itself relied on (e.g. a scoped resource resolved earlier in the pipeline) produced the exact same
"the rate limiter has already been disposed" message as the limiter's own disposal — misleading
whoever reads the rejection log/response into debugging the wrong thing. The cost delegate and
`Acquire()` now each have their own `try`/`catch`, so the message names which one actually threw
("a dependency used by the permit-cost delegate has already been disposed" vs. "the rate limiter has
already been disposed"). **Both still fail CLOSED** (#143/#134's decision is not reopened) — this is
a diagnostic-accuracy fix only, not a change to the reject-vs-allow outcome.

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
- Any number of internally-created (`UseFixedWindowRateLimiting`/`UseTokenBucketRateLimiting`/
  `UsePayloadSizeRateLimiting`) limiters are supported per pipeline, and across sibling pipelines
  sharing one `IBenzeneServiceContainer` (see #200 above) — each is captured directly in its own
  middleware's closure, so stacking them (or combining them with BYO limiters via `UseRateLimiting`/
  `UsePartitionedRateLimiting`) is always independent; there is no shared DI key any two calls could
  collide on.
- An internally-created limiter's disposal lives on the DI container (see #249 above), reachable
  through the caller's ordinary shutdown path — disposing the root `IServiceProvider`/Autofac
  container — with no reach into the pipeline builder or middleware required.

## Tests
- `test/Benzene.Core.Test/Plugins/RateLimiting/RateLimitingPipelineTest.cs` - pass-through under
  the limit, 429 + message over it (+ `Retry-After` header), payload-size budget spend +
  oversized-payload rejection + Content-Length pre-check, BYO concurrency limiter lease release, BYO
  cost function (+ negative-cost rejection, + throwing-delegate propagation), BYO limiter disposed
  before use fails closed (+ the #202 message naming the limiter, not the cost delegate), a disposed
  cost-delegate dependency failing closed with the #202 message naming the cost delegate, not the
  limiter, an internally-created limiter reachable via the PUBLIC API alone (`UseFixedWindowRateLimiting`,
  no `pipeline.GetItems()`) proven disposed when the DI container is disposed (#249 — the second
  message against the same closure-captured limiter fails CLOSED with the #202 disposed-limiter
  message) (+ the BYO mirror case proving disposing the middleware directly is a no-op for a
  caller-owned limiter), stacking two internal limiters on one pipeline (now legal - each enforces
  its own budget independently), two sibling pipelines sharing one container each calling
  `UseFixedWindowRateLimiting` independently (#200), partitioned limiter isolates one abusive
  partition from another.
