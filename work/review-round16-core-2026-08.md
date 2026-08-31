# Round 16 review findings — core packages, DI/middleware pipeline, resilience, versioning (2026-08)

**Status: ACTIVE — findings only, not yet fixed.** Scope assigned for this round: `Benzene.Core.*`
(message handling, middleware pipeline composition, request/response mapping, media format
negotiation, correlation-id/health-check touchpoints), `Benzene.Microsoft.Dependencies` /
`Benzene.Autofac` (DI adapters), `Benzene.Resilience` / `Benzene.Resilience.Polly`,
`Benzene.Core.Versioning`, `Benzene.Results`, `Benzene.Core.MessageHandlers`, `Benzene.Http`,
`Benzene.Testing` — the packages every other package depends on. Explicitly asked to look *beyond*
what round 15's WP-A (`#226`, `CasterFuncBuilder` recursion) and WP-E (`#237`/`#238`, Polly
cancellation + Xml serializer) already fixed, and beyond round 14's Autofac review (`#210`).

Every finding below was executed against the real assemblies at `28473b0` (isolated, throwaway
console/xUnit probes referencing the actual project files via `ProjectReference` — not speculation),
then deleted. Two worth-fixing findings; a broad "read but held up" list at the end.

---

## §1 `Benzene.Microsoft.Dependencies`: per-message scope disposal is synchronous-only — a
user-registered `IAsyncDisposable`-only scoped/transient service crashes every message and leaks

**Where:** `MicrosoftServiceResolverAdapter.Dispose()` (`src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverAdapter.cs:97-100`),
reached from `MiddlewareApplication<TEvent,TContext[,TResult]>.HandleAsync`'s
`using var serviceResolver = serviceResolverFactory.CreateScope();` (`src/Benzene.Core.Middleware/MiddlewareApplication.cs:50` and `:95`)
— the core per-message dispatch entry point shared by essentially every transport built on
`Benzene.Core.Middleware` (HTTP, SQS, ServiceBus, EventHub, Kafka, Kinesis, ...).

**The claim in the code:** `IServiceResolver : IDisposable` (`src/Benzene.Abstractions/DI/IServiceResolver.cs`)
is the *only* disposal contract the whole DI abstraction exposes — there is no
`IAsyncDisposable` anywhere on `IServiceResolver`, and `MiddlewareApplication` disposes the
per-message scope with a plain `using`, never `await using`.

**The bug:** Microsoft.Extensions.DependencyInjection's `ServiceProviderEngineScope.Dispose()`
throws `InvalidOperationException` ("'X' type only implements IAsyncDisposable. Use DisposeAsync to
dispose the container.") the moment it needs to synchronously dispose a resolved instance that
implements *only* `IAsyncDisposable` — this is standard, documented MS DI behaviour, not a Benzene
quirk. Since every `Benzene.Microsoft.Dependencies`-backed pipeline tears its per-message scope down
via a synchronous `using`, **any application that registers a scoped or transient service which
implements only `IAsyncDisposable`** — an entirely ordinary, idiomatic .NET pattern for an
async-native client/connection/repository (gRPC channel wrapper, some async DB drivers, a
hand-rolled async resource, etc.) — will throw this exception on **every single message** that
resolves it, discarding whatever result the handler actually computed. Worse, the resource's own
`DisposeAsync` never runs at all, so it is simultaneously a hard failure *and* a leak.

This is the same defect class already found and fixed piecemeal for framework-owned types
(`AutofacServiceResolverFactory` missing `IAsyncDisposable`, `#85`; `InternallyOwnedRateLimiterHolder<TContext>`
missing `IDisposable`, the round-15+-rounds-12-14 post-merge fix) — but those fixes only patched the
*specific classes Benzene itself owns and resolves*. They cannot fix a **user's own** registration,
because the root cause is architectural: `IServiceResolver`'s only disposal contract is synchronous,
and `MiddlewareApplication` never gives a resolved instance the chance to be disposed asynchronously.

**Verified** with a throwaway console app (`ProjectReference` to the real
`Benzene.Microsoft.Dependencies` and `Benzene.Autofac` projects, no reflection/internals):

```csharp
public sealed class AsyncOnlyResource : IAsyncDisposable
{
    public bool DisposedAsync;
    public ValueTask DisposeAsync() { DisposedAsync = true; return ValueTask.CompletedTask; }
}

var services = new ServiceCollection();
services.AddScoped<AsyncOnlyResource>();
var factory = new MicrosoftServiceResolverFactory(services);

using (var resolver = factory.CreateScope())      // == MiddlewareApplication.HandleAsync's own line
{
    resolver.GetService<AsyncOnlyResource>();
}                                                   // <-- throws here, on scope disposal
```

Actual output:
```
System.InvalidOperationException: 'AsyncOnlyResource' type only implements IAsyncDisposable.
Use DisposeAsync to dispose the container.
   at Microsoft.Extensions.DependencyInjection.ServiceLookup.ServiceProviderEngineScope.Dispose()
   at Benzene.Microsoft.Dependencies.MicrosoftServiceResolverAdapter.Dispose()
```
and `AsyncOnlyResource.DisposedAsync` is `false` — the resource is never actually released either.

**Side-by-side control (same probe, Autofac instead of Microsoft DI):** registering the identical
`AsyncOnlyResource` as `InstancePerLifetimeScope()` under `Benzene.Autofac`'s
`AutofacServiceResolverFactory`/`AutofacServiceResolverAdapter` and disposing the scope the same
(synchronous) way does **not** throw, and `DisposedAsync` **is** `true` — Autofac's own
`ILifetimeScope.Dispose()` correctly invokes `DisposeAsync()` on `IAsyncDisposable`-only components
even from a synchronous `Dispose()` call. So this is specific to the Microsoft DI adapter, not an
inherent limitation of the `IServiceResolver`/`MiddlewareApplication` design — Autofac already proves
a `Dispose()`-based adapter *can* get this right.

**Impact:** any Benzene application built on `Benzene.Microsoft.Dependencies` (the adapter used by
the ASP.NET Core, AWS Lambda hosting, and most of the framework's own examples/docs) that registers
a scoped or transient `IAsyncDisposable`-only service and resolves it during message handling will
see this exception on every message that touches it, forever, with no workaround short of also
implementing `IDisposable` on every such user type (undocumented anywhere in
`Benzene.Microsoft.Dependencies/CLAUDE.md`, `Benzene.Core.Middleware/CLAUDE.md`, or the docs site).

**Not previously recorded:** grepped `work/outstanding-bugs.md` for `IAsyncDisposable`/`DisposeAsync`
— every existing entry (`#85`, the round-15+ rounds-12–14 rate-limiting holder fix, `#146`, the mesh
exporters) is about a *framework-owned* singleton/holder getting `IDisposable` added so it disposes
cleanly; none address the general case of a user's own scoped/transient registration, and none flag
that the Microsoft adapter's synchronous-only disposal contract makes this unfixable except per-type.

---

## §2 `Benzene.Resilience.Polly`: the documented "full Polly strategy set… hedging, fallback" claim
does not hold, and the underlying reason (no per-attempt isolation) is a real correctness bug too

**Where:** `PollyResilienceMiddleware<TContext>.HandleAsync` (`src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs:69-101`);
`Extensions.UseResiliencePipeline` (`src/Benzene.Resilience.Polly/Extensions.cs`, all four overloads);
documented in the class's own XML remarks, the package's `.csproj` `<Description>`, `CLAUDE.md`, and
the dedicated `docs/cookbooks/polly-resilience.md`.

### 2a. Hedging and Fallback cannot be expressed through any of the four `UseResiliencePipeline`
overloads or the cookbook's own example code

Every one of the four `UseResiliencePipeline<TContext>` overloads takes either a prebuilt
non-generic `Polly.ResiliencePipeline`, or an `Action<ResiliencePipelineBuilder>` (also
non-generic). `PollyResilienceMiddleware<TContext>` stores a plain `ResiliencePipeline` field. But
in Polly.Core 8.5.0 (the package's pinned dependency), **Hedging and Fallback are only defined as
extensions on the *generic* `ResiliencePipelineBuilder<TResult>`** — `HedgingStrategyOptions<TResult>`
and `FallbackStrategyOptions<TResult>` have no non-generic counterpart the way
`RetryStrategyOptions`/`CircuitBreakerStrategyOptions`/`TimeoutStrategyOptions` do. So the exact
code shape the cookbook and CLAUDE.md advertise for these two strategies does not compile.

**Verified** (real `dotnet test` compiler output against the actual `Benzene.Resilience.Polly`
project, not a guess):
```
error CS1929: 'ResiliencePipelineBuilder' does not contain a definition for 'AddHedging' and
the best extension method overload
'HedgingResiliencePipelineBuilderExtensions.AddHedging<string>(ResiliencePipelineBuilder<string>,
HedgingStrategyOptions<string>)' requires a receiver of type 'Polly.ResiliencePipelineBuilder<string>'
```
and, separately, attempting the analogous `builder.AddFallback(new FallbackStrategyOptions { ... })`:
```
error CS0305: Using the generic type 'FallbackStrategyOptions<TResult>' requires 1 type arguments
```
By contrast, `Retry`/`Timeout`/`CircuitBreaker` (also confirmed by compiling) all have
non-generic-compatible option types and work fine on the plain `ResiliencePipelineBuilder` — which
is exactly why the cookbook's own worked example (`.AddTimeout(...).AddCircuitBreaker(...)`)
compiles and every existing `PollyResilienceMiddlewareTest` passes; nobody had tried Hedging or
Fallback through this package before.

Three separate places explicitly list Hedging/Fallback as supported: the `.csproj`
`<Description>` ("retry, circuit breaker, timeout, hedging, fallback, rate limiting"), the
`PollyResilienceMiddleware` XML remarks ("any strategy the pipeline is built with (retry, circuit
breaker, timeout, hedging, fallback, rate limiter, ...)"), and `docs/cookbooks/polly-resilience.md`'s
title and body ("Polly Resilience Pipelines (circuit breaker, timeout, hedging, fallback)"). None of
the four qualify the claim, and no doc mentions the generic/non-generic split or a workaround (e.g.
building a `ResiliencePipelineBuilder<TResult>` separately and calling `.AsPipeline()` before handing
it to the plain-`ResiliencePipeline` overload — untested here, and unmentioned anywhere in the docs
regardless of whether it works).

### 2b. Deeper problem: even a hand-rolled concurrent-attempt strategy (Polly's own documented
`AddStrategy` extensibility point) corrupts shared pipeline state

`PollyResilienceMiddleware<TContext>.HandleAsync`'s per-attempt callback closes over **one shared,
mutable `context` and `next` for the entire `HandleAsync` call** — there is no per-attempt
isolation. This is invisible for every strategy that only ever runs one attempt at a time
(Retry is sequential; Timeout/CircuitBreaker/RateLimiter gate a single attempt) but breaks the
instant a strategy invokes the wrapped callback **concurrently** — which Hedging would, if it were
reachable (§2a), and which is also a fully public, documented, no-reflection Polly pattern via
`ResiliencePipelineBuilderExtensions.AddStrategy(...)` (hand-rolling "run N attempts concurrently,
take the first" is exactly Polly's own suggested way to build a custom hedge).

**Verified** with the simplest possible such strategy, built entirely from Polly's public API:
```csharp
sealed class ConcurrentDuplicateStrategy : ResilienceStrategy
{
    protected override async ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
        ResilienceContext context, TState state)
    {
        var first = callback(context, state).AsTask();
        var second = callback(context, state).AsTask();
        var winner = await Task.WhenAny(first, second);
        await Task.WhenAll(first, second);
        return await winner;
    }
}
```
Run through `PollyResilienceMiddleware<object>` with `next` writing to a shared field:
```
next() ran 2 times for one HandleAsync call. Final observed value: 1.
```
(`next` was invoked with `mine == 1` given a *longer* delay than `mine == 2`, deliberately, so the
non-Polly-selected attempt finishes last and silently overwrites the winner's write.) `next` is the
entire rest of the downstream pipeline for one logical message — a real terminal middleware/handler
writing `context.Response`/`MessageResult` there means whichever attempt's write lands *last*
(irrespective of which outcome the resilience strategy itself picked) is what a caller/consumer
actually observes: duplicated side effects if `next` isn't idempotent, or a silently discarded
"winning" result — not just wasted work.

**Framing:** (2a) means this cannot be hit today through Polly's *built-in* catalogue on this
middleware (Hedging is the only built-in concurrent-attempt strategy, and it's inaccessible); (2b)
means the moment (2a) is worked around — by hand or via a future Polly release that adds a
non-generic Hedging overload — the middleware's shared-state design corrupts results the instant two
attempts overlap. Both halves trace to the same root gap: the middleware and its docs were designed
and tested only against strictly-sequential strategies.

**Not previously recorded:** `#237` (round 15) fixed cancellation-token propagation for the
sequential case and explicitly named Timeout/Hedging/RateLimiter as the strategies it was fixing for
— but its own test suite (`PollyResilienceMiddlewareTest.cs`) never exercises Hedging or any
concurrent-attempt strategy, so this gap survived that fix untested.

---

## Areas read and held up (no new finding)

- `Benzene.Core.Versioning`'s `VersionSelector`/`MessageHandlerDefinitionLookUp`/`MessageHandlerDefinitionIndex`
  chain: confirmed `(topicId, version)` de-duplication is correct (`GroupBy(...).Select(x => x.First())`),
  the single-registered-version fast path in `MessageHandlerDefinitionLookUp.FindHandler` is provably
  equivalent to running the selector, `VersionSelector.Select`'s ordinal (not culture-sensitive)
  `MaxBy` fallback is deliberate and documented, and `Topic`'s constructors never produce a `null`
  `Version` (always `string.Empty`), so no null/ambiguous-match path was found in the selection logic
  itself. `CasterFuncBuilder`'s much larger expression-building surface (class/enum/collection/base-type
  mapping) was read in full beyond the already-fixed `#226` recursion guard; no further defect found.
- `MessageRouter<TContext>`, `MessageGetter<TContext>`, `MessageTopicGetterExtensions.GetVersionedTopic`,
  `DeriveTopicMiddleware`/`PresetTopicMiddleware`/`ResolvedTopicCache`: the version-join-once-in-the-getter
  design (task `#98`) and the topic-cache invalidation on preset/derive are internally consistent; no
  stale-topic or double-join path found.
- `MiddlewarePipeline<TContext>`/`MiddlewarePipelineBuilder`: the reversed-once-at-construction chain
  composition and per-request lazy middleware resolution (deferred inside the closure, not eagerly
  during `Aggregate`) trace correctly to in-order execution; no ordering bug found.
- `Benzene.Http.Routing` (`UrlMatcher`, `CompiledRoutePath`, `VersionedHttpEndpointFinder`,
  `HttpVersioningOptions`): per-segment literal/parameter compilation, case-insensitive literal/prefix/suffix
  matching with case-preserved parameter extraction, and the empty-parameter-value → no-match rule were
  all read and traced through concrete examples; single-parameter-per-segment is a documented
  limitation, not a silent-corruption bug.
- `DefaultMessageHandlerResultSetterBase`, `MessageHandlerNoResultWrapper`: no nullable-contract
  violation found (the `#100`-`#103` class of bug); these are deliberately no-op/passthrough.
- `MicrosoftBenzeneServiceContainer.Reopen()`'s `new ServiceCollection { _services }` collection-copy
  idiom was re-checked (this exact line was flagged and cleared as correct in round 15's `just-noting`
  list) — not re-litigated here.
- Correlation-id (`Benzene.Abstractions.ICorrelationId`/`CorrelationHeaderDefaults`) and health-check
  aggregation live in `Benzene.Diagnostics`/`Benzene.HealthChecks`, outside this round's assigned
  package list (`Benzene.Core.*`, DI adapters, resilience, versioning, `Benzene.Results`,
  `Benzene.Testing`); the abstractions-only contracts in scope (`ICorrelationId`) carry no
  implementation to probe.

## Evidentiary note on this round's shared environment

`/workspace/benzene-dotnet`'s `test/` tree is being edited concurrently by several other round-16
review agents right now (visible via `git status --short`: sibling review docs
`review-round16-{aws,azure,infrastructure,mesh-composition,observability,schema-codegen}-2026-08.md`
and in-progress probe files under `test/`, including another agent's own
`PollyResilienceMiddlewareConcurrentAttemptRedTest.cs` independently targeting the same §2b root
cause). Both findings above were therefore verified in fully isolated, throwaway console projects
under the scratch directory (`ProjectReference`s pointing at the real `src/` project files, no
changes to `/workspace/benzene-dotnet` itself) rather than added to the shared `test/` tree, to avoid
interfering with that work; no source or test file in the repository was modified.
