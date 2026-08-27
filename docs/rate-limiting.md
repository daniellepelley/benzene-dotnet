# Rate Limiting

`Benzene.RateLimiting` adds best-effort request rate limiting to any Benzene pipeline, built on
.NET's standard [`System.Threading.RateLimiting`](https://learn.microsoft.com/dotnet/api/system.threading.ratelimiting)
abstractions. Its job is **protection**: endpoints a service can't avoid exposing publicly — health
checks, the [spec endpoint](spec.md) — should not be a free denial-of-service vector, or, on
serverless plans that scale on demand, a way for a stranger to run up your bill.

## Read this first: it limits per instance

The limiter lives inside one service instance. Run three instances behind a load balancer and you
admit up to **3× the configured rate**; let a serverless platform scale out under load and the
multiplier grows with it — the very scale-out an attacker triggers weakens the brake. That makes
this deliberately *not* an exact science:

- **Authoritative rate limiting belongs at the gateway** — API Gateway usage plans, Azure API
  Management policies, your ingress/WAF — where one limit covers every instance.
- Use this package as **defense-in-depth**: a cheap floor of protection for endpoints that would
  otherwise ship with none, and for deployments that don't have a gateway in front of them yet.

## Quick start

Add the middleware **before** whatever it should protect:

```csharp
app.UseBenzeneMessage(x => x
    .UseFixedWindowRateLimiting(60, TimeSpan.FromMinutes(1))   // 60 requests/minute
    .UseHealthCheck()
    .UseSpec()
    .UseMessageHandlers());
```

A message over the limit never reaches the protected middleware: it short-circuits with a
`too-many-requests` result, which the standard status mapping serves as **HTTP 429**, with the
limiter's retry-after hint in both the error message and a standard `Retry-After` response header
when the limiter provides one (not every limiter does — `SlidingWindowRateLimiter` never supplies
it):

```json
{ "status": "too-many-requests", "errors": ["Rate limit exceeded; retry after 42s"] }
```

Nothing is ever queued — a protective limiter that queues requests just moves the resource
exhaustion into memory — so rejection is immediate.

A rejection is also logged (a structured warning naming the limiter and the message's cost) when
an `ILogger` is available in the pipeline's DI container — there is nothing else to configure.

### One shared limiter — or one per caller

Every entry point above shares **one limiter across every caller**: an accidental retry storm (or a
deliberate abuser) from a single caller can exhaust the whole budget and start rejecting every other
caller too. `UsePartitionedRateLimiting` gives each caller its own share instead, keyed however you
derive it from the message — an IP, an API key, a tenant claim:

```csharp
var perCaller = PartitionedRateLimiter.Create<BenzeneMessageContext, string>(context =>
    RateLimitPartition.GetTokenBucketLimiter(
        ApiKeyOf(context),
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 100,
            TokensPerPeriod = 100,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

app.UsePartitionedRateLimiting(perCaller, context => ApiKeyOf(context)); // second arg: for log lines only
```

**Honesty note:** a caller-supplied key (an API key header, an unauthenticated claim) is spoofable —
a caller who can vary it can always get a fresh share. It is still strictly better than the
one-shared-limiter default: defeating it costs an attacker active effort, where the default costs
them nothing. A key derived from something the caller can't freely choose (an authenticated
identity, the peer IP a trusted proxy set) isn't spoofable this way at all.

## Limiting by payload size

Request *count* isn't always the right unit: a spec endpoint costs roughly the same per hit, but a
message-accepting endpoint can be abused with a few enormous payloads as effectively as with many
small ones. `UsePayloadSizeRateLimiting` runs a token bucket where **each message costs its request
body's size in UTF-8 bytes** (a bodyless message costs 1) — a bytes-per-second budget:

```csharp
.UsePayloadSizeRateLimiting(
    maxBurstBytes: 256 * 1024,          // bucket size: the most admissible at once
    bytesPerPeriod: 64 * 1024,          // sustained rate: 64 KiB/second
    replenishmentPeriod: TimeSpan.FromSeconds(1))
```

A single payload larger than `maxBurstBytes` can never be granted and is always rejected. When the
transport reports a `Content-Length` header already over `maxBurstBytes`, that declared size is
used as the cost directly, rejecting without reading/measuring the (already-buffered) body.

> **This is a rate bound, not a memory bound.** On ASP.NET Core hosts, Benzene reads the whole
> request body into memory **before** any message-pipeline middleware runs (`UseBufferedRequestBody`
> is unconditional) — so by the time this middleware's cost delegate sees the body, the allocation
> it exists to bound has already happened. The `Content-Length` pre-check above is a real but
> partial mitigation: it saves a full-body byte count on this middleware's own side for a well-behaved
> oversized request, but it does **not** stop the earlier buffering, and does nothing for a
> `Content-Length`-less streamed body. What this middleware actually bounds is the *rate* of
> oversized/many payloads reaching the handler and further downstream work — not the peak memory a
> single oversized request costs the process. If payload size is a real memory-exhaustion concern for
> an endpoint, put a host-level cap in front of it as well (Kestrel's `MaxRequestBodySize`, or a
> gateway body-size limit) — this middleware alone does not provide one.

## Bring your own limiter

Every built-in above is sugar over the same seam: any `System.Threading.RateLimiting.RateLimiter`
plugs in — sliding window, concurrency, or your own subclass — with an optional per-message permit
cost:

```csharp
.UseRateLimiting(new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
{
    PermitLimit = 100,
    Window = TimeSpan.FromSeconds(10),
    SegmentsPerWindow = 5,
    QueueLimit = 0,
}))

// or with a custom cost per message:
.UseRateLimiting(myLimiter, (resolver, context) => CostOf(context))
```

The limiter instance is shared across every message on the pipeline for the process lifetime — the
caller owns its disposal. Concurrency-style limiters work correctly: the acquired lease is held
across the rest of the pipeline and released when the message completes.

## Extension reference

| Extension | Limiter | Unit |
|---|---|---|
| `UseFixedWindowRateLimiting(permitLimit, window)` | `FixedWindowRateLimiter` | messages per window |
| `UseTokenBucketRateLimiting(tokenLimit, tokensPerPeriod, replenishmentPeriod)` | `TokenBucketRateLimiter` | messages, smoothed with bursts |
| `UsePayloadSizeRateLimiting(maxBurstBytes, bytesPerPeriod, replenishmentPeriod)` | `TokenBucketRateLimiter` | payload bytes |
| `UseRateLimiting(rateLimiter)` | bring your own | 1 permit per message |
| `UseRateLimiting(rateLimiter, permitCost)` | bring your own | bring your own |
| `UsePartitionedRateLimiting(partitionedLimiter, ...)` | bring your own `PartitionedRateLimiter<TContext>` | one share per partition key, bring your own cost |
