# Benzene.Cache.Core

## What this package does
Provider-agnostic caching abstractions plus a cache-aside / write-through base-class layering that
concrete providers (e.g. `Benzene.Cache.Redis`) extend. This package contains **no cache middleware
and no HTTP response caching** — it is a set of interfaces + abstract base classes your handler code
calls directly, plus a health check. Values are JSON-serialized via a constructor-injected
`ISerializer` (pass one through `CacheWriteActions<T>`'s/`CacheEntry<T>`'s constructor - a concrete
provider typically resolves it from DI once and shares it across every entry it creates), falling back
to a shared `CacheSerializerDefaults.Serializer` (`Benzene.Core.MessageHandlers.Serialization.JsonSerializer`-backed)
when none is supplied anywhere.

## Key types/interfaces
- `ICacheService` - marker service exposing only `CanConnectAsync(CancellationToken = default)` (a
  provider connection is supplied by the concrete implementation, e.g. `RedisCacheService`)
- `ICacheEntry<T>` / `ICacheWriteActions<T>` / `ICacheInvalidateActions` - the read/write/invalidate
  contracts a concrete single-/multi-key cache entry implements. Every member takes an optional
  trailing `CancellationToken cancellationToken = default`; `SetValueAsync`/`LazyLoadAsync`/
  `WriteThroughAsync` also take an optional `TimeSpan? expireIn` (flows to the underlying
  `SetValueAsync`/`SetEntryValueAsync` call - `LazyLoadAsync`/`WriteThroughAsync` no longer hard-code
  `DefaultCacheLifespan` for a per-call write).
- `CacheUpdateAction` enum - `None` / `Set` / `Invalidate`
- `CacheHealthCheck<TCacheService>` + `CacheHealthCheckFactory<TCacheService>` and the
  `IHealthCheckBuilder.AddCacheHealthCheck<TCacheService>()` extension
- `CacheSerializerDefaults` - the shared, process-wide default `ISerializer` used when nothing
  constructor-injects one.

## When to use this package
- When implementing a cache provider (subclass the `CacheEntry<T>` layering)
- When your handler needs cache-aside (`LazyLoadAsync`) or write-through (`WriteThroughAsync`) around
  a database read/modify

## Deliberate boundaries (NOT shipped)
- **No stampede / single-flight / dogpile protection.** `CacheEntry<T>.LazyLoadAsync` is a plain
  cache-aside: on a miss every concurrent caller runs `databaseReadFunc` and writes back. There is
  no lock or in-flight coalescing (see `CacheEntry.cs`).
- No cache middleware, no automatic key generation, no HTTP response caching.

## Dependencies on other Benzene packages
- **Benzene.Core.MessageHandlers** - the shared JSON `ISerializer`
- **Benzene.Core** / **Benzene.Results** / **Benzene.Abstractions.Results** - `IBenzeneResult`/status
- **Benzene.Diagnostics** - `IProcessTimerFactory` timing scopes
- **Benzene.HealthChecks.Core** - health-check contracts

## Important conventions
- Read failures degrade to a miss: `CacheEntry<T>.GetValueAsync` swallows and logs a read exception
  rather than propagating, so a cache outage doesn't fail the request. A caller-driven
  `OperationCanceledException` is the one exception excluded from this - it always propagates rather
  than being logged as a miss, everywhere in this package (read, write, invalidate, write-through).
- A cache hit is decided by **presence**, never by whether the deserialized value is itself `null`:
  `CacheEntry<T>.TryReadEntryAsync` decides presence as `cacheValue != null` (#201 - **not**
  `!string.IsNullOrEmpty(cacheValue)`, which conflated "key absent" with "the serializer emitted an
  empty string" and broke negative caching for any `ISerializer` encoding null/default as `""` rather
  than the stock serializer's `"null"`). `LazyLoadAsync` treats an explicitly-cached `null`
  (`SetValueAsync(default)`) as a real, repeatable hit - negative caching - rather than a permanent
  miss that re-runs `databaseReadFunc` forever. It still never writes a `null` `Payload` back
  automatically on a cache miss; a caller opts a null result into the cache itself. **Every concrete
  `GetEntryValueAsync` must genuinely distinguish a store-level miss (`null`) from an empty stored
  value (`""`), including on its own error-handling path** - `RedisCacheEntry.GetEntryValueAsync`'s
  catch block used to return `""` on a thrown exception, which the presence check above would
  otherwise misread as a hit of an empty cached value rather than "the read failed"; it now returns
  `null` there too.
- Write-through's cache-sync step (`Set`/`Invalidate`, run *after* `modifyDatabaseFunc` has already
  committed) never turns an already-successful database write into this operation's own failure: an
  exception or a provider honestly returning `false` is logged (`Warning`) and swallowed by
  `CacheInvalidateActions.SyncCacheAfterWriteAsync`, and the database's own successful result is still
  returned. This covers not just the cache I/O itself but also `WriteThroughAsync`'s own
  caller-supplied `getCacheValue`/`getCacheAction` mapping delegates (#199 - they run inside the same
  `SyncCacheAfterWriteAsync` call as the cache I/O they decide, so a throw from either degrades
  identically instead of propagating as if the database write itself had failed).
  `SetValueAsync`/`InvalidateAsync` themselves are unchanged for a caller invoking them
  directly (outside write-through) - there, an exception is the primary requested action's own failure.
- `CacheHealthCheck<TCacheService>` - an `IHealthCheck` verifying `ICacheService.CanConnectAsync(cancellationToken)`;
  result `Data` includes `CanConnect` and `Error` (the exception's type name, not its message - not a
  connection string or other secret); result `Dependencies` includes one
  `HealthCheckDependency("Cache", typeof(TCacheService).Name)`
- `CacheInvalidateActions` / `CacheWriteActions<T>` / `CacheEntry<T>` - the abstract base-class
  layering every concrete cache entry (e.g. `Benzene.Cache.Redis`'s `RedisCacheEntry<T>`) builds
  on, each adding write-through behavior on top of the last: `CacheInvalidateActions` (delete +
  `WriteThroughInvalidateAsync` - invalidate only when `modifyDatabaseFunc`'s result is
  successful, via `SyncCacheAfterWriteAsync`) → `CacheWriteActions<T>` (adds `SetValueAsync`
  (serializes via the constructor-injected/default `ISerializer`) + three `WriteThroughAsync`
  overloads, the simplest defaulting the cache action from the result's `BenzeneResultStatus` -
  `Ok`/`Created`/`Accepted`/`Updated` → `Set`, `Deleted` → `Invalidate`, anything else → `None`) →
  `CacheEntry<T>` (adds `GetValueAsync` - swallows and logs a read exception rather than
  propagating, so a cache outage degrades to a miss - and `LazyLoadAsync`, which only writes back
  to the cache on a cache miss whose `databaseReadFunc` result is successful). A concrete subclass
  implements only 4 protected members: `Logger`, `ProcessTimerFactory`, `KeyDescription`, and the
  3 `*EntryAsync` primitives (`Get`/`Set`/`Invalidate`, each now taking a `CancellationToken`) the
  layers above call.

## Tests
- `test/Benzene.Core.Test/Cache/CacheHealthCheckTest.cs` - `CacheHealthCheck<TCacheService>`.
- `test/Benzene.Core.Test/Cache/CacheEntryTest.cs` - the `CacheInvalidateActions`/
  `CacheWriteActions<T>`/`CacheEntry<T>` layering, via a `FakeCacheEntry<T>` test double backed by
  an in-memory dictionary (no Redis/network dependency - `Benzene.Cache.Redis`'s
  `RedisCacheEntry<T>` was the only prior concrete subclass and had no dedicated tests either).
  Covers: `GetValueAsync` hit/miss/underlying-read-throws (swallowed, logged, returns default)/
  underlying-read-throws-`OperationCanceledException` (propagates); `SetValueAsync`/`InvalidateAsync`;
  `LazyLoadAsync`'s hit-skips-database-call vs. miss-calls-database-and-writes-back-only-on-success
  branches, the value-type miss-as-hit regression, the reference-type explicitly-cached-null-is-a-hit
  case (negative caching), and per-call `expireIn` threading; all three `WriteThroughAsync` overloads
  (default `BenzeneResultStatus`-derived action mapping for `Ok`/`Deleted`/`NotFound`, a custom
  cache-value mapping, a custom cache-action mapping, per-call `expireIn` threading, a cache-side
  exception on the `Set`/`Invalidate` step not failing the already-successful database result, and -
  #199 - a throwing `getCacheValue`/`getCacheAction` delegate degrading the same way rather than
  propagating as if the database write had failed, with a caller-driven `OperationCanceledException`
  from either delegate still propagating); a custom `ISerializer` that encodes null as `""` still
  getting a real negative-cache hit through `LazyLoadAsync` (#201); and `WriteThroughInvalidateAsync`'s
  successful-vs-unsuccessful-result branches plus a cache-side `false` result being logged rather than
  silently discarded.
