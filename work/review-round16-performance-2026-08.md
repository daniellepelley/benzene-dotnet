# Round 16 review — Performance & Reliability sweep (cross-cutting)

Scope: whole `src/` tree, hunting for the established bug classes (ambient-cancellation-token gaps,
unbounded/uncapped fan-out, undisposed/mis-shaped-disposal resources, thundering-herd/single-flight
gaps, per-item batch-failure isolation) recurring somewhere not yet checked. Reviewed against `main`
at `28473b0` in the shared `/workspace/benzene-dotnet` checkout.

Read-only review. No source file under `src/` or `test/` on `main` was modified. Two verification test
files were written to prove the findings below, run, and then deleted (not committed) once the
findings were confirmed — reproduced here in full so either finding can be re-verified or turned into
a permanent regression test by whoever picks up the fix. `RedisCacheServiceSyncDisposalRedTest.cs`'s
repro was additionally re-verified via a standalone, single-purpose console project outside the shared
test tree (see below) to get one completely uncontended confirmation.

## Note on environment

This checkout is shared with several other concurrently-running review agents. Over the course of this
review the whole-project test build broke and un-broke itself multiple times as another agent's own
in-progress scratch probe file (`test/Benzene.Core.Test/Resilience/HedgingRaceProbeTest.cs`) went
through several edit states, and files I had written were twice deleted out from under me by what
looks like another agent's cleanup pass. Findings below were re-verified after each disruption; the
Polly finding's proof was additionally cross-checked against a `Polly.Core` 8.5.0 XML-doc read (not
just against this repo's own code) so it does not depend on the shared checkout being in any particular
state.

---

## Finding 1 (headline) — `PollyResilienceMiddleware<TContext>` has no isolation between concurrent
Polly attempts: a shared ambient `CancellationTokenAccessor` gets torn between them, and the entire
downstream pipeline (`next()`, closing over one shared mutable context) can run twice for one logical
message

**Severity: high — reliability + a documentation-vs-reality gap, both freshly introduced in round 15's
own fix for #237.** `src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs` is exactly the kind of
fresh, unbenchmarked/under-tested code this round's brief calls out as the likeliest place for a
regression to hide. `Benzene.Resilience.Polly` falls under `infrastructure-product-owner`'s
"resilience patterns" remit per `.claude/PRODUCT_OWNERS.md`, but was **not** in the scope list of
`work/review-round16-infrastructure-2026-08.md` (that review covered `Benzene.Cache.*`,
`Benzene.RateLimiting`, `Benzene.Microsoft.Dependencies`, `Benzene.Autofac`, `Benzene.NewtonsoftJson`,
`Benzene.Xml`, `Benzene.Avro` — no `Benzene.Resilience`/`Benzene.Resilience.Polly`) — **loop
infrastructure-product-owner in on this one**, since this is squarely their package and it changes
observable behaviour/API-doc claims, not just an internal detail.

### The defect

`PollyResilienceMiddleware<TContext>.HandleAsync` (added/rewritten in round 15 to fix #237 —
`work/outstanding-bugs.md`'s "Round 15, WP-E" entry) does this inside the callback Polly invokes for
every attempt:

```csharp
await _pipeline.ExecuteAsync(static async (state, attemptToken) =>
{
    var original = state._accessor.CancellationToken;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(original, attemptToken);
    state._accessor.CancellationToken = cts.Token;
    try
    {
        await state.next();
        ...
    }
    finally
    {
        state._accessor.CancellationToken = original;
    }
}, (next, context, _isFailure, _accessor)).ConfigureAwait(false);
```

`_accessor` is a single mutable `CancellationTokenAccessor` instance shared across every attempt (it's
either resolved once per DI scope, or a private instance created once per middleware — see the
constructor), and `next`/`context` are the same closures/instance for every attempt too (`next` is the
entire rest of the downstream pipeline, not a fresh per-attempt action). None of `original`/`cts.Token`/
the restore step is attempt-scoped or synchronised. This is safe **only** as long as Polly never invokes
the callback more than once concurrently for the same execution — true for Retry (sequential) and
Timeout (single attempt), but false for any strategy that runs concurrent attempts, of which Polly's own
Hedging strategy is the paradigm example.

The XML doc on this type, and the freshly-updated `docs/cookbooks/polly-resilience.md` (both edited as
part of the same round-15 #237 fix), explicitly claim Hedging is one of the strategies this now
correctly supports: *"So Polly's Timeout, Hedging, and RateLimiter strategies — anything that cancels
an attempt — actually reach downstream code..."* (cookbook, line ~153) and the cookbook's headline and
"Everything else" section both list hedging as an available strategy via
`.UseResiliencePipeline(...)`. **This claim does not hold**: Polly's `AddHedging` extension exists only
on the *generic* `ResiliencePipelineBuilder<TResult>`
(`Polly.HedgingResiliencePipelineBuilderExtensions.AddHedging<TResult>(ResiliencePipelineBuilder<TResult>,
HedgingStrategyOptions<TResult>)` — confirmed against the `Polly.Core` 8.5.0 XML docs, the only
`AddHedging` overload in the package), and every `.UseResiliencePipeline(...)` overload in
`src/Benzene.Resilience.Polly/Extensions.cs` only accepts a **non-generic** `ResiliencePipeline`/
`Action<ResiliencePipelineBuilder>`. A literal `builder.AddHedging(...)` call on the non-generic
builder this package hands the caller does not compile (`CS1929`). The same is true of Polly's
`Fallback` strategy (`FallbackStrategyOptions<TResult>` is likewise generic-only) — also advertised in
the same cookbook sections.

That inability to wire real Hedging through the *convenience* API is an accident of Polly's own
generic/non-generic split, **not a safeguard against the underlying bug**: Polly's own documented
low-level extensibility point — `ResiliencePipelineBuilderExtensions.AddStrategy(ResiliencePipelineBuilder,
Func<StrategyBuilderContext, ResilienceStrategy>, ResilienceStrategyOptions)` — is available on exactly
the non-generic `ResiliencePipelineBuilder` that `.UseResiliencePipeline(Action<ResiliencePipelineBuilder>
configure)` already exposes today, and it lets a caller register **any** custom strategy, including a
hand-rolled "run N attempts concurrently, take the first" strategy (a DIY hedge — a completely
foreseeable thing for someone to reach for exactly because the cookbook already tells them hedging-like
concurrent-attempt behaviour is what this package is for). The moment that happens, the race below is
real, present-day, reachable through the shipped public API — not a hypothetical future concern.

### Verified reproduction

Built entirely from Polly's own public API (no reflection, no internal types) against the real,
unmodified `PollyResilienceMiddleware<TContext>` on `main`/`28473b0`:

```csharp
// test/Benzene.Core.Test/Resilience/PollyResilienceMiddlewareConcurrentAttemptRedTest.cs
// (written, run, and deleted — not committed; reproduced here in full)

private sealed class ConcurrentDuplicateStrategy : ResilienceStrategy
{
    protected override async ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
        ResilienceContext context,
        TState state)
    {
        var first = callback(context, state).AsTask();
        var second = callback(context, state).AsTask();
        var winner = await Task.WhenAny(first, second);
        await Task.WhenAll(first, second); // let the loser finish too, like a real hedge would
        return await winner;
    }
}

private sealed class ConcurrentDuplicateStrategyOptions : ResilienceStrategyOptions { }

private static ResiliencePipeline ConcurrentDuplicatePipeline() =>
    new ResiliencePipelineBuilder()
        .AddStrategy(_ => new ConcurrentDuplicateStrategy(), new ConcurrentDuplicateStrategyOptions())
        .Build();

[Fact]
public async Task HandleAsync_ConcurrentAttemptStrategy_InvokesNextConcurrently_NotSequentially()
{
    var activeConcurrently = 0;
    var maxObservedConcurrency = 0;
    var gate = new object();
    var middleware = new PollyResilienceMiddleware<object>(ConcurrentDuplicatePipeline());

    await middleware.HandleAsync(new object(), async () =>
    {
        lock (gate) { activeConcurrently++; maxObservedConcurrency = Math.Max(maxObservedConcurrency, activeConcurrently); }
        await Task.Delay(50);
        lock (gate) { activeConcurrently--; }
    });

    Assert.Equal(2, maxObservedConcurrency); // PASSED
}

[Fact]
public async Task HandleAsync_ConcurrentAttemptStrategy_SharedAccessor_TornBetweenAttempts()
{
    var accessor = new CancellationTokenAccessor();
    var middleware = new PollyResilienceMiddleware<object>(ConcurrentDuplicatePipeline(), accessor: accessor);
    var mismatchObserved = false;

    await middleware.HandleAsync(new object(), async () =>
    {
        var tokenAtEntry = accessor.CancellationToken;
        await Task.Delay(30); // let the sibling attempt run its own set/restore meanwhile
        if (tokenAtEntry != accessor.CancellationToken) { mismatchObserved = true; }
    });

    Assert.True(mismatchObserved); // PASSED — the token this attempt "owns" changed under it
}

[Fact]
public async Task HandleAsync_ConcurrentAttemptStrategy_NextRunsTwice_LastWriteWinsOnSharedContext()
{
    var writes = 0;
    var context = new MutableResult(); // { public int Value { get; set; } }
    var middleware = new PollyResilienceMiddleware<MutableResult>(ConcurrentDuplicatePipeline());

    await middleware.HandleAsync(context, async () =>
    {
        var mine = Interlocked.Increment(ref writes);
        await Task.Delay(mine == 1 ? 60 : 10); // interleave so both actually overlap
        context.Value = mine; // simulates a terminal middleware setting context.Response
    });

    Assert.Equal(2, writes); // PASSED — next() really ran twice for one HandleAsync call
}
```

All three passed on `main`/`28473b0`:

```
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 183 ms - Benzene.Test.dll (net10.0)
```

This proves, concretely and not just by inspection:

1. **`next()` — the entire rest of the downstream pipeline — really does run concurrently**, not just
   Polly's internal bookkeeping. For an inbound message pipeline, this means the actual handler
   dispatch (and any side effect it has — a write, a charge, an outbound send) executes twice for one
   logical message the moment any concurrent-attempt strategy is on the pipeline.
2. **The shared `CancellationTokenAccessor` is torn between attempts.** One attempt's `finally` restore
   can — and, per the test, does — overwrite the ambient token a sibling attempt is still relying on
   mid-flight. In the worst case the value restored is a disposed `CancellationTokenSource`'s token
   (the `using var cts` from the OTHER, already-finished attempt): reading `IsCancellationRequested` on
   that is harmless, but anything downstream that calls `.Register(...)` on it (a completely standard
   cooperative-cancellation pattern) throws `ObjectDisposedException` on a message that, from the
   caller's perspective, did nothing wrong.
3. **Whichever attempt's terminal middleware writes last wins, silently**, on the one shared mutable
   context object (`MutableResult` here stands in for a real `OutboundContext`/inbound context whose
   terminal middleware sets `context.Response`/calls `IMessageHandlerResultSetter`) — a duplicate-side-
   effect/lost-result correctness bug, not merely a cancellation-plumbing nit.

### Recommendation

**REQUEST CHANGES** on the round-15 #237 fix and its documentation, with these concrete next steps:

1. **Immediate, low-risk**: correct `docs/cookbooks/polly-resilience.md` and
   `PollyResilienceMiddleware<TContext>`'s own XML doc to stop claiming Hedging/Fallback work through
   `.UseResiliencePipeline(...)` — they don't compile against the non-generic builder this package's
   convenience overloads hand out, so the current wording is actively misleading, exactly the kind of
   "doc claims a capability the code doesn't have" defect #237 itself was partly about
   ("the published cookbook additionally claimed the token was passed through, which was false against
   the actual source").
2. **Design decision needed (loop in `infrastructure-product-owner`, this is their package)**: either
   (a) make `PollyResilienceMiddleware<TContext>` reject/guard against concurrent re-entrant invocation
   of its callback (e.g. detect re-entrancy via a per-attempt counter/semaphore and throw a clear,
   documented `NotSupportedException` rather than silently corrupting shared state), or (b) redesign the
   ambient-token exposure to be attempt-scoped (e.g. an `AsyncLocal<CancellationToken>` per logical
   attempt rather than one mutable field, and a documented contract that a concurrent-attempt strategy
   must not share a downstream continuation across attempts at all — which may mean concurrent-attempt
   strategies are simply out of scope for this middleware, stated as plainly as `Benzene.Resilience.Core`
   already states its own "no circuit breaker/timeout/bulkhead" boundary).
3. **Test coverage gap**: `test/Benzene.Core.Test/Resilience/PollyResilienceMiddlewareTest.cs` has no
   test exercising concurrent attempt invocation at all (every existing test is Retry-sequential or
   Timeout-single-attempt) — worth a permanent regression test once (2) is decided, using the same
   `AddStrategy`-based custom-strategy technique above (it doesn't require Polly to ship a non-generic
   Hedging option; the concurrency hazard is independent of which strategy triggers it).
4. This is **not** a benchmarked-performance finding — no `BenchmarkDotNet` coverage exists for
   `Benzene.Resilience.Polly` at all (the suite in `benchmarks/Benzene.Benchmarks` covers only
   `MiddlewarePipeline<TContext>.HandleAsync` and `MultiSerializerOptionsRequestMapper<TContext>.GetBody<T>`
   per its own README) — it is a correctness/reliability finding, reasoned from code and proven by the
   xUnit repro above, not a measured performance regression.

---

## Finding 2 (corroboration, not new) — `RedisCacheService` (`Benzene.Cache.Redis`) is
`IAsyncDisposable`-only; synchronous DI-container disposal throws `InvalidOperationException`

**Already filed in full by `infrastructure-product-owner`'s own round-16 review**
(`work/review-round16-infrastructure-2026-08.md`, "Finding — `RedisCacheService` is
`IAsyncDisposable`-only..."), including the `AddScoped` per-message-scope variant, the Autofac-vs-
Microsoft-DI divergence, and a suggested fix mirroring `MeshAnnouncer`/`InternallyOwnedRateLimiterHolder`.
Recorded here only because I independently found and reproduced the exact same defect (from the
"IAsyncDisposable-declared-alone" DI-registration-hazard grep this round's brief specifically asked
for) before discovering it had already been filed — cross-referencing so a reader of this doc doesn't
think it was missed, and adding one independent, fully-isolated repro outside the shared/contended test
tree for an extra clean confirmation:

```csharp
// Standalone console project (no shared checkout, no xunit, no mocking library) referencing
// Benzene.Cache.Redis + Benzene.Microsoft.Dependencies + Benzene.Diagnostics directly.
internal sealed class NeverConnectingFactory : IRedisConnectionFactory
{
    public Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions options)
        => new TaskCompletionSource<IConnectionMultiplexer>().Task; // never resolves - irrelevant here
}

internal sealed class ReproRedisCacheService : RedisCacheService
{
    public ReproRedisCacheService(ILogger<RedisCacheService> logger, IProcessTimerFactory processTimerFactory,
        IRedisConnectionFactory connectionFactory, ISerializer? serializer = null)
        : base(logger, processTimerFactory, connectionFactory, serializer) { }

    protected override Task<ConfigurationOptions> GetConfigurationOptionsAsync() => Task.FromResult(new ConfigurationOptions());
}

var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IProcessTimerFactory>(_ => new DebugTimerFactory());
services.AddSingleton<IRedisConnectionFactory>(_ => new NeverConnectingFactory());
services.AddSingleton<ReproRedisCacheService>();

var factory = new MicrosoftServiceResolverFactory(services);
var service = factory.CreateScope().GetService<ReproRedisCacheService>(); // forces construction
factory.Dispose(); // synchronous container disposal
```

Output:

```
Constructed: True
Implements IDisposable: False
Implements IAsyncDisposable: True
RESULT: THREW System.InvalidOperationException: 'ReproRedisCacheService' type only implements
IAsyncDisposable. Use DisposeAsync to dispose the container.
```

Confirms the infra PO's finding exactly, with zero dependency on the shared checkout's state. See their
doc for the full severity assessment, the `AddScoped` variant, and the suggested fix — no need to
duplicate it here.

---

## Other areas swept — no additional finding clearing the bar

Per the brief's specific hunt list:

- **`CancellationToken.None` literal audit** (`grep -rn "CancellationToken.None" src/`, ~60 matches):
  every non-doc-comment occurrence traced. All are either (a) already-documented deliberate design —
  `BenzeneServiceBusWorker`'s/`BenzeneEventHubWorker`'s settlement/checkpoint calls (explicitly commented
  as intentionally decoupled from the message's own lock-expiry token), `SqsConsumer`'s poll-loop
  settlement, `Task.Run(action, CancellationToken.None)` scheduling idiom in `OutboxDispatcherWorker`/
  `BenzeneKafkaWorker` (correct — the loop's *own* internal token governs the loop; `Task.Run`'s
  parameter only controls whether the task starts running at all), or (b) `HealthCheckProcessor.
  RunTimedAsync`'s explicit, commented "takes no ambient CancellationToken (out of scope for this
  change)" — a known, already-documented gap, not a silently-dropped one. No new silent-drop found
  beyond what #1-2/#62/#104/#237 already fixed.
  - One lower-confidence item flagged for further investigation rather than filed as a finding:
    `DynamoDbEventStore.SafeCurrentVersionAsync` (`src/Benzene.EventSourcing.DynamoDb/DynamoDbEventStore.cs:209-224`)
    reads back the current version with `CancellationToken.None` and no explicit timeout after a
    confirmed write conflict, wrapped in a try/catch that only handles thrown exceptions, not a hang.
    The comment justifies not using the caller's token (a cancelling caller should still see the real
    conflict), but doesn't bound the read at all. In practice the AWS SDK's own HTTP-client timeouts
    bound this regardless, so this is not filed as a proven defect — just flagged as worth an explicit,
    documented timeout (the codebase's own `TimeOutHealthCheck` pattern) rather than relying on the SDK's
    default.
- **Reverse check — packages that plausibly need `ICancellationTokenAccessor` but don't use it yet**:
  spot-checked `Benzene.Cache.Redis` (uses explicit `CancellationToken` parameters throughout instead —
  a valid, arguably better alternative to the ambient-accessor idiom since every call site already has
  one), `Benzene.Grpc.Client`, `Benzene.Clients.Http`, `Benzene.Idempotency`, `Benzene.ClaimCheck` — all
  already wire the accessor or an equivalent explicit token. No gap found.
- **`Task.WhenAll(` outside `BoundedFanOut` in a batch/fan-out context**: every non-`BoundedFanOut`
  call site found (`Benzene.Mesh.Aggregator`, `Benzene.SelfHost.CompositeBenzeneWorker`,
  `Benzene.Clients.InProcess.InProcessFanOutClientMiddleware`, `Benzene.Cache.Redis.RedisMultiKeyActions`,
  `Benzene.Saga.Stage`, `Benzene.HealthChecks.HealthCheckProcessor`) is bounded by a statically-configured
  set (route targets, saga steps, registered workers/usage-sources — not an attacker/caller-controlled
  per-message collection) and already has per-item exception isolation (a try/catch inside the mapped
  delegate, so no task in the `WhenAll` array can fault and no per-item failure loses a sibling's
  result) — the exact opposite of the already-fixed #92-class bug, not a recurrence of it.
  `CompositeBenzeneWorker.StopAsync`'s bare `Task.WhenAll(_workers.Select(x => x.StopAsync(...)))`
  looked suspicious at first (no `SafeStart`-style wrapping, unlike its own `StartAsync`) but every
  concrete `IBenzeneWorker.StopAsync` in this codebase is declared `async Task` (one exception,
  `SqsConsumer.StopAsync`, is a trivial `return Task.CompletedTask`) — an `async Task` method can't
  throw synchronously out of the `.Select(...).ToArray()` enumeration (any pre-first-`await` throw is
  captured into the returned faulted `Task`, not thrown to the caller), so every worker's `StopAsync`
  genuinely gets invoked and `Task.WhenAll` waits for all of them before surfacing any exception. Not a
  bug.
- **`IDisposable`/`IAsyncDisposable` declared alone on a DI-registered type** (the
  `InternallyOwnedRateLimiterHolder<TContext>`-class bug): full sweep of every class declaring exactly
  one of the two interfaces in `src/`. Findings: `RedisCacheService` (Finding 2, already filed by
  infra). Everything else checked out:
  - `AutofacServiceResolverFactory`/`MicrosoftServiceResolverFactory` (declared `IAsyncDisposable` only
    in the type header, but both also implement a `Dispose()` method and their common interface
    `IServiceResolverFactory : IDisposable` supplies `IDisposable` transitively — genuinely both).
  - `RateLimitingMiddleware<TContext>`/`PartitionedRateLimitingMiddleware<TContext>` (`IAsyncDisposable`-
    only, but never DI-registered — constructed fresh per message via `app.Use(resolver => new
    RateLimitingMiddleware<TContext>(...))`, so no container ever tracks them for disposal; confirmed via
    `grep` across `Benzene.RateLimiting/Extensions.cs` — every construction site is a factory delegate,
    none is `AddSingleton`/`AddScoped`).
  - `RabbitMqConnectionProvider` (`IAsyncDisposable`-only) — **minor, unproven secondary observation,
    not filed as a finding**: it's constructed via bare `new RabbitMqConnectionProvider(connectionFactory)`
    inside `RabbitMqHealthCheckExtensions.AddRabbitMqHealthCheck`/`AddRabbitMqDependencyHealthCheck` and
    captured in a closure — never registered with any DI container at all, so it isn't at risk of the
    *crash* this finding class describes, but nothing ever calls its `DisposeAsync()` either: the
    health-check's dedicated RabbitMQ connection lives for the process's whole lifetime with no explicit
    cleanup path (it will close when the process exits regardless, and it's one connection per configured
    health check, not per-request, so this doesn't compound under load) — worth a follow-up to give
    `RabbitMqHealthCheck` itself `IAsyncDisposable` and thread disposal through
    `IHealthCheckBuilder`/`IBenzeneServiceContainer`'s own lifecycle if `infrastructure-product-owner`
    judges it worth the API surface.
  - `HttpMeshIssueExporter`/`HttpMeshTraceExporter`/`MeshAnnouncer` already implement both (correct,
    already-fixed instances of this exact pattern).
- **Timeout/deadline propagation**: `Benzene.Resilience.TimeoutMiddleware<TContext>` re-read in full —
  its nested-timeout composition (innermost deadline governs, each layer restores exactly what it saw
  on entry, timeout-vs-genuine-cancellation disambiguated via the *original* host token rather than the
  attempt's own token) is careful and already well-documented; no gap found there. The one deadline-
  propagation gap this round actually found is Finding 1 above — not a caller-supplied-deadline-ignored
  case, but the inverse: the mechanism built to *propagate* Polly's per-attempt deadline correctly for a
  single attempt has no isolation once more than one attempt exists concurrently.
- **Hot-path allocation regressions from this session's 16 work packages**: read
  `InternallyOwnedRateLimiterHolder<TContext>` (WP-J), the S3/SNS/EventBridge escalation path (WP-B,
  `#229`), and `MessageHandlerDefinitionIndex`/`CacheMessageHandlersFinder` call sites touched this
  round — no new per-message allocation introduced; the fixes are either construction-time (DI
  registration) or exception-path-only code, not hot-path-every-message code. No benchmark exists for
  any of these paths specifically (only `MiddlewarePipeline<TContext>.HandleAsync` and
  `MultiSerializerOptionsRequestMapper<TContext>.GetBody<T>` are covered per
  `benchmarks/Benzene.Benchmarks/README.md`) — flagged as a standing gap, not a regression found.

## Summary

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | `PollyResilienceMiddleware<TContext>` corrupts shared ambient token + shared context under any concurrent-attempt Polly strategy; cookbook/XML doc overclaims Hedging/Fallback support that doesn't compile | High — reliability + doc accuracy | New, proven by xUnit repro (3/3 assertions passed) |
| 2 | `RedisCacheService` `IAsyncDisposable`-only, throws on sync DI disposal | High — reliability | Already filed by `infrastructure-product-owner`; independently reproduced here for corroboration |

**Recommendation for Finding 1: REQUEST CHANGES** — loop in `infrastructure-product-owner` (package
owner) and `core-product-owner` (the `IMiddleware<TContext>` `next()`-has-no-per-attempt-identity shape
that makes this possible is a pipeline-contract question, not just an implementation bug in one
package) before either fixing the doc alone (leaves the underlying corruption reachable via
`AddStrategy`) or attempting a runtime fix (needs a design decision on whether concurrent-attempt
strategies are in scope for this middleware at all).

No other finding in this sweep cleared the bar for a genuine, provable correctness bug, resource leak,
or broken reliability guarantee distinct from what's already fixed or already filed elsewhere this
round.
