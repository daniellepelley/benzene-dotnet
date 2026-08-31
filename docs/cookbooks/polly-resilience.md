# Polly Resilience Pipelines (circuit breaker, timeout, retry, rate limiter)

Run a Benzene middleware pipeline (or an outbound port call) through your own
[Polly](https://www.pollydocs.org/) `ResiliencePipeline`, so you get circuit breaker / timeout /
retry / rate limiting without Benzene wrapping or hiding Polly behind its own abstraction.

> **Hedging and Fallback are not supported.** See
> [Why concurrent-attempt strategies aren't supported](#why-concurrent-attempt-strategies-arent-supported)
> below.

## Problem Statement

`Benzene.Resilience` ships exactly one resilience pattern in-box: retry with exponential backoff
(`RetryMiddleware<TContext>` / `.UseRetry(...)`) — see [Resilience](../resilience.md). It
deliberately does **not** ship a circuit breaker, timeout, or bulkhead, and does not depend on Polly,
so it stays the zero-dependency option for callers who only want retry.

Richer sequential-attempt patterns — circuit breaker, timeout, retry, rate limiting — come from the
sibling **`Benzene.Resilience.Polly`** package. It takes a `Polly.Core` dependency in exchange for
that toolkit, and it *exposes* Polly rather than wrapping it: you build a `ResiliencePipeline` with
exactly the strategies you want and hand it to `.UseResiliencePipeline(...)`. Benzene gives Polly a
clean place to plug into the pipeline; it does not re-abstract the strategy surface (retry strategies,
circuit-breaker state, rate limiting) that's the reason to reach for Polly in the first place.
**Concurrent-attempt strategies — Hedging and Fallback — are out of scope**; see below.

> Prefer to own the ~15 lines yourself instead of taking the dependency? The
> [DIY alternative](#appendix-diy-without-the-package) at the end shows the hand-rolled middleware —
> the package is that same bridge, packaged, tested, and with the outcome-aware failure handling
> below added.

## Prerequisites

- A Benzene middleware pipeline (any transport) built with `IMiddlewarePipelineBuilder<TContext>`.
- Familiarity with building a Polly `ResiliencePipeline` via `ResiliencePipelineBuilder` — this
  cookbook doesn't re-teach Polly itself; see the [Polly docs](https://www.pollydocs.org/) for the
  full strategy catalogue. This package supports the **sequential-attempt** strategies (retry,
  circuit breaker, timeout, rate limiter) — not Hedging or Fallback, see below.

## Installation

```bash
dotnet add package Benzene.Resilience.Polly
```

It brings in `Polly.Core` (Polly's modern strategy-pipeline API, `ResiliencePipeline` /
`ResiliencePipelineBuilder`) transitively, plus `Benzene.Abstractions.Middleware` and
`Benzene.Core.Middleware`.

## Step-by-Step Implementation

### 1. Build a pipeline with the strategies you need

Compose whatever Polly strategies your service needs — timeout and circuit breaker in this example:

```csharp
using Polly;
using Polly.CircuitBreaker;

var pipeline = new ResiliencePipelineBuilder()
    .AddTimeout(TimeSpan.FromSeconds(5))
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(30),
        MinimumThroughput = 10,
        BreakDuration = TimeSpan.FromSeconds(15),
    })
    .Build();
```

### 2. Wire it into the pipeline

`.UseResiliencePipeline(...)` comes in four overloads — pass a prebuilt `ResiliencePipeline`, or
build it inline with an `Action<ResiliencePipelineBuilder>`, each optionally with the `isFailure`
predicate covered below:

```csharp
using Benzene.Resilience.Polly;

// Bring your own prebuilt pipeline...
app.UseSqs(sqsApp => sqsApp
    .UseResiliencePipeline(pipeline)
    .UseMessageHandlers(router => router.UseFluentValidation()));

// ...or configure it inline:
app.UseSqs(sqsApp => sqsApp
    .UseResiliencePipeline(builder => builder
        .AddTimeout(TimeSpan.FromSeconds(5))
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions { /* ... */ }))
    .UseMessageHandlers());
```

Register a stateful `ResiliencePipeline` **once** and reuse it — a circuit breaker's open/closed
state lives on the instance, so building a fresh one per message would defeat it. The prebuilt-pipeline
overload is the one to use when you want to share a single instance across pipeline builds (e.g. hold
it as a singleton in DI and pass it in).

`.UseResiliencePipeline(...)` is fully generic, like every other Benzene middleware, so it works on
any pipeline context — inbound transport contexts and `OutboundContext` alike.

## Outcome awareness: retrying a returned failure result

Benzene reports domain failure two ways: a **thrown exception**, or an **unsuccessful
`IBenzeneResult`** left on the context (not thrown) — see
[Message Result](../message-result.md). Polly's strategies fire on exceptions, so by default a
returned failure *result* is invisible to them.

Pass an `isFailure` predicate to bridge the two. After the pipeline runs, if the predicate returns
`true`, the middleware throws an internal `BenzeneFailureResultException` that Polly can treat as a
handled outcome — **but only if you configure the pipeline to handle it**:

```csharp
using Benzene.Resilience.Polly;
using Polly;
using Polly.Retry;

app.UseResiliencePipeline<MyMessageContext>(
    builder => builder.AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<BenzeneFailureResultException>(),
        MaxRetryAttempts = 3,
    }),
    isFailure: ctx => ctx.MessageResult?.IsSuccessful == false);
```

The sentinel **never escapes**: once the pipeline finishes (retries exhausted, breaker open, …), it's
swallowed and the last unsuccessful result remains on the context — identical to running with no
resilience middleware. A **real** exception is never wrapped and propagates normally. With no
`isFailure` (the default), only thrown exceptions drive the strategies. This is the one thing the DIY
middleware in the appendix doesn't give you for free.

## Outbound clients: the same middleware, no extra work

Because `.UseResiliencePipeline(...)` is fully generic, it works on an outbound route exactly the way
`Benzene.Resilience`'s `.UseRetry(...)` does (see
[Clients — Outbound middleware](../clients.md#outbound-middleware)). This is the higher-value case —
Benzene's whole thesis is wrapping port calls:

```csharp
services.UsingBenzene(x => x.AddOutboundRouting(routing => routing
    .Route("order:create", pipeline => pipeline
        .UseSqs(queueUrl)
        .UseResiliencePipeline(circuitBreakerPipeline))));
```

## Cancellation

Benzene's middleware pipeline does not thread a `CancellationToken` through
`IMiddleware<TContext>.HandleAsync(TContext context, Func<Task> next)` — no middleware anywhere in
Benzene carries one. So `PollyResilienceMiddleware` cannot hand Polly's per-attempt token to `next`
directly. Instead — exactly the pattern
[`Benzene.Resilience`'s `TimeoutMiddleware<TContext>`](../resilience.md) already uses — it exposes
that token to whatever `next()` wraps via the ambient `ICancellationTokenAccessor`: for the duration
of each Polly attempt it links the attempt's token with whatever ambient token was already set (so an
outer `UseTimeout`, or any host-seeded token, is never lost), sets the accessor to the linked token
before calling `next()`, and restores the prior ambient token once the attempt finishes. So Polly's
Timeout and RateLimiter strategies — anything that cancels an attempt — actually reach downstream
code, as long as that code reads the token from the accessor:

```csharp
public class MyOutboundHandler
{
    private readonly ICancellationTokenAccessor _accessor;

    public MyOutboundHandler(ICancellationTokenAccessor accessor) => _accessor = accessor;

    public Task CallDownstreamAsync() =>
        _httpClient.GetAsync("https://example.com", _accessor.CancellationToken);
}
```

When constructing the middleware via `.UseResiliencePipeline(...)`, the accessor is resolved from the
same DI scope as the rest of the pipeline, so this is automatic — nothing beyond reading
`ICancellationTokenAccessor` in your own code is required.

**The caveat — same as `TimeoutMiddleware`.** This can only cancel work that *observes* the ambient
token. `next()` (and whatever it calls) has no way to be forcibly interrupted — like every
`CancellationToken`-based mechanism in .NET, cancellation is cooperative. A `next()` that never reads
`ICancellationTokenAccessor` (or otherwise ignores the token it's handed) simply keeps running past
the configured deadline; since it never throws `OperationCanceledException`, Polly's own strategy never
sees the signal it needs to raise `TimeoutRejectedException` either — the pipeline just waits for
`next()` to finish and returns normally, functionally identical to running without this middleware at
all. There is no true "abandon and move on": Polly cannot forcibly abort a still-running `Task` any
more than `TimeoutMiddleware` can.

**Widening `ShouldHandle` can silently drop cancellation-safety.** Polly's own *default*
`ShouldHandle` (used when a strategy's options leave it unset) already excludes
`OperationCanceledException` — a caller-cancelled request does not, by itself, trip a circuit breaker
or exhaust a retry budget. But the `Handle<BenzeneFailureResultException>()` pattern shown above (and
the plain `Handle<Exception>()` used in the retry examples in this cookbook and its tests) is an
**explicit** `ShouldHandle`, which replaces that safe default rather than adding to it. Copy-pasting
`Handle<Exception>()` from a *retry* config onto a **circuit breaker**'s `ShouldHandle` reintroduces
exactly the bug the default quietly protected you from: a caller-cancelled request now counts as a
breaker failure and can trip the breaker for every other in-flight caller sharing it. Use
`Benzene.Resilience.Polly`'s `.ExcludingCancellation<TResult>()` extension (on `PredicateBuilder`/
`PredicateBuilder<TResult>`) instead of `Handle<Exception>()` whenever you widen a strategy's
`ShouldHandle` beyond a specific exception type — it excludes `OperationCanceledException` (and
subclasses, e.g. `TaskCanceledException`), mirroring `RetryMiddleware`'s own documented default
(`ex is not OperationCanceledException`):

```csharp
using Benzene.Resilience.Polly;
using Polly;
using Polly.CircuitBreaker;

var pipeline = new ResiliencePipelineBuilder()
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        // Safe: excludes OperationCanceledException, like Polly's own unset default.
        ShouldHandle = new PredicateBuilder().ExcludingCancellation(),
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(30),
        MinimumThroughput = 10,
        BreakDuration = TimeSpan.FromSeconds(15),
    })
    .Build();
```

## Testing

`ResiliencePipeline` is a real object you can construct directly in a test — no need to spin up your
whole host to exercise the middleware. Pass a `CancellationTokenAccessor` explicitly and have `next`
read its token, exactly as real downstream code would (see [Cancellation](#cancellation) above) —
that is what actually lets Polly's timeout reach `next`:

```csharp
using Benzene.Core;
using Benzene.Resilience.Polly;
using Polly;
using Polly.Timeout;

var accessor = new CancellationTokenAccessor();
var pipeline = new ResiliencePipelineBuilder()
    .AddTimeout(TimeSpan.FromMilliseconds(50))
    .Build();
var middleware = new PollyResilienceMiddleware<object>(pipeline, accessor: accessor);

await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
    middleware.HandleAsync(new object(), () => Task.Delay(TimeSpan.FromSeconds(1), accessor.CancellationToken)));
```

See `test/Benzene.Core.Test/Resilience/PollyResilienceMiddlewareTest.cs` for the full set (retry,
exception propagation, the outcome-aware failure-result path, and the cancellation behavior above —
including the caveat case where `next` ignores the token).

## Why concurrent-attempt strategies aren't supported

`PollyResilienceMiddleware<TContext>` only supports **sequential-attempt** Polly strategies — Retry,
Timeout, CircuitBreaker, RateLimiter — where Polly invokes the per-attempt callback strictly one
attempt at a time. It does **not** support Hedging or Fallback, or any custom strategy that runs more
than one attempt concurrently for a single execution. Two independent reasons, one shallow and one
fundamental:

1. **The convenience API can't even build one.** Polly.Core 8.5.0 defines `AddHedging`/`AddFallback`
   only on the *generic* `ResiliencePipelineBuilder<TResult>`
   (`HedgingStrategyOptions<TResult>`/`FallbackStrategyOptions<TResult>`), but every
   `.UseResiliencePipeline(...)` overload in this package hands out the *non-generic*
   `ResiliencePipelineBuilder`. This isn't an oversight to fix — Benzene results flow through the
   mutable pipeline `TContext`, not a `TResult` returned from `next()`, so the generic
   result-typed strategies have no natural mapping onto this middleware's shape in the first place.
2. **Even a hand-rolled concurrent attempt corrupts shared state.** The shallow restriction above is
   not a safety net: Polly's own public, non-generic `ResiliencePipelineBuilderExtensions.AddStrategy(...)`
   extensibility point is available on exactly the builder `.UseResiliencePipeline(Action<ResiliencePipelineBuilder>)`
   already hands out, and it lets you register **any** custom `ResilienceStrategy` — including a
   hand-rolled "run N attempts concurrently, take the first" hedge, a completely standard Polly
   pattern. `PollyResilienceMiddleware<TContext>.HandleAsync`'s per-attempt callback closes over one
   shared `next` continuation (the entire downstream pipeline), one shared `TContext` instance, and
   one shared ambient `CancellationTokenAccessor`. A concurrent-attempt strategy would run `next()` —
   and therefore the real handler dispatch and its side effects — more than once for one logical
   message, and tear the shared ambient token and `TContext` writes between attempts (last write
   wins, silently).

Rather than allow that corruption, the middleware detects re-entrancy: if a second attempt starts
while one is already in flight for the same `HandleAsync` call, it throws `NotSupportedException`
naming the problem, and `next()` never runs more than once. If you need hedging-style behavior,
implement it at a different layer — e.g. issue concurrent calls yourself around (not through) this
middleware, each with its own context/token. Supporting concurrent attempts natively would need a
redesign (per-attempt context/token isolation, with defined merge semantics for a mutable message
context) that is deliberately out of scope for this middleware; see `work/outstanding-bugs.md` for
the open maintainer question.

## Troubleshooting

**I want retry AND circuit breaker together.**
Compose both strategies into the same `ResiliencePipeline` via `ResiliencePipelineBuilder`
(`.AddRetry(...).AddCircuitBreaker(...)`) rather than stacking `Benzene.Resilience`'s `RetryMiddleware`
on top of `.UseResiliencePipeline(...)` — Polly's own strategies are designed to compose correctly
with each other (e.g. a circuit breaker sees retries as part of one logical call), which two
independent middleware wrapping each other might not get right.

**Should I use `Benzene.Resilience`'s `UseRetry` or Polly's retry strategy?**
Use `UseRetry` when you want retry only and no extra dependency; use `Benzene.Resilience.Polly` when
you want anything more, or already standardize on Polly. Don't stack both on the same call.

**My returned failure result isn't being retried.**
Supplying `isFailure` isn't enough on its own — the Polly pipeline must also be configured to handle
`BenzeneFailureResultException` (`ShouldHandle = new PredicateBuilder().Handle<BenzeneFailureResultException>()`).
Without that, the sentinel the middleware throws isn't a handled outcome, so no strategy fires.

## Appendix: DIY without the package

If you'd rather not take the `Polly.Core` dependency through `Benzene.Resilience.Polly`, the
exception-only bridge is genuinely small — this is what the package's core does, minus the
outcome-aware `isFailure` handling:

```csharp
using Benzene.Abstractions.Middleware;
using Polly;

public class PollyMiddleware<TContext> : IMiddleware<TContext>
{
    private readonly ResiliencePipeline _pipeline;

    public PollyMiddleware(ResiliencePipeline pipeline) => _pipeline = pipeline;

    public string Name => nameof(PollyMiddleware<TContext>);

    public Task HandleAsync(TContext context, Func<Task> next)
        => _pipeline.ExecuteAsync(async _ => await next()).AsTask();
}

public static class PollyExtensions
{
    public static IMiddlewarePipelineBuilder<TContext> UsePolly<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app, ResiliencePipeline pipeline)
        => app.Use(_ => new PollyMiddleware<TContext>(pipeline));
}
```

You still `dotnet add package Polly.Core` for the `ResiliencePipeline` type — the only thing you save
is the thin `Benzene.Resilience.Polly` layer (and you give up its four overloads and the outcome-aware
failure bridge). For most teams the package is the better trade; the DIY route is here for those who
want zero Benzene-owned surface between their code and Polly.

## See Also

- [Resilience](../resilience.md) — Benzene's own retry-with-backoff middleware
- [Middleware](../middleware.md)
- [Capability Matrix](../capability-matrix.md)
- [Polly documentation](https://www.pollydocs.org/)
