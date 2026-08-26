# Caching

Benzene's caching support is a provider-agnostic abstraction (`Benzene.Cache.Core`) with a Redis implementation (`Benzene.Cache.Redis`) that your handlers and services consume directly through dependency injection.

## Overview

Caching in Benzene is **not** a middleware you add to a pipeline with a `.UseCache(...)` call — there is no such extension in the codebase. Instead, `Benzene.Cache.Core` gives you a small set of abstractions for building a cache-backed service:

- `ICacheService` — a marker interface with a single `CanConnectAsync(CancellationToken = default)` method, used to health-check whatever cache backend you're using.
- `ICacheEntry<T>` — represents a single cached value. Supports reading (`GetValueAsync`), writing/invalidating (inherited from `ICacheWriteActions<T>`), and a "lazy load" pattern (`LazyLoadAsync`) that reads from cache first and falls back to a database/service call on a miss.
- `ICacheWriteActions<T>` — write and "write-through" operations (`SetValueAsync`, `WriteThroughAsync`).
- `ICacheInvalidateActions` — invalidate and "write-through invalidate" operations (`InvalidateAsync`, `WriteThroughInvalidateAsync`). `ICacheWriteActions<T>` extends this.
- `CacheUpdateAction` — an enum (`None`, `Set`, `Invalidate`) used to tell a generic `WriteThroughAsync` call what to do with the cache after the underlying write completes.

`Benzene.Cache.Core` also ships abstract base classes (`CacheInvalidateActions`, `CacheWriteActions<T>`, `CacheEntry<T>`) that implement the serialization, logging, and timing boilerplate around these interfaces, so a concrete cache provider only needs to implement the few `protected abstract` methods that actually talk to the backend (get/set/delete a raw string value). `Benzene.Cache.Redis`'s `RedisCacheEntry<T>`, `RedisMultiKeyActions<T>`, and `RedisWildcardActions` are exactly this: internal classes that fill in those abstract methods using `StackExchange.Redis`.

There's also a ready-made health check: `CacheHealthCheck<TCacheService>` (created via `CacheHealthCheckFactory<TCacheService>`), wired into `Benzene.HealthChecks.Core` via the `AddCacheHealthCheck<TCacheService>()` extension on `IHealthCheckBuilder`.

Use this when you want request-scoped or cross-instance caching of message handler results or downstream reads, with an opt-in write-through/invalidate pattern that keeps your handler code declarative (call `LazyLoadAsync`/`WriteThroughAsync` instead of hand-writing "check cache, then call database, then update cache" each time).

## Prerequisites

- A Benzene service using `Benzene.Core.MessageHandlers` (message handlers returning `IBenzeneResult<T>`).
- For distributed caching: a reachable Redis instance (or cluster) and the `StackExchange.Redis` connection details for it.
- `Benzene.Diagnostics`'s `IProcessTimerFactory` registered in DI — see [Installation](#installation) below. This is a hard constructor dependency of `RedisCacheService`, not optional.

## Installation

Add the core abstractions:

```
dotnet add package Benzene.Cache.Core
```

For Redis-backed caching, also add:

```
dotnet add package Benzene.Cache.Redis
```

`Benzene.Cache.Redis` depends on `Benzene.Cache.Core` and `StackExchange.Redis` directly — no other NuGet dependencies are pulled in.

### The `IProcessTimerFactory` dependency

`RedisCacheService`'s constructor requires an `IProcessTimerFactory` (from `Benzene.Diagnostics.Timers`) alongside `ILogger<RedisCacheService>` and `IRedisConnectionFactory`. Every cache operation (`LazyLoadAsync`, `WriteThroughAsync`, `WriteThroughInvalidateAsync`, and Redis's own connection setup) opens a named timer scope through this factory.

If nothing registers an `IProcessTimerFactory` in your `IServiceCollection`, DI resolution of your `RedisCacheService` subclass will fail at runtime. The normal way to satisfy this is to call `AddDiagnostics()` from `Benzene.Diagnostics`, which registers `ActivityProcessTimerFactory` (the default, `Activity`-span-based implementation) as `IProcessTimerFactory`:

```csharp
services.UsingBenzene(x => x
    .AddDiagnostics());
```

If you don't want `Benzene.Diagnostics`'s tracing behavior, you can instead register any other `IProcessTimerFactory` implementation directly — `Benzene.Diagnostics` ships `DebugTimerFactory`, `LoggingProcessTimerFactory`, and `CompositeProcessTimerFactory` for this purpose:

```csharp
services.AddScoped<IProcessTimerFactory>(_ => new DebugTimerFactory());
```

Either way, some `IProcessTimerFactory` registration is mandatory before a `RedisCacheService` subclass can be constructed. This is confirmed by the package's own tests (`test/Benzene.Core.Test/Cache/Redis/RedisCacheServiceTest.cs`), which always register one (`DebugTimerFactory`) before resolving the service under test.

## Basic Usage

Caching is consumed directly by application code — there's no pipeline middleware to add. You subclass `RedisCacheService`, supply Redis connection details, and expose typed cache-entry accessors for the keys you care about:

```csharp
using Benzene.Cache.Core;
using Benzene.Cache.Redis;
using Benzene.Diagnostics.Timers;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public class OrderCacheService : RedisCacheService
{
    public OrderCacheService(
        ILogger<RedisCacheService> logger,
        IProcessTimerFactory processTimerFactory,
        IRedisConnectionFactory connectionFactory)
        : base(logger, processTimerFactory, connectionFactory)
    {
        StartConnection(); // kick off the (lazy) Redis connection eagerly
    }

    protected override Task<ConfigurationOptions> GetConfigurationOptionsAsync()
    {
        return Task.FromResult(ConfigurationOptions.Parse("localhost:6379"));
    }

    public ICacheEntry<Order> GetOrderEntry(Guid orderId) =>
        CreateCacheEntry<Order>($"order:{orderId}");
}
```

Register it in DI and inject it wherever you need it — a message handler, in this example:

```csharp
services.AddScoped<IRedisConnectionFactory, RedisConnectionFactory>();
services.AddScoped<OrderCacheService>();
```

```csharp
[Message("orders:get")]
public class GetOrderMessageHandler : IMessageHandler<GetOrderRequest, Order>
{
    private readonly OrderCacheService _cache;
    private readonly IOrderRepository _orders;

    public GetOrderMessageHandler(OrderCacheService cache, IOrderRepository orders)
    {
        _cache = cache;
        _orders = orders;
    }

    public Task<IBenzeneResult<Order>> HandleAsync(GetOrderRequest message)
    {
        var entry = _cache.GetOrderEntry(message.OrderId);

        return entry.LazyLoadAsync(() => _orders.GetOrderAsync(message.OrderId));
    }
}
```

`LazyLoadAsync` checks the cache first; on a miss it calls your `databaseReadFunc`, and if that result `IsSuccessful`, stores the payload back in the cache before returning it.

## Configuration

### `RedisCacheService`

| Member | Purpose |
| --- | --- |
| `GetConfigurationOptionsAsync()` (abstract) | Supplies the `StackExchange.Redis` `ConfigurationOptions` used to connect. Called once, lazily, the first time the connection is needed (or immediately if `StartConnection()` is called in your constructor). |
| `DefaultCacheLifespan` (virtual, default `TimeSpan.FromMinutes(5)`) | TTL applied to `SetValueAsync` calls that don't pass an explicit `expireIn`. Override to change the default. |
| `Serializer` (`ISerializer`, public) | Shared by every cache entry this service creates. The constructor-injected `ISerializer` if your subclass's constructor forwards one (resolved automatically by DI, since `Benzene.Core.MessageHandlers` already registers one), otherwise a shared `System.Text.Json`-backed default - never a fresh instance per cache entry. |
| `StartConnection()` (protected) | Touches the lazily-created connection `Task` so the connection begins immediately rather than on first use. Optional — call it from your subclass's constructor if you want to fail fast / warm the connection. Throws `ObjectDisposedException` if called after `DisposeAsync()`. |
| `CreateCacheEntry<T>(string key)` (protected) | Returns an `ICacheEntry<T>` (backed by `RedisCacheEntry<T>`) for a single Redis string key. |
| `CreateMultiKeyActions<T>(IEnumerable<string> keys)` (protected) | Returns an `ICacheWriteActions<T>` that sets/invalidates the *same* value across multiple keys at once. |
| `CreatePrefixActions(string prefix)` (protected) | Returns an `ICacheInvalidateActions` that invalidates every key matching `prefix + "*"`. |
| `CreateWildcardActions(string pattern)` (protected) | Returns an `ICacheInvalidateActions` that invalidates every key matching an arbitrary pattern. |

Every cache operation (`CanConnectAsync`, and everything on the entries/actions the `Create...` methods
return) takes an optional trailing `CancellationToken`. A hung connect is bounded by *your* token, not
just StackExchange.Redis's own internal timeouts: `RedisSetup` (the internal connect-and-get-database
step every operation goes through) applies your token via `Task.WaitAsync`, so a client
disconnect/shutdown unblocks the caller even while the underlying connect attempt keeps retrying in the
background for whoever else is (or later becomes) waiting on it.

### `ICacheEntry<T>` / write-through actions

Every member below also takes an optional trailing `CancellationToken cancellationToken = default`
(omitted from the signatures here for brevity) - it flows into the concrete provider's own I/O (for
`Benzene.Cache.Redis`, into every Redis call via `Task.WaitAsync`, since StackExchange.Redis has no
native per-call cancellation) but **not** into your `databaseReadFunc`/`modifyDatabaseFunc` delegates,
which stay `Func<Task<TResult>>` - observe the ambient token yourself inside those the same way you
would calling your database/service directly.

| Member | Purpose |
| --- | --- |
| `GetValueAsync()` | Reads and deserializes the cached value, or `default` on a miss or error (errors are logged, not thrown). |
| `SetValueAsync(T value, TimeSpan? expireIn = null)` | Serializes (via the constructor-injected `ISerializer`, or a shared `System.Text.Json`-backed default) and stores the value, using `expireIn` or the service's `DefaultCacheLifespan`. Storing `default` (e.g. `null` for a reference type) is a legitimate negative-cache write - see below. |
| `InvalidateAsync()` | Deletes the key(s). |
| `LazyLoadAsync(Func<Task<IBenzeneResult<T>>> databaseReadFunc, TimeSpan? expireIn = null)` | Cache-aside read: hit returns from cache; miss calls `databaseReadFunc` and, on a successful result with a non-null `Payload`, caches it (with `expireIn` if given). |
| `WriteThroughAsync(Func<Task<TResult>> modifyDatabaseFunc, TimeSpan? expireIn = null)` | Runs the write, then updates the cache based on the result's `BenzeneResultStatus` (`ok`/`created`/`updated`/`accepted` → `Set`; `deleted` → `Invalidate`; anything else → no cache change). A cache-side failure during the `Set`/`Invalidate` step - an exception, or the provider honestly reporting it didn't happen - is logged and swallowed, never thrown from here or folded into the returned result: the database write already committed and is what the return value represents. |
| `WriteThroughAsync(..., Func<TResult, T> getCacheValue, TimeSpan? expireIn = null)` / `WriteThroughAsync(..., getCacheValue, getCacheAction, TimeSpan? expireIn = null)` | Overloads for when the write's result type isn't `IBenzeneResult<T>` directly, or when you need custom cache-action logic instead of the default status mapping. Same cache-side-failure handling as above. |
| `WriteThroughInvalidateAsync(Func<Task<TResult>> modifyDatabaseFunc)` | Runs the write, and invalidates the cache only if the result `IsSuccessful` — no `Set` path, since there's nothing to cache (e.g. a delete-only operation). Same cache-side-failure handling as `WriteThroughAsync`. |

### Negative caching

A cache hit is decided by whether a value is **present**, never by whether it's `null`. This means a
reference-type `T` you explicitly cache as `null` — `await entry.SetValueAsync(null)` — is a real,
repeatable hit on every subsequent `LazyLoadAsync` call, not a permanent miss that re-runs
`databaseReadFunc` every time (the classic cache-penetration scenario: a flood of lookups for keys that
are legitimately, consistently absent). `LazyLoadAsync` itself stays conservative and does **not** do
this automatically — a successful database read whose `Payload` is `null` is still not written back to
the cache on your behalf, so an ordinary "not found" doesn't silently start being cached. Decide for
yourself when a null result is safe/worth negative-caching, and call `SetValueAsync(default, ...)`
once you've made that call:

```csharp
var result = await _orders.GetOrderAsync(orderId); // IBenzeneResult<Order?>
if (result.IsSuccessful && result.Payload is null)
{
    // A genuine, expected "no such order" - safe to negative-cache for a short TTL.
    await entry.SetValueAsync(null, TimeSpan.FromSeconds(30));
}
```

### Health check

```csharp
.UseHealthCheck("healthcheck", x => x
    .AddCacheHealthCheck<OrderCacheService>())
```

`AddCacheHealthCheck<TCacheService>()` (from `Benzene.Cache.Core`) registers a `CacheHealthCheckFactory<TCacheService>` that resolves your cache service from DI and calls its `CanConnectAsync()`. For `RedisCacheService`, `CanConnectAsync()` runs a Redis `PING`. The check reports `Type` `"Cache"` and a `CanConnect` boolean in its `Data`.

## Advanced Usage

### Multi-key writes

If the same value needs to live under more than one key (e.g. a primary key and a secondary lookup key), use `CreateMultiKeyActions`:

```csharp
public ICacheWriteActions<Order> GetOrderMultiKeyActions(Guid orderId, string externalRef) =>
    CreateMultiKeyActions<Order>(new[] { $"order:{orderId}", $"order:ref:{externalRef}" });
```

`SetValueAsync`/`InvalidateAsync` on the result apply to every key in the set; the operation reports
success if *any* key was updated/deleted. `SetValueAsync` issues each key's write concurrently and
independently - a failure on one key (an exception, or Redis reporting it didn't happen) never stops
the others from being attempted, and is logged without affecting the rest. `InvalidateAsync` issues a
single atomic multi-key `DEL` command for every key at once, rather than one command per key.

### Prefix and wildcard invalidation

```csharp
public ICacheInvalidateActions GetAllOrdersInvalidation() => CreatePrefixActions("order:");
```

Internally this runs a Redis `KEYS <pattern>` command and deletes the matches in batches (capped at 1,048,000 keys per batch). `KEYS` is an O(N) scan of the whole keyspace on the Redis side — be cautious about using prefix/wildcard invalidation against a large, busy production Redis instance, since it can block other commands while it runs.

### Custom cache-action mapping

The three-argument `WriteThroughAsync` overload lets you decide, per result, whether to `Set`, `Invalidate`, or leave the cache alone — useful when the default `BenzeneResultStatus` → `CacheUpdateAction` mapping (`ok`/`created`/`updated`/`accepted` → `Set`, `deleted` → `Invalidate`) doesn't fit your handler:

```csharp
await entry.WriteThroughAsync(
    () => _orders.ArchiveOrderAsync(orderId),
    result => result.Payload,
    result => result.Status == BenzeneResultStatus.Ok ? CacheUpdateAction.Invalidate : CacheUpdateAction.None);
```

### A different cache backend

Because `Benzene.Cache.Core`'s `CacheEntry<T>`/`CacheWriteActions<T>`/`CacheInvalidateActions` do all the serialization, logging, and write-through/lazy-load orchestration, adding support for a backend other than Redis means implementing just the raw storage primitives:

```csharp
protected abstract Task<string?> GetEntryValueAsync(CancellationToken cancellationToken);
protected abstract Task<bool> SetEntryValueAsync(string value, TimeSpan? expireIn, CancellationToken cancellationToken);
protected abstract Task<bool> InvalidateEntryAsync(CancellationToken cancellationToken);
```

along with `Logger`, `ProcessTimerFactory`, and `KeyDescription` (used for log messages). `Benzene.Cache.Redis`'s `RedisCacheEntry<T>` is a complete, minimal example of this.

## Examples

See [Basic Usage](#basic-usage) for a full worked example (custom `RedisCacheService` subclass + message handler using `LazyLoadAsync`), and [Advanced Usage](#advanced-usage) for multi-key and wildcard invalidation.

## Troubleshooting

**DI fails to resolve my cache service with a missing-service error for `IProcessTimerFactory`.**
Register one — either `services.UsingBenzene(x => x.AddDiagnostics())` (from `Benzene.Diagnostics`) or a specific `IProcessTimerFactory` implementation directly (`DebugTimerFactory`, `LoggingProcessTimerFactory`, etc.). See [Installation](#installation).

**`GetValueAsync()` always returns `default`, even though I know the key exists.**
Read errors are caught and logged (at `Error` level for `CacheEntry<T>.GetValueAsync`, `Warning` for `RedisCacheEntry<T>.GetEntryValueAsync`) rather than thrown, so a connectivity or serialization problem will silently look like a cache miss. Check your logs for "Error occurred when trying to read from cache" / "Error getting value from cache".

**`InvalidateAsync()` on a prefix/wildcard entry returns `false` even though I expect keys to exist.**
`RedisWildcardActions` returns `true` only if at least one key was actually deleted, and swallows connection errors (logged as a warning) by returning `false` rather than throwing. Check for "Error deleting keys from cache" in your logs.

**A write-through call returns the successful database result, but the cache doesn't seem to have updated.**
`WriteThroughAsync`/`WriteThroughInvalidateAsync` never let a cache-side failure turn an already-committed database write into a failure result — the database write is the source of truth. If the cache-side `Set`/`Invalidate` step itself failed (threw, or the provider honestly reported `false`), it's logged as a `Warning` ("Cache set/invalidate failed/threw for key ... after the database write already succeeded") rather than surfaced any other way. Check your logs for that message.

**`RedisSetup`/`CanConnectAsync`/a cache operation throws `ObjectDisposedException`.**
The owning `RedisCacheService` has already had `DisposeAsync()` called on it — resolve/inject a live instance instead of one whose scope has ended (e.g. a captured singleton dependency outliving a scoped `RedisCacheService`, or a manually-disposed instance reused afterwards).

## See Also

- [Health Checks](health-checks.md)
- [Monitoring & Diagnostics](monitoring.md) — for `IProcessTimerFactory` / `AddDiagnostics()`
- [Message Results](message-result.md) — for `IBenzeneResult<T>` / `BenzeneResultStatus`
- [Resilience](resilience.md)
