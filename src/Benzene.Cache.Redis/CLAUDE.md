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
  new multiplexer. Factory methods build the concrete entry/action types below.
  `CreatePrefixActions(prefix)` throws `ArgumentException` for a null/empty/whitespace `prefix`
  (#198) before ever building the wildcard pattern - an empty prefix would otherwise silently become
  the literal glob `"*"`, matching (and so invalidating) the entire keyspace.
- `RedisCacheEntry<T>` (internal) - `CacheEntry<T>` over a single key. `Get`/`Set`/`Invalidate` map
  to `StringGetAsync` / `StringSetAsync` (with TTL) / `KeyDeleteAsync`. `GetEntryValueAsync` returns
  `null` for both a genuine Redis miss (`StringGetAsync`'s `RedisValue.Null`) **and** its own
  error-handling path (#201 - previously `""`, which `CacheEntry.TryReadEntryAsync`'s
  `cacheValue != null` presence check would otherwise misread as a hit of a genuinely-empty cached
  value rather than "the read failed").
- `RedisMultiKeyActions<T>` (internal) - write/invalidate the same value across several keys.
  `SetEntryValueAsync` issues each key's `StringSetAsync` concurrently, with each key's outcome
  (success / `false` / a thrown exception) captured independently so one key's failure never stops the
  others from being attempted; `InvalidateEntryAsync` issues one atomic multi-key `KeyDeleteAsync(RedisKey[])`
  rather than a per-key loop.
- `RedisWildcardActions` (internal) - invalidate by pattern via a `KEYS <pattern>` scan then batched
  `KeyDeleteAsync`. `InvalidateEntryAsync` refuses to run (throws `InvalidOperationException`, no
  `KEYS` scan issued) for a null/empty/whitespace pattern or one that's - after trimming - composed
  entirely of `*` (Redis glob syntax treats `"*"`/`"**"`/`" * "` identically): defense-in-depth
  against #198 for this type's own `CreateWildcardActions` escape hatch, which passes an unescaped,
  caller-supplied pattern through by design and so isn't covered by `CreatePrefixActions`'s guard.
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
