# Round 18 review — HealthChecks, Cache, RateLimiting, Resilience, non-OIDC Auth

Scope: `src/Benzene.HealthChecks`, `Benzene.HealthChecks.Core`, `.Disk`, `.Http`, `.Schema`, `.Tcp`,
`Benzene.Cache.Core`, `Benzene.Cache.Redis`, `Benzene.RateLimiting`, `Benzene.Resilience`,
`Benzene.Resilience.Polly`, `Benzene.Auth.Basic`, `Benzene.Auth.Core`, `Benzene.Auth.OAuth2`,
`Benzene.Clients.HealthChecks`, `Benzene.Diagnostics`. Reviewed against `main` at `7f642b2`.

Read-only review — no source files modified. No dotnet SDK is available in this environment, so
nothing here was compiled or executed; every finding is traced by manual code reading plus, where
useful, `git log -p`/`git show` archaeology to establish exactly which commit introduced or removed a
given behaviour. Each finding below names the regression test that would prove it for a future round
with CI access.

Read `work/outstanding-bugs.md` (rounds 1–17, all `[RESOLVED]`/`[DECISION]` entries touching this
territory), `work/review-round16-infrastructure-2026-08.md` (this exact territory, prior round),
`work/review-round17-auth-security-2026-08.md`, `work/review-round17-reliability-2026-08.md`, and
`work/review-round17-grpc-healthchecks-2026-08.md` before starting, specifically to avoid re-reporting
anything already tracked and to know what "clean" already looks like for Auth and the gRPC health
bridge.

## Headline findings

1. **[HIGH] `CacheEntry<T>.LazyLoadAsync`'s cache-aside write-back is unprotected** — the identical
   defect class as #139 (fixed for `WriteThroughAsync` in round 11), never fixed here.
2. **[HIGH] `Benzene.RateLimiting`'s `OwnedRateLimiter` (the #249 fix) is `IAsyncDisposable`-only** —
   the exact "throws on synchronous container disposal" bug class round 16 found and fixed for
   `RedisCacheService`, reintroduced in this package by the very commit that fixed the *other*
   rate-limiter disposal bug, and never caught since.

Two lower-severity findings follow (an info-disclosure convention break in
`Benzene.Clients.HealthChecks`, and a cancellation-classification gap in an inline health-check
helper), plus re-verification results for the two areas the assignment specifically flagged
(`Benzene.Resilience.Polly`'s concurrent-attempt guard, and Auth.Basic/OAuth2 timing safety) — both
confirmed correct on this pass.

---

## Finding 1 — `CacheEntry<T>.LazyLoadAsync`'s write-back can turn an already-successful database read into a thrown exception (the #139 defect class, missed for this call site)

**Severity: high.** This is precisely the scenario the assignment asked about: "does a transient
[cache] blip take down callers that should degrade gracefully?" For the write-back path specifically,
the answer is: not always.

### The defect

`src/Benzene.Cache.Core/CacheEntry.cs:119-122`:

```csharp
if (benzeneResult.IsSuccessful && benzeneResult.Payload is not null)
{
    await SetValueAsync(benzeneResult.Payload, expireIn, cancellationToken);
}
```

This is `LazyLoadAsync`'s cache-aside write-back on a miss, called with **no try/catch**. Compare
`CacheWriteActions<T>.SetValueAsync` (`src/Benzene.Cache.Core/CacheWriteActions.cs:46-51`):

```csharp
public async Task<bool> SetValueAsync(T value, TimeSpan? expireIn = null, CancellationToken cancellationToken = default)
{
    Logger.LogDebug("Setting cache for key {key}", KeyDescription);
    var cacheValue = Serializer.Serialize(value);
    return await SetEntryValueAsync(cacheValue, expireIn, cancellationToken);
}
```

`Serializer.Serialize(value)` runs **before** control ever reaches a concrete provider's
`SetEntryValueAsync` override. `Benzene.Cache.Redis`'s `RedisCacheEntry<T>.SetEntryValueAsync`
(`src/Benzene.Cache.Redis/RedisCacheEntry.cs:47-63`) does catch and swallow its own I/O exceptions
(a genuine Redis connection drop during the `StringSetAsync` call is caught, logged, and returns
`false`) — so a plain Redis blip during the write-back does **not** crash `LazyLoadAsync` today. But
a `Serializer.Serialize` failure happens one level up, entirely inside `Benzene.Cache.Core`, before any
provider-specific try/catch is reached — and this is not a hypothetical: it is the *literal motivating
example* #139's own fix comment names ("e.g. `Serializer.Serialize` failing inside `SetValueAsync`,
called from `WriteThroughAsync`'s `Set` branch", `work/outstanding-bugs.md` line ~2079).

#139 fixed exactly this for `WriteThroughAsync`'s `Set` branch by routing the **entire** `SetValueAsync`
call (serialize + I/O together) through `SyncCacheAfterWriteAsync`
(`src/Benzene.Cache.Core/CacheWriteActions.cs:126-131`):

```csharp
if (cacheValue is not null)
{
    // The database write already committed - see SyncCacheAfterWriteAsync (#139): a
    // cache-side failure here must not surface as this operation's own failure.
    await SyncCacheAfterWriteAsync(ct => SetValueAsync(cacheValue, expireIn, ct), "set", cancellationToken);
}
```

`SyncCacheAfterWriteAsync` (`CacheInvalidateActions.cs:56-74`) is `private protected` — visible to
`CacheEntry<T>` (a subclass of `CacheWriteActions<T>` of `CacheInvalidateActions`) exactly as much as
to `CacheWriteActions<T>` itself. `LazyLoadAsync` simply never calls it. #139/#199's own fix history
protected `WriteThroughAsync` and its 3-arg overload's mapping delegates; the cache-aside write-back in
`LazyLoadAsync` — a third call site of the identical "cache write happens after work has already
succeeded" shape — was never brought under the same protection.

### Concrete failure scenario

1. A handler calls `entry.LazyLoadAsync(fetchFromDatabase)` (the package's headline cache-aside API).
2. Cache miss (key absent, or the cache is transiently unreachable — `GetValueAsync`'s read path
   already degrades misses/read-failures to a miss correctly, per the package's own documented
   guarantee).
3. `fetchFromDatabase()` runs and succeeds, producing a real, non-null `Payload`.
4. `SetValueAsync(payload, ...)` is invoked to warm the cache. If the configured `ISerializer` throws
   for this payload (a type it can't handle, a custom serializer bug, a payload containing something
   the specific serialization library rejects) — or, for any *other* `ICacheEntry<T>` provider
   (this codebase ships only `Benzene.Cache.Redis`, but the interface is provider-agnostic and
   `Benzene.Cache.Core`'s own `CLAUDE.md` documents it as the base layer "concrete providers extend")
   whose own `SetEntryValueAsync` does not independently swallow its I/O exceptions — that exception
   propagates straight out of `LazyLoadAsync`.
5. The caller, who successfully got real data from the database two lines ago, now receives an
   unhandled exception instead of the successful `IBenzeneResult<T>` — directly contradicting the
   package's own documented philosophy ("Read failures degrade to a miss... a cache outage doesn't
   fail the request", `Benzene.Cache.Core/CLAUDE.md`) and the sibling `WriteThroughAsync`'s already-
   fixed guarantee for the identical defect class.

### Why this wasn't caught

`test/Benzene.Core.Test/Cache/CacheEntryTest.cs`'s `FakeCacheEntry<T>` test double has a `ThrowOnSet`
flag (line ~26) used by exactly one test,
`WriteThroughAsync_SetAction_CacheWriteThrows_StillReturnsTheSuccessfulDatabaseResult` (line 392) — it
is never exercised against `LazyLoadAsync`. Every `LazyLoadAsync_*` test in the file uses a clean
in-memory store with no injected write failure, so this call site's own exception-propagation behaviour
has simply never been tested.

### Suggested fix direction (not applied — read-only review)

Route `LazyLoadAsync`'s write-back through `SyncCacheAfterWriteAsync` the same way `WriteThroughAsync`
already does:

```csharp
if (benzeneResult.IsSuccessful && benzeneResult.Payload is not null)
{
    await SyncCacheAfterWriteAsync(ct => SetValueAsync(benzeneResult.Payload, expireIn, ct), "set", cancellationToken);
}
```

(`SyncCacheAfterWriteAsync` is already visible to `CacheEntry<T>` via `private protected`, so this is a
same-file, no-new-surface change.) Add a regression test mirroring the existing
`WriteThroughAsync_SetAction_CacheWriteThrows_StillReturnsTheSuccessfulDatabaseResult`, but against
`LazyLoadAsync`:

```csharp
[Fact]
public async Task LazyLoadAsync_CacheMiss_WriteBackThrows_StillReturnsTheSuccessfulDatabaseResult()
{
    var store = new Dictionary<string, string>();
    var entry = new FakeCacheEntry<string>(store) { ThrowOnSet = true };

    var result = await entry.LazyLoadAsync(() => Task.FromResult(BenzeneResult.Ok("from-database")));

    Assert.True(result.IsSuccessful);
    Assert.Equal("from-database", result.Payload);
}
```

This test fails today (the exception `FakeCacheEntry.SetEntryValueAsync` throws when `ThrowOnSet` is
true propagates straight out of `LazyLoadAsync`, uncaught) and would pass once the fix above lands.

---

## Finding 2 — `Benzene.RateLimiting`'s `OwnedRateLimiter` reintroduces the exact `IAsyncDisposable`-only-throws-on-sync-`Dispose()` bug round 16 fixed for `RedisCacheService` — and it was never fixed here

**Severity: high.** This is the specific re-verification the assignment asked for: "does the limiter
disposal actually work correctly under the Microsoft DI container's synchronous scope disposal in
every registration path, not just the one test covers?" The answer is **no** for the internally-created
convenience entry points.

### The defect

`src/Benzene.RateLimiting/OwnedRateLimiter.cs:11`:

```csharp
internal sealed class OwnedRateLimiter : IAsyncDisposable
```

Only `IAsyncDisposable` — no `IDisposable`. This type is registered as a DI **factory singleton**
(`src/Benzene.RateLimiting/Extensions.cs:314`):

```csharp
var owned = new OwnedRateLimiter(rateLimiter);
app.Register(x => x.AddSingleton<OwnedRateLimiter>(_ => owned));
```

— by `UseFixedWindowRateLimiting`/`UseTokenBucketRateLimiting`/`UsePayloadSizeRateLimiting` (the three
documented, headline convenience entry points; not the BYO `UseRateLimiting`/
`UsePartitionedRateLimiting` overloads, which register nothing with DI). This is *exactly* the shape
round 16 proved throws: "Microsoft.Extensions.DependencyInjection's `ServiceProvider`/
`ServiceProviderEngineScope.Dispose()` throws `InvalidOperationException` when it has to dispose a
container-tracked instance (singleton or scoped) that implements only `IAsyncDisposable`"
(`work/review-round16-infrastructure-2026-08.md`), reproduced there for `RedisCacheService` and fixed
by giving that type its own synchronous `Dispose()` bridge (still present today,
`src/Benzene.Cache.Redis/RedisCacheService.cs:215-224` — re-verified correct on this pass).
`OwnedRateLimiter` never got the same treatment.

### This is a regression, not an oversight from day one — confirmed via `git log`

Round 12–13's #200 fix (`df355a1`) originally solved the internally-created-limiter DI-collision bug
with a type named `InternallyOwnedRateLimiterHolder<TContext>` — also `IAsyncDisposable`-only at that
point. Round 16's own infrastructure review recorded that this earlier type "already has both
`IDisposable` and `IAsyncDisposable` from the round-15 fix; re-verified it's correct as-is"
(`work/review-round16-infrastructure-2026-08.md`, "Other areas reviewed"). Rounds 14–15's #249 fix
(`69199ef`, "WP-B: rate-limiter disposal regression, Polly cancellation, doc/cache fixes"), landed to
fix a **different** bug — the limiter's disposal being unreachable through the public API at all —
**replaced** `InternallyOwnedRateLimiterHolder<TContext>` with the current `OwnedRateLimiter`, and in
doing so the synchronous `IDisposable` bridge did not carry over:

```
$ git log --all --oneline -S"InternallyOwnedRateLimiterHolder" -- .
df355a1 WP-J: fix Cache + RateLimiting round-13 residue (#198-#202)   <- introduced, IAsyncDisposable-only
$ git log --oneline --all -- src/Benzene.RateLimiting/OwnedRateLimiter.cs
69199ef WP-B: rate-limiter disposal regression, Polly cancellation, doc/cache fixes (#249-#252)  <- only commit; introduced OwnedRateLimiter, IAsyncDisposable-only, from scratch
```

`OwnedRateLimiter.cs` has exactly one commit in its history. Whatever gave the earlier
`InternallyOwnedRateLimiterHolder<TContext>` its `IDisposable` bridge (round 16's review states it was
present) never carried forward into the type that superseded it. This package's own `#249` disposal
fix, ironically the fix whose entire purpose was "make disposal actually reachable," reintroduced the
"reachable but throws" flavour of the same underlying bug class in the process.

### Concrete failure scenario

This package's own `CLAUDE.md` documents its headline use case as protecting endpoints "a service
can't avoid exposing publicly (health checks, spec)" — precisely the `UseHealthCheck`/`UseSpec`
scenario, wired via `UseFixedWindowRateLimiting` et al. `Benzene.Microsoft.Dependencies` (this
package's primary DI integration, per its own `CLAUDE.md`: "Standard choice for modern .NET
applications") is typically wired against a host-owned `IServiceProvider` — ASP.NET Core's
`WebApplicationBuilder.Build()`, or a Generic Host `HostBuilder.Build()` — **not** one Benzene's own
`MicrosoftServiceResolverFactory` constructs and owns itself. `MicrosoftServiceResolverFactory`'s own
sync/async disposal bridge (#266, `src/Benzene.Microsoft.Dependencies/MicrosoftServiceResolverFactory.cs:72-96`)
only fires when `_ownsServiceProvider` is `true` — the constructor overload that builds its own
provider from an `IServiceCollection` (the AWS Lambda self-hosting pattern). For the externally-
supplied-provider constructor (`MicrosoftServiceResolverFactory(IServiceProvider serviceProvider)`,
`_ownsServiceProvider = false`), Benzene's own `Dispose()`/`DisposeAsync()` are **no-ops on the
provider** — disposal of the root `IServiceProvider` is entirely up to whoever built and owns it (the
ASP.NET Core/Generic Host runtime, in the common case). If that host's own shutdown path disposes its
root provider **synchronously** — the classic `using var app = builder.Build(); app.Run();` /
`using var host = builder.Build(); host.Run();` pattern still shown throughout ordinary .NET hosting
code, or any environment that calls `((IDisposable)serviceProvider).Dispose()` — then Microsoft.
Extensions.DependencyInjection's real `ServiceProvider.Dispose()` throws `InvalidOperationException`
the moment it reaches the `OwnedRateLimiter` singleton it constructed, purely because the app used one
of this package's own three headline convenience entry points.

### Why this wasn't caught

`test/Benzene.Core.Test/Plugins/RateLimiting/RateLimitingPipelineTest.cs`'s
`InternallyCreatedLimiter_ReachableViaPublicApi_IsDisposedWhenTheContainerIsDisposed` (line ~285, the
dedicated #249 regression test, and the *only* test in the suite that disposes a container after
`UseFixedWindowRateLimiting` has forced `OwnedRateLimiter`'s resolution) disposes the root provider with
`await provider.DisposeAsync()` (line 335) — the **async** path, on the raw `ServiceProvider` object
directly (never even routed through Benzene's own `MicrosoftServiceResolverFactory.Dispose()`/
`DisposeAsync()`). `ServiceProvider.DisposeAsync()` disposes every tracked instance preferring
`IAsyncDisposable.DisposeAsync()` when available — it does **not** hit the code path that throws for an
`IAsyncDisposable`-only singleton (that is specific to the **synchronous** `ServiceProvider.Dispose()`
entry point). Every other test in the file that exercises disposal (`CreateApp`'s own helper, line 34)
likewise only ever builds/wraps a provider via `MicrosoftServiceResolverFactory(IServiceProvider)` and
never disposes it. No test in this suite calls `((IDisposable)provider).Dispose()` on the container that
owns an internally-created limiter.

### Verified reproduction (traced, not run — no dotnet SDK in this environment)

The repro shape is identical to round 16's own already-twice-proven pattern (`RedisCacheService`,
Autofac-vs-Microsoft-DI parity test) — restated here for `OwnedRateLimiter`'s exact shape:

```csharp
private sealed class AsyncOnlyDisposableProbe : IAsyncDisposable
{
    public bool Disposed;
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

[Fact]
public void FactoryRegisteredAsyncOnlySingleton_SynchronousProviderDispose_ThrowsInvalidOperationException()
{
    // Mirrors OwnedRateLimiter's exact registration shape: x.AddSingleton<OwnedRateLimiter>(_ => owned)
    var services = new ServiceCollection();
    services.AddSingleton<AsyncOnlyDisposableProbe>(_ => new AsyncOnlyDisposableProbe());
    var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<AsyncOnlyDisposableProbe>(); // force construction/disposal-tracking

    var ex = Record.Exception(() => ((IDisposable)provider).Dispose());

    Assert.NotNull(ex);
    Assert.IsType<InvalidOperationException>(ex); // reproduces today for this shape
}
```

A more direct, public-API-only repro against the actual package (recommended for the fix's own
regression test): take `InternallyCreatedLimiter_ReachableViaPublicApi_IsDisposedWhenTheContainerIsDisposed`
verbatim and change line 335 from `await provider.DisposeAsync();` to
`((IDisposable)provider).Dispose();` — this is expected to throw `InvalidOperationException` on current
`main`, and to pass cleanly once `OwnedRateLimiter` gets an `IDisposable` bridge.

### Suggested fix direction (not applied — read-only review)

Give `OwnedRateLimiter` the same synchronous bridge this codebase has now established three times over
(`MeshAnnouncer.Dispose()`, `InternallyOwnedRateLimiterHolder<TContext>`'s own prior fix, and
`RedisCacheService.Dispose()`, all bounded — the work being waited on is this type's own prompt,
local `RateLimiter.DisposeAsync()`, not arbitrary user code):

```csharp
internal sealed class OwnedRateLimiter : IAsyncDisposable, IDisposable
{
    public RateLimiter RateLimiter { get; }
    public OwnedRateLimiter(RateLimiter rateLimiter) => RateLimiter = rateLimiter;
    public ValueTask DisposeAsync() => RateLimiter.DisposeAsync();
    public void Dispose() => DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
}
```

Given this is the *third* time this exact pattern has needed fixing in this codebase, it may be worth a
standing rule (a CLAUDE.md note, or even a Roslyn analyzer) rather than relying on each new
`IAsyncDisposable`-only container-owned type being individually remembered.

---

## Re-verification — `Benzene.Resilience.Polly`'s concurrent-attempt guard (#267/#288)

**Confirmed correct**, traced under genuine concurrent execution (not just re-reading the fix).

`PollyResilienceMiddleware<TContext>.HandleAsync` (`src/Benzene.Resilience.Polly/PollyResilienceMiddleware.cs:96-146`)
guards re-entrancy with a shared `int[1]` counter, checked via `Interlocked.Increment` **before** any
shared mutable state (the ambient `CancellationTokenAccessor`, the `TContext`) is touched:

```csharp
if (Interlocked.Increment(ref state.inFlight[0]) > 1)
{
    Interlocked.Decrement(ref state.inFlight[0]); // #288: paired, so a rejected attempt never
                                                    // permanently poisons a later sequential attempt
    throw new NotSupportedException(...);
}
```

Traced the race directly: `Interlocked.Increment` assigns each concurrent caller a distinct,
monotonically-increasing integer regardless of scheduling order — exactly one attempt ever observes the
value `1` (proceeds); every other concurrent attempt observes `> 1`, decrements back, and throws
*before* it ever reads or writes the shared accessor/context. This closes the two failure modes #267
originally found (`next()` running twice; the shared accessor torn mid-flight) for **any** number of
concurrent attempts, not just two — the earlier attempt already holding the counter at `1` is what every
later concurrent attempt correctly detects, however many race in. The paired decrement-before-throw
(#288, added after an earlier version of this guard left the counter permanently poisoned once tripped)
means a genuinely sequential strategy (e.g. an outer `Retry` retrying after a concurrent-strategy
rejection elsewhere in the same pipeline) is not wrongly blocked by a stale counter value.

One residual,_already-documented_ property worth restating rather than re-filing: when a concurrent
strategy's "winning" attempt happens to complete its own `next()` call successfully before the guard
rejects the loser, that successful side effect has already happened by the time `HandleAsync` as a
whole throws `NotSupportedException` to its caller (confirmed by manual trace of the existing
`ConcurrentDuplicateStrategy` test fixture in `PollyResilienceMiddlewareConcurrentAttemptGuardTest.cs`).
This is inherent to "detect and refuse" rather than "prevent count from ever exceeding one" — it does
not corrupt shared state (the guard's actual job) and concurrent-attempt strategies are explicitly,
repeatedly documented as unsupported for this middleware, so it doesn't clear this round's bar as a new
finding; noting it here only so a future reader doesn't rediscover it as a surprise.

Test coverage (`test/Benzene.Core.Test/Resilience/PollyResilienceMiddlewareConcurrentAttemptGuardTest.cs`)
matches this trace exactly: max observed concurrency inside `next()` is 1, the accessor is never torn,
and `next()` runs at most once per `HandleAsync` call. No new finding here.

---

## Re-verification — Auth.Basic / Auth.OAuth2 timing safety

**Confirmed clean**, consistent with round 17's dedicated adversarial security pass on this exact
surface (`work/review-round17-auth-security-2026-08.md`, items 2 and 5).

- `Benzene.Auth.Basic.BasicAuthMiddleware<TContext>` (`src/Benzene.Auth.Basic/BasicAuthMiddleware.cs`)
  never compares credentials itself — it decodes the RFC 7617 header and delegates entirely to a
  caller-supplied `IBasicAuthCredentialValidator`. The package ships no default implementation by
  design (documented: avoids a hardcoded-credential footgun), so timing safety is deliberately the
  implementer's responsibility. Traced every branch of `HandleAsync`: malformed header, malformed
  base64, missing `:` separator, and validator failure all funnel through the same `ChallengeAsync`
  path with the same generic `"Invalid credentials"`/`"Malformed..."` detail and no early return that
  would let an attacker distinguish "no such user" from "wrong password" from response *shape* (only
  the detail *string* differs, and none of the strings depend on which credential was wrong).
- `Benzene.Auth.OAuth2.OAuth2BearerMiddleware<TContext>` (`src/Benzene.Auth.OAuth2/OAuth2BearerMiddleware.cs:81-105`):
  every validation failure — thrown exception during `ValidateTokenAsync`, or `result.IsValid == false`
  for any reason (bad signature, expired, wrong issuer/audience/algorithm) — collapses to the identical
  generic `"Invalid bearer token"` `Unauthorized` response; the real reason is logged server-side only
  (`_logger`), never returned. `JsonWebTokenHandler.ValidateTokenAsync` itself is Microsoft's own
  constant-effort signature-verification path (not a homegrown comparison), consistent with round 17's
  finding that signature verification here is genuine, not a format check.

No new timing/exception-branch oracle found in either package on this pass.

---

## Finding 3 — `Benzene.Clients.HealthChecks/ClientHealthCheck.cs` leaks the raw exception message, breaking this codebase's otherwise-universal "type name only" health-check convention

**Severity: low/medium** (information disclosure, not a crash or data-corruption bug).

`src/Benzene.Clients.HealthChecks/ClientHealthCheck.cs:64-70`:

```csharp
catch (Exception ex)
{
    // IHealthCheck contract: report expected failures (e.g. connection refused) as a Failed
    // result rather than throwing. The processor's outer wrappers remain the backstop.
    return new HealthCheckResult(HealthCheckStatus.Failed, _serviceName,
        new Dictionary<string, object> { ["reachable"] = false, ["error"] = ex.Message }, dependencies);
}
```

Every other exception-reporting health check in the entire reviewed territory — and, on a broader grep,
in the entire codebase — reports `ex.GetType().Name`, never `ex.Message`, and several do so with an
explicit inline comment naming exactly why:

- `src/Benzene.HealthChecks.Tcp/TcpHealthCheck.cs:77`: `"Error", ex.GetType().Name` — "Report the
  failure type, not the message (a message can carry infra detail)".
- `src/Benzene.HealthChecks.Http/HttpPingHealthCheck.cs:86`: `"Exception", ex.GetType().Name` — "Report
  the exception's type name only, never its message (it can carry connection details)".
- `src/Benzene.HealthChecks.Disk/DiskHealthCheck.cs:64`, `src/Benzene.HealthChecks.DynamoDb/DynamoDbHealthCheck.cs:57`,
  `src/Benzene.HealthChecks.EntityFramework/DatabaseHealthCheck.cs:64-65`,
  `src/Benzene.HealthChecks/HealthCheckBuilderExtensions.cs:117,146` (with the doc comment: "its
  exception *type* is recorded under `Data["Error"]` — never the message, which may carry secrets"),
  `src/Benzene.HealthChecks/MemoryHealthCheck.cs:75`, `src/Benzene.HealthChecks/TimeOutHealthCheck.cs:70`,
  `src/Benzene.HealthChecks/ExceptionHandlingHealthCheck.cs:52`,
  `src/Benzene.HealthChecks.Core/HealthCheckError.cs:107` — all the same pattern.

`ClientHealthCheck` is the single outlier across the whole grep. This matters specifically here because
this package's own docs describe the `contracts` topic this check is registered on as something that
"can flow out to whoever calls the health check topic with no authorization" (the identical framing
`HttpPingHealthCheck`'s own userinfo-stripping fix uses to justify its own leak-prevention). The
underlying `_client.HealthCheckAsync()` call goes over `IBenzeneMessageSender` — depending on transport,
a thrown exception's `Message` can plausibly carry an internal hostname/IP, a broker connection detail,
a TLS failure reason, or other infrastructure detail that this codebase's own established convention
says should never leave the process.

### Suggested fix (not applied — read-only review)

```csharp
new Dictionary<string, object> { ["reachable"] = false, ["error"] = ex.GetType().Name }
```

Regression test: mirror the existing convention tests for the sibling checks — assert the `error` field
equals `nameof(SomeThrowingException)` and does **not** contain a message string injected via the fake
`IHasHealthCheck`/`IBenzeneMessageSender` double.

---

## Finding 4 — `Benzene.HealthChecks/HealthCheckBuilderExtensions.cs`'s `AddHealthCheck(kind, name, probe)` convenience overloads don't classify `OperationCanceledException` distinctly, unlike every other self-catching health check post-#50/#114

**Severity: low.** Narrow because the affected overloads' `probe` delegate has no `CancellationToken`
parameter of its own, so this only bites a probe that captures cancellation from elsewhere (e.g. an
`IHostApplicationLifetime.ApplicationStopping` token closed over at registration time, or an ambient
accessor resolved inside the closure).

`src/Benzene.HealthChecks/HealthCheckBuilderExtensions.cs:105-118` (the `Func<Task> probe` overload;
the `Func<Task<bool>> probe` overload at lines ~134-147 has the identical shape):

```csharp
try
{
    await probe();
    return HealthCheckResult.CreateInstance(true, name, new Dictionary<string, object>(), dependencies);
}
catch (Exception ex)
{
    return HealthCheckResult.CreateInstance(false, name,
        new Dictionary<string, object> { { "Error", ex.GetType().Name } }, dependencies);
}
```

No `catch (OperationCanceledException) { throw; }` ahead of the broad catch. This is the exact shape
round 11's #50/round 10's #114 swept the codebase for — "every `IHealthCheck` implementer... routes
through `HealthCheckError.Classify` (which re-throws OCE) or has its own explicit catch/rethrow" — and
that sweep explicitly named `Benzene.Clients.HealthChecks/ClientHealthCheck.cs` and
`Benzene.Cache.Core/CacheHealthCheck.cs` as two extra files it found and fixed beyond the original
`IHealthCheck`-implementer grep. `HealthCheckBuilderExtensions.AddHealthCheck(kind, name, probe)` is a
third such self-catching-but-not-`IHealthCheck`-itself site (the actual `IHealthCheck` implementer is
the generic `InlineHealthCheck`, which the #114 sweep's own "grepped every `IHealthCheck`
implementation" methodology would not have surfaced this particular catch inside, since the catch lives
in the caller-side lambda, not in `InlineHealthCheck.ExecuteAsync` itself). Confirmed via grep that
neither `InlineHealthCheck` nor `HealthCheckBuilderExtensions` appears anywhere in
`work/outstanding-bugs.md` — this exact file was never in scope for that sweep.

Consequence: a probe that legitimately observes cancellation (mid-shutdown, or a caller-supplied
timeout token) is reported as an ordinary `Failed`/dead-dependency result rather than
`ExceptionHandlingHealthCheck`'s dedicated `"Cancelled"` outcome — the same "genuinely dead dependency
becomes indistinguishable from a cooperative cancellation" observability gap #50 was fixed to close
everywhere else, just not reachable through this one documented, public, low-ceremony BYO-check
registration path.

### Suggested fix (not applied — read-only review)

```csharp
catch (OperationCanceledException)
{
    throw;
}
catch (Exception ex)
{
    ...
}
```

in both the `Func<Task>` and `Func<Task<bool>>` three-arg `AddHealthCheck` overloads. Regression test:
a probe that throws `OperationCanceledException` should propagate out of the built `InlineHealthCheck`
rather than being reported as `Failed`, mirroring the existing pattern in
`test/Benzene.Core.Test/Cache/Redis/RedisCacheServiceTest.cs`'s Cancelled-classification tests.

---

## Other areas reviewed — no finding clearing the bar

- **`Benzene.HealthChecks.Disk`/`.Http`/`.Tcp`** — all three read cleanly: correct healthy/warning/failed
  thresholding (Disk), correct 200-only success criteria + userinfo stripping (Http), correct
  cancellation-vs-connectivity-failure classification (Tcp, Http; Disk is synchronous, no cancellation
  surface). No change since round 11's WP-K/#50/#114 hardening.
- **`Benzene.HealthChecks.Schema`** — `SchemaHealthCheck`/`ContractHash` alignment with the consumer
  side (`Benzene.Clients.HealthChecks`) re-traced; the shared `SchemaHealthCheckConstants` keeps both
  sides from drifting on a literal key/type string. No issue found.
- **`Benzene.Cache.Redis`'s connection-drop/failover handling otherwise** — `RedisCacheService`'s
  `GetConnectionTask`/`DisposeAsync`/`Dispose` (round-16's #262/#266-fixed IDisposable bridge,
  re-verified present and correct at `RedisCacheService.cs:164-224`), the read-path degrade-to-miss
  behaviour (`RedisCacheEntry.GetEntryValueAsync`, `CacheEntry.TryReadEntryAsync`), and the
  write/invalidate paths' own internal exception swallowing (`RedisCacheEntry.SetEntryValueAsync`/
  `InvalidateEntryAsync`) all correctly degrade a genuine Redis connection blip to a logged warning
  rather than a crash — the one gap is Finding 1 above, which is a `Benzene.Cache.Core` base-class
  defect reachable through any provider, not Redis-specific.
- **`Benzene.RateLimiting` otherwise** — the BYO paths (`UseRateLimiting`, `UsePartitionedRateLimiting`)
  register nothing with DI and are unaffected by Finding 2. The #200 DI-collision fix (closure-captured
  limiter, never resolved from DI for *use*) and the #202/#143/#134 fail-closed disposal/cost-delegate
  handling all re-verified correct on read.
- **`Benzene.Auth.Core`** — `RoleClaims`/`AuthorizationExtensions`/`AuthenticationHolder` re-read against
  round 11's #179/#182 fixes; both still correct (`RequirePolicy` caches and fails hard on a genuinely
  missing policy; role-claim JSON-array expansion is consistent). No new finding.
- **`Benzene.Diagnostics`** — no `IAsyncDisposable` type exists in this package at all (confirmed by
  grep), so the disposal-bridge bug class doesn't apply here. Light pass only, given round 17's own
  dedicated W3C-trace-context/fire-and-forget-span pass on this package found it correct; nothing
  contradicting that surfaced on this pass.

## Note on environment

No dotnet SDK is available in this sandbox — every finding above is a manual code trace (cross-checked
where useful against `git log -p`/`git show` for exact provenance), not a compiled/executed repro.
Findings 1–4's suggested regression tests are written out in full above so a future round with CI
access can drop them in directly and confirm red-then-green.
