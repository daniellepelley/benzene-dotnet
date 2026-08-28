# Benzene.Resilience

## What this package does
Provides two middlewares for the Benzene pipeline: `RetryMiddleware<TContext>` (`.UseRetry(...)`)
and `TimeoutMiddleware<TContext>` (`.UseTimeout(...)`). Retry uses **exponential backoff only**.
There is **no Polly dependency** — this package is pure Benzene middleware over
`Benzene.Abstractions.Middleware`. See the [Capability Matrix](../../docs/capability-matrix.md) for
where retry/timeout fit and when to reach for `Benzene.Resilience.Polly` (circuit breaker / hedging
/ fallback / a more configurable timeout policy).

## Key types/interfaces
- **`RetryMiddleware<TContext>`** — re-invokes the downstream pipeline (`next`) on failure. Retries
  on a thrown exception (default: any exception except `OperationCanceledException`) and/or on a
  context predicate. Constructor knobs:
  - `numberOfRetries` (default 3)
  - `initialDelay` (default 200ms)
  - `backoffFactor` (default 2.0 — delay is multiplied by this each attempt)
  - `maxDelay` (`TimeSpan?`, default `null` = uncapped) — caps the actual sleep duration each
    attempt; the underlying exponential growth used to compute the *next* attempt's delay is left
    uncapped, so later attempts still compound off the true curve (matches AWS's documented "full
    jitter" algorithm: `sleep = random(0, min(cap, base * factor^attempt))`)
  - `shouldRetry` (`Func<Exception, bool>`) — decide retry from the exception
  - `shouldRetryContext` (`Func<TContext, bool>`) — decide retry from the resulting context (default
    `false`, i.e. no retry on a "successful-but-failed" context)
  - `jitter` (`Func<TimeSpan, TimeSpan>`, default `null` = no jitter / identity) — transforms the
    capped delay into the actual sleep duration. `RetryMiddleware.FullJitter(Random? random = null)`
    (the non-generic companion class) is a ready-made "full jitter" implementation
    (`random(0, delay)`) you can pass straight in — spreads out retries from many callers that
    backed off at the same moment instead of them all retrying in lockstep
  - `delay` (`Func<TimeSpan, Task>`) — override the delay mechanism (default `Task.Delay`), useful
    for tests
- **`RetryMiddleware.FullJitter(...)`** — static helper on the non-generic `RetryMiddleware` class
  (not `RetryMiddleware<TContext>` — it needs no `TContext`, matching the `Task`/`Task<T>` pattern).
- **`Extensions.UseRetry<TContext>(...)`** — pipeline-builder extension registering the middleware
  with the same parameters.
- **`TimeoutMiddleware<TContext>`** — wraps the downstream pipeline (`next`) in a deadline. Composes
  with the ambient `Benzene.Abstractions.DI.ICancellationTokenAccessor` (see
  `work/archive/cancellation-design-2026-08.md` §2.2/§2.4 for the full design): saves the accessor's current token,
  links a new `CancellationTokenSource` to it, arms it with `CancelAfter(timeout)`, sets the linked
  token as ambient for the duration of `next()`, and restores the original token in a `finally`
  (`using` on the linked source, so it — and its timer, and its registration on the original token —
  is disposed on every path). If the timer fires it translates the resulting
  `OperationCanceledException` into a `TimeoutException`; if the *original* token was already
  cancelled (a genuine host cancellation, e.g. shutdown or client disconnect) the
  `OperationCanceledException` is left to propagate untouched, so queue/settle/ack transports still
  redeliver interrupted work exactly as they would without this middleware in the pipeline. Nested
  `.UseTimeout(...)` calls compose naturally (innermost deadline governs while inside it).
- **`Extensions.UseTimeout<TContext>(...)`** — pipeline-builder extension registering
  `TimeoutMiddleware<TContext>`, resolving `Benzene.Core.CancellationTokenAccessor` from the
  per-invocation scope.

## When to use this package
- When you want a downstream pipeline step retried on transient failure with exponential backoff
  (`.UseRetry`).
- When you want a hard deadline around the downstream pipeline that turns into a precise
  `BenzeneResultStatus.Timeout` failure result rather than an opaque hang or aborted call
  (`.UseTimeout`) — see `MessageHandler`'s `catch (TimeoutException)` for the status mapping.

## Deliberate boundaries (this package)
- **This package is retry + a simple deadline-based timeout, and stays that way.** No circuit
  breaker, bulkhead, hedging, or fallback here, and **no Polly dependency** — that keeps
  `Benzene.Resilience` the zero-dependency option for callers who only want these two policies.
  `TimeoutMiddleware`'s timeout is intentionally simple: one fixed `TimeSpan`, no per-attempt reset,
  no combination with retry beyond ordinary middleware composition (`.UseRetry(...).UseTimeout(...)`
  applies the same deadline to every retry attempt combined; `.UseTimeout(...).UseRetry(...)` applies
  it once across all attempts).
- **The full toolkit lives in the sibling `Benzene.Resilience.Polly`.** For circuit breaker / hedging
  / fallback / rate limiting / a more configurable timeout policy, use `.UseResiliencePipeline(...)`
  from that package, which runs the pipeline through a Polly v8 `ResiliencePipeline`. It also bridges
  Benzene's result-on-context failure model to Polly's outcome model via an optional `isFailure`
  predicate — see `src/Benzene.Resilience.Polly/CLAUDE.md`. Pick this package (`.UseRetry`/
  `.UseTimeout`) for retry/timeout with no extra dependency; pick `Benzene.Resilience.Polly` for
  anything more.

## Important conventions
- **Retry re-invokes the whole downstream pipeline.** Do not place it on an inbound context that has
  already written a response (e.g. an inbound HTTP context) — a re-invocation would run those steps
  again. It is intended for outbound/port calls that are safe to re-run.
- `OperationCanceledException` is not retried by default (respects cancellation).
- **`UseTimeout` only interrupts cooperative work.** It sets the ambient cancellation token; it does
  not forcibly abort a handler or middleware that never reads
  `ICancellationTokenAccessor.CancellationToken`. Downstream code has to actually pass the token into
  its own I/O (an HTTP call, `Task.Delay`, a DB query) for the deadline to have a visible effect.
- **The timeout-vs-cancellation line is load-bearing.** A fired *timer* becomes a
  `TimeoutException` → `BenzeneResultStatus.Timeout` failure result. A fired *host* token (the
  original, pre-existing ambient token) must keep propagating as an untouched
  `OperationCanceledException` — do not weaken the `when (!original.IsCancellationRequested)` filter
  in `TimeoutMiddleware.HandleAsync`, it's what tells the two apart. **#61 deliberately dropped the
  `ex.CancellationToken == cts.Token` half this filter used to carry** (round 7-10) — comparing the
  exception's token against *this layer's own* `cts.Token` breaks under nested `.UseTimeout(...)`
  composition: when an OUTER deadline fires while execution is inside an INNER wrap, the exception
  that unwinds through the inner layer always carries the *inner* layer's linked token (its own
  linked source observed its parent — the outer layer's `cts.Token` — being cancelled and reports
  itself as the source), never the outer layer's. Requiring an exact `cts.Token` match here would let
  that case slip past this layer's catch entirely, as a raw, untranslated
  `OperationCanceledException`, when a timer — just not *this* layer's own — is exactly what fired.
  Comparing only against `original` (the true, never-timer-cancelled host/pre-existing token) is what
  correctly separates "some timer in this nesting fired" from "the host cancelled" in every nesting
  depth. See the type's own XML remarks (`TimeoutMiddleware.cs`) for the full worked example — they
  are the source of truth if this bullet and the code ever again disagree.

## Dependencies on other Benzene packages
- **Benzene.Abstractions.Middleware** — `IMiddleware<TContext>`, `IMiddlewarePipelineBuilder<TContext>`
- **Benzene.Core** — `CancellationTokenAccessor` (`TimeoutMiddleware`'s write handle onto the ambient
  cancellation token)
- **Benzene.Core.Middleware** — middleware pipeline implementation
