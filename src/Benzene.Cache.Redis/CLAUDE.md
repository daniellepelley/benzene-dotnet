# Benzene.Cache.Redis

## What this package does
Redis-backed implementation of the `Benzene.Cache.Core` abstractions, using StackExchange.Redis for
distributed caching shared across instances.

## Key types/interfaces
- `RedisCacheService` - **abstract** `ICacheService`, `IAsyncDisposable`. You subclass it and
  implement `GetConfigurationOptionsAsync()` (returns a StackExchange.Redis `ConfigurationOptions`);
  its constructor also optionally takes an `ISerializer` (DI-resolved automatically for you when your
  subclass is constructed through DI), shared by every cache entry this service creates, via its
  public `Serializer` property. Holds a lazily-established, cached `IConnectionMultiplexer`;
  `CanConnectAsync(cancellationToken)` issues a `PING`. `DisposeAsync()` disposes that cached
  multiplexer (a no-op if a connect was never started or never completed) - register your subclass so
  its container disposes it on shutdown; after it runs, any further `RedisSetup`/`StartConnection`/
  connect-driven call throws `ObjectDisposedException` rather than silently opening (and leaking) a
  new multiplexer. Factory methods build the concrete entry/action types below. `CreatePrefixActions(prefix)`
  throws `ArgumentException` on a null/empty/whitespace `prefix` (#198) - unescaped, that would
  otherwise silently become the pattern `"*"` and invalidate every key in the database; the
  deliberate route for that is `CreateWildcardActions("*")`, named in the exception message.
- `RedisCacheEntry<T>` (internal) - `CacheEntry<T>` over a single key. `Get`/`Set`/`Invalidate` map
  to `StringGetAsync` / `StringSetAsync` (with TTL) / `KeyDeleteAsync`. A Redis error on the read path
  degrades to `null` (a genuine miss), never `""` (#201) - `CacheEntry<T>`'s presence check is
  `cacheValue is not null`, so returning `""` here would have masqueraded a failed read as a hit of
  an empty cached value.
- `RedisMultiKeyActions<T>` (internal) - write/invalidate the same value across several keys.
  `SetEntryValueAsync` issues each key's `StringSetAsync` concurrently, with each key's outcome
  (success / `false` / a thrown exception) captured independently so one key's failure never stops the
  others from being attempted; `InvalidateEntryAsync` issues one atomic multi-key `KeyDeleteAsync(RedisKey[])`
  rather than a per-key loop.
- `RedisWildcardActions` (internal) - invalidate by pattern via a `KEYS <pattern>` scan then batched
  `KeyDeleteAsync`.
- `IRedisConnectionFactory` / `RedisConnectionFactory` - the `ConnectionMultiplexer.ConnectAsync`
  seam (overridable for tests).

## When to use this package
- When you need caching shared across multiple instances backed by Redis.

## Deliberate boundaries (NOT shipped)
- **No cluster-specific handling.** The client talks to whatever endpoint(s) the
  `ConfigurationOptions` you supply describe; there is no cluster-aware sharding logic in this
  package.
- **No atomic / conditional operations.** Operations are `StringGet`/`StringSet`/`KeyDelete` (plus a
  `KEYS`-based wildcard delete). There is no `SETNX`/Lua/transaction-based atomicity here — so this
  is not a distributed-lock or single-flight primitive (cache-aside stampede caveat lives in
  `Benzene.Cache.Core`).
- The wildcard invalidation uses `KEYS`, which scans the keyspace — use with care on large Redis
  instances.

## Important conventions
- Configuration is supplied as `ConfigurationOptions` from your `GetConfigurationOptionsAsync()`
  override, **not** as a bare connection string on this package's API.
- `DefaultCacheLifespan` defaults to 5 minutes (override in your subclass); `SetEntryValueAsync`
  applies it as the TTL when no explicit expiry is passed (unless a per-call `expireIn` was given -
  see `Benzene.Cache.Core`'s `LazyLoadAsync`/`WriteThroughAsync`).
- Redis errors on get/set/invalidate are caught and logged (returning a miss / `false`) rather than
  thrown, so a Redis outage degrades gracefully. A caller-driven `OperationCanceledException` is the
  one exception excluded from this - it always propagates, never logged as an ordinary Redis failure.
- Every Redis call is wrapped in `Task.WaitAsync(cancellationToken)` - StackExchange.Redis's
  `IDatabase` methods have no native per-call cancellation, so this is the standard way to bound a
  caller's own wait on a task that doesn't support it directly; the underlying Redis operation itself
  keeps running in the background rather than being aborted. `RedisSetup` (the shared connect-and-
  get-database step every operation goes through) applies the same pattern to the memoized connect
  task, but deliberately does **not** cancel the shared task itself (it's awaited by every concurrent
  caller - cancelling it for one caller would break another's unrelated in-flight wait), only each
  caller's own wait on it.

## Dependencies on other Benzene packages
- **Benzene.Cache.Core** - the cache abstractions and base-class layering
- **Benzene.Diagnostics** - `IProcessTimerFactory`
- **StackExchange.Redis** - the Redis client

## Tests
- `test/Benzene.Core.Test/Cache/Redis/RedisCacheServiceTest.cs` - health check, cache-entry
  lazy-load hit/miss, all `WriteThroughAsync`/`WriteThroughInvalidateAsync` shapes, multi-key set/
  invalidate (including one key throwing not stopping the others - #147), prefix/wildcard
  invalidation (including glob-metacharacter escaping - and, since #198, `CreatePrefixActions`
  throwing `ArgumentException` on a null/empty/whitespace prefix rather than silently invalidating
  everything), connect/disconnect/dispose lifecycle (including the `ObjectDisposedException`-after-
  dispose guard - #146 - and cancellation unblocking a hung connect - #141), constructor-injected
  `ISerializer` override (#145), and (#201) a Redis read error degrading to a genuine miss (not a
  false hit of `""`) alongside a stored empty string round-tripping as a real hit.
