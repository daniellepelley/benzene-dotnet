# Round 16 review — Infrastructure Product Owner surface (DI adapters, caching, rate limiting, resilience wiring, serialization)

Scope: `src/Benzene.Microsoft.Dependencies`, `src/Benzene.Autofac`, `src/Benzene.Cache.Core`,
`src/Benzene.Cache.Redis`, `src/Benzene.RateLimiting`, `src/Benzene.NewtonsoftJson`, `src/Benzene.Xml`,
`src/Benzene.Avro`. Reviewed against `main` at `28473b0` (the commit named in the assignment; `main`
had since advanced to `6f19a06` with two docs-only commits in between — no source changes in scope).

Read-only review. No source files were modified. Verification tests described below were written,
run, and then deleted (not committed) — they are reproduced here in full so the finding can be
re-verified or turned into a permanent regression test by whoever picks up the fix.

## Finding — `RedisCacheService` is `IAsyncDisposable`-only; disposing it synchronously (the *only*
disposal path some hosts have, and the common per-message path in general) throws `InvalidOperationException`

**Severity: high.** This is exactly the bug class the assignment flagged as a lead (the
`InternallyOwnedRateLimiterHolder<TContext>` / round-15 fix), and it is present, unfixed, in
`Benzene.Cache.Redis` — a package this round's PO owns and that the round-15 fix did not touch.

### The defect

`src/Benzene.Cache.Redis/RedisCacheService.cs:10`:

```csharp
public abstract class RedisCacheService : ICacheService, IAsyncDisposable
```

Only `IAsyncDisposable` is implemented; there is no `IDisposable`. The package's own `CLAUDE.md`
explicitly tells consumers to let a container own this: *"register your subclass so its container
disposes it on shutdown"* — with no caveat that the container's disposal must be the async one.

`Benzene.Abstractions.DI.IServiceResolver` (the per-message/per-request scope abstraction every
transport in this codebase uses) is declared as:

```csharp
public interface IServiceResolver : IDisposable
```

— synchronous-only, no `IAsyncDisposable` counterpart at all. So **every** scope teardown in Benzene's
DI abstraction — per message, per request, or at application shutdown — is a synchronous `Dispose()`
call underneath, regardless of transport.

Microsoft.Extensions.DependencyInjection's `ServiceProvider`/`ServiceProviderEngineScope.Dispose()`
throws `InvalidOperationException` when it has to dispose a container-tracked instance (singleton
*or* scoped — not just singleton) that implements only `IAsyncDisposable`. `RedisCacheService` is
exactly that shape. Two concrete, independently-verified consequences:

1. **Registered `AddScoped` (the natural lifetime for anything constructed per message), disposal
   throws on the very first message** that resolves it — not just at process shutdown. Any
   per-message `IServiceResolver` teardown (e.g. `AwsLambdaEntryPoint.FunctionHandlerAsync`'s
   `using var scope = _serviceResolverFactory.CreateScope();`, run on every single Lambda invocation)
   reproduces this.
2. **Registered `AddSingleton`, disposal throws whenever the owning container is disposed
   synchronously.** `MicrosoftServiceResolverFactory.Dispose()` (`src/Benzene.Microsoft.Dependencies/
   MicrosoftServiceResolverFactory.cs:48`) does exactly that:
   `(_serviceProvider as IDisposable)?.Dispose()`. Critically, **this is the *only* disposal path
   `Benzene.Aws.Lambda.Core` has at all** — `IAwsLambdaEntryPoint : IDisposable` (no async variant),
   `AwsLambdaEntryPoint.Dispose()` → `_serviceResolverFactory.Dispose()` (the same
   `MicrosoftServiceResolverFactory.Dispose()`), `AwsLambdaHost<TStartUp>.Dispose()` → same. There is
   no `DisposeAsync` anywhere in that chain for a caller to prefer instead.

### Verified reproduction

Both scenarios were reproduced with real Microsoft.Extensions.DependencyInjection (no mocking of the
container) against `Benzene.Test.Cache.Redis.Instance.TestRedisCacheService` (the existing test
double already used by `RedisCacheServiceTest.cs`), built and run in an isolated worktree pinned at
`28473b0` to avoid interference from other concurrently-running review agents sharing the checkout.

```csharp
// Scenario 1: AddScoped, per-message scope disposal (the AwsLambdaEntryPoint pattern) — throws on
// the very first message, not at shutdown.
[Fact]
public void ScopedRedisCacheService_PerMessageScopeDisposal_ThrowsInvalidOperationException()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped<IProcessTimerFactory>(_ => new DebugTimerFactory());
    services.AddScoped<IRedisConnectionFactory>(_ => new MockConnectionFactory());
    services.AddScoped<TestRedisCacheService>();

    var factory = new MicrosoftServiceResolverFactory(services);
    var scope = factory.CreateScope();

    var service = scope.GetService<TestRedisCacheService>();
    Assert.NotNull(service);

    var ex = Record.Exception(() => scope.Dispose());

    Assert.NotNull(ex);
    Assert.IsType<InvalidOperationException>(ex);   // PASSES today — the bug reproduces.

    factory.Dispose();
}

// Scenario 2: AddSingleton, container/factory disposal (what AwsLambdaHost.Dispose() forces you into
// — there is no async alternative in that package).
[Fact]
public void SingletonRedisCacheService_SyncContainerDisposal_ThrowsInvalidOperationException()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IProcessTimerFactory>(_ => new DebugTimerFactory());
    services.AddSingleton<IRedisConnectionFactory>(_ => new MockConnectionFactory());
    services.AddSingleton<TestRedisCacheService>();

    var factory = new MicrosoftServiceResolverFactory(services);
    var resolver = factory.CreateScope();
    var service = resolver.GetService<TestRedisCacheService>();
    Assert.NotNull(service);

    var ex = Record.Exception(() => factory.Dispose());

    Assert.NotNull(ex);
    Assert.IsType<InvalidOperationException>(ex);   // PASSES today — the bug reproduces.
}
```

Both ran green (i.e. both assertions that an `InvalidOperationException` is thrown succeeded),
confirming the bug on `main`/`28473b0`:

```
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2 — (singleton scenario, run together)
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1 — (scoped scenario, run separately)
```

### Cross-container divergence (the "two adapters should behave identically" theme from prior rounds)

The identical shape does **not** throw under `Benzene.Autofac`. Verified against real Autofac (no
mocking):

```csharp
private sealed class AsyncOnlyDisposable : IAsyncDisposable
{
    public bool DisposedAsync { get; private set; }
    public ValueTask DisposeAsync() { DisposedAsync = true; return ValueTask.CompletedTask; }
}

[Fact]
public void SyncDispose_OwnedContainer_AsyncOnlyDisposableSingleton_DoesNotThrow_UnlikeMicrosoftDI()
{
    var containerBuilder = new ContainerBuilder();
    containerBuilder.RegisterType<AsyncOnlyDisposable>().SingleInstance();

    var factory = new AutofacServiceResolverFactory(containerBuilder);
    AsyncOnlyDisposable spy;
    using (var scope = factory.CreateScope()) { spy = scope.GetService<AsyncOnlyDisposable>(); }

    var ex = Record.Exception(() => factory.Dispose());

    Assert.Null(ex);              // PASSES — Autofac does not throw.
    Assert.True(spy.DisposedAsync); // PASSES — and it actually ran the async disposal.
}
```

This passed (`Failed: 0, Passed: 1`): Autofac's `ILifetimeScope.Dispose()` transparently
bridges a `SingleInstance` component that implements only `IAsyncDisposable` to its `DisposeAsync()`
synchronously, without throwing. So the exact same `RedisCacheService` subclass, wired through
`Benzene.Microsoft.Dependencies`, crashes on disposal, while wired through `Benzene.Autofac`, disposes
cleanly. This is the same "DI adapters diverge on a documented-as-identical contract" pattern already
seen at #82-85/#210, and the same root defect class as the already-fixed #85
(`AutofacServiceResolverFactory` itself used to be missing `IAsyncDisposable`) and the round-15 fix to
`InternallyOwnedRateLimiterHolder<TContext>` — except here it sits in a type this round owns
(`Benzene.Cache.Redis`) that neither of those fixes touched, and it's reachable through an ordinary,
documented usage pattern (subclass `RedisCacheService`, register it, let the container own it) rather
than an internal implementation detail.

### Why this wasn't caught by existing tests

`test/Benzene.Core.Test/Cache/Redis/RedisCacheServiceTest.cs`'s `HealthCheckTest`/`FailedHealthCheckTest`
register `TestRedisCacheService` as `AddScoped` and wrap the *root* `ServiceProvider` directly in a
`MicrosoftServiceResolverAdapter` — they never call `CreateScope()` and never dispose anything
(neither the adapter nor the provider). Every other test in that file constructs `TestRedisCacheService`
directly with `new`, bypassing DI-owned disposal entirely. So the suite exercises `DisposeAsync()`
called directly (`DisposeAsync_AfterConnecting_DisposesTheMultiplexerTest` etc.) but never exercises
disposal *through* either DI container, which is the only way this defect can surface.

### Suggested fix direction (not applied — read-only review)

Mirror the pattern this codebase already established twice for exactly this defect class
(`MeshAnnouncer.Dispose()`, and round-15's `InternallyOwnedRateLimiterHolder<TContext>.Dispose()`):
add `IDisposable` to `RedisCacheService`, bridging synchronously to the existing `DisposeAsync()` with
a bounded wait (`DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5))`, swallowing the resulting
`AggregateException` since the underlying disposal is expected to complete promptly). Add regression
coverage for both the `AddScoped`-per-message-scope and `AddSingleton`-container-shutdown paths (the
existing suite has neither), and a parity test alongside the existing `AutofacDIParityTest` /
`MicrosoftDITest` pair asserting the two adapters dispose an `IAsyncDisposable`-only container-owned
service identically.

## Other areas reviewed — no additional finding clearing the bar

- **`Benzene.RateLimiting`**: `RateLimitingMiddleware<TContext>` and
  `PartitionedRateLimitingMiddleware<TContext>` are also `IAsyncDisposable`-only, but neither is ever
  registered with a DI container — `MiddlewarePipeline<TContext>.CreateChain` constructs them fresh via
  `new` on every message and nothing disposes them automatically (documented as such in
  `RateLimitingMiddleware<TContext>.DisposeAsync`'s own remarks), so the container-disposal hazard
  that hits `RedisCacheService` does not apply to them. `InternallyOwnedRateLimiterHolder<TContext>`
  (the one type in this package that *is* container-owned) already has both `IDisposable` and
  `IAsyncDisposable` from the round-15 fix; re-verified it's correct as-is.
- **`Benzene.Autofac`**: `AutofacServiceResolverFactory` correctly implements both interfaces (#85);
  no further divergence found beyond the one documented above, which is a defect in the *consumed*
  type (`RedisCacheService`), not in either adapter itself — both adapters behave exactly as designed
  for the type they were given, they just disagree on what a badly-shaped type does to them.
- **Cache negative-caching + serializer interaction (`Benzene.Cache.Core`/`Benzene.Xml`)**: traced the
  scenario the assignment specifically flagged — `Benzene.Xml`'s `XmlSerializer.Serialize(null)`
  returns `""` (documented, deliberate), which is the exact shape `CacheEntry.TryReadEntryAsync`'s
  `#201` fix comment warns a "serializer that encodes null as \"\"" would silently break presence
  detection for. Traced it through `RedisCacheEntry`/StackExchange.Redis concretely: Redis itself
  distinguishes a missing key (`RedisValue.Null`, converts to C# `null`) from a real stored empty
  string (`RedisValue` for `""`, converts to `""`), so a negative-cached XML-serialized `null`
  (stored as `""`) still reads back as `(found: true, value: default)` correctly — `""` is never
  confused with a genuine miss. No reproducible bug in the one cache backend this codebase ships
  (`Benzene.Cache.Redis`); flagging only as a latent trap for any *future* `ICacheService` backend
  whose own miss/hit signal is `string.IsNullOrEmpty` rather than a true null check (Redis is fine
  precisely because it isn't).
- **`Benzene.Avro`**: schema-evolution edge cases (enum values added on the producer's side that don't
  exist in the consumer's CLR enum, decoded via `Enum.Parse` in `AvroDatumConverter.FromAvroString`)
  produce a raw, unwrapped exception rather than the package's own `AvroSchemaMismatchException` — but
  the package's `CLAUDE.md` already explicitly disclaims all schema evolution ("the reader and writer
  must share the exact same reflected/registered schema... A field removed, added, or reordered... is
  not detected or resolved") and instructs users to keep schemas field-for-field identical or version
  explicitly. An unwrapped exception for a scenario already documented as unsupported/undefined doesn't
  clear this round's bar (a genuine correctness bug against the documented contract) — it's a
  consistency/polish nit at most, not filed as a finding.
- **`Benzene.NewtonsoftJson`**: `Serialize(null)` round-trips through `JsonConvert.SerializeObject`
  the same way as System.Text.Json (`"null"`, 4 chars) — no divergence from the default serializer's
  null handling that the cache layer's presence-detection logic depends on.
- **`Benzene.Microsoft.Dependencies` / `Benzene.Cache.Core` internals**: `RedisCacheService`'s own
  connect/dispose race handling (`GetConnectionTask`/`DisposeAsync`, #141/#146), `CacheHealthCheck`,
  `CacheWriteActions`/`CacheEntry` write-through and lazy-load paths were re-read against the #139/
  #140/#147/#198-201 fixes; found no residue beyond what's already fixed and tested.

## Note on environment

`/workspace/benzene-dotnet` is a shared, actively-mutating checkout (other review agents' concurrent
work repeatedly changed files under `test/Benzene.Core.Test` mid-session, twice breaking a
whole-project build with unrelated missing/incomplete files). Verification builds for this review were
done in an isolated `git worktree` pinned at `28473b0` to get a clean, reproducible result; the
temporary test files described above were deleted from both locations afterwards (not committed, per
instructions).
