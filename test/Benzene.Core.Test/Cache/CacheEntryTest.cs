using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Results;
using Benzene.Cache.Core;
using Benzene.Diagnostics.Timers;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Benzene.Test.Cache;

public class CacheEntryTest
{
    private class FakeCacheEntry<T> : CacheEntry<T>
    {
        private readonly Dictionary<string, string> _store;
        private readonly string _key;
        private readonly ILogger _logger;

        public bool ThrowOnGet;
        public bool ThrowOnGetOperationCanceled;
        public bool ThrowOnSet;
        public bool ThrowOnInvalidate;
        public bool FailInvalidate;
        public TimeSpan? LastExpireIn;

        public FakeCacheEntry(Dictionary<string, string> store, string key = "the-key", ILogger? logger = null)
            : this(store, key, logger, serializer: null)
        {
        }

        public FakeCacheEntry(Dictionary<string, string> store, string key, ILogger? logger, Benzene.Abstractions.Serialization.ISerializer? serializer)
            : base(serializer)
        {
            _store = store;
            _key = key;
            _logger = logger ?? NullLogger.Instance;
        }

        protected override ILogger Logger => _logger;

        protected override IProcessTimerFactory ProcessTimerFactory => new DebugTimerFactory();

        protected override string KeyDescription => _key;

        protected override Task<string?> GetEntryValueAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnGetOperationCanceled)
            {
                throw new OperationCanceledException();
            }
            if (ThrowOnGet)
            {
                throw new InvalidOperationException("cache read failed");
            }
            return Task.FromResult(_store.TryGetValue(_key, out var value) ? value : null);
        }

        protected override Task<bool> SetEntryValueAsync(string value, TimeSpan? expireIn, CancellationToken cancellationToken)
        {
            LastExpireIn = expireIn;
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("cache write failed");
            }
            _store[_key] = value;
            return Task.FromResult(true);
        }

        protected override Task<bool> InvalidateEntryAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnInvalidate)
            {
                throw new InvalidOperationException("cache invalidate failed");
            }
            if (FailInvalidate)
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(_store.Remove(_key));
        }
    }

    /// <summary>
    /// Captures <see cref="LogLevel.Warning"/> messages so a test can assert a cache-sync failure was
    /// logged (#139) without depending on any particular logging provider.
    /// </summary>
    private class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task GetValueAsync_KeyPresent_DeserializesTheStoredValue()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        await entry.SetValueAsync("hello");

        var result = await entry.GetValueAsync();

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task GetValueAsync_KeyMissing_ReturnsDefault()
    {
        var entry = new FakeCacheEntry<string>(new Dictionary<string, string>());

        var result = await entry.GetValueAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetValueAsync_UnderlyingReadThrows_LogsAndReturnsDefaultInsteadOfThrowing()
    {
        var entry = new FakeCacheEntry<string>(new Dictionary<string, string>()) { ThrowOnGet = true };

        var result = await entry.GetValueAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task SetValueAsync_SerializesAndStoresTheValue()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store, "my-key");

        var ok = await entry.SetValueAsync("hello");

        Assert.True(ok);
        Assert.True(store.ContainsKey("my-key"));
    }

    [Fact]
    public async Task InvalidateAsync_RemovesTheKey()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store, "my-key");
        await entry.SetValueAsync("hello");

        var removed = await entry.InvalidateAsync();

        Assert.True(removed);
        Assert.False(store.ContainsKey("my-key"));
    }

    [Fact]
    public async Task LazyLoadAsync_CacheHit_ReturnsTheCachedValueWithoutCallingDatabaseFunc()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        await entry.SetValueAsync("cached");
        var databaseFuncCalled = false;

        var result = await entry.LazyLoadAsync(() =>
        {
            databaseFuncCalled = true;
            return Task.FromResult(BenzeneResult.Ok("from-database"));
        });

        Assert.False(databaseFuncCalled);
        Assert.Equal("cached", result.Payload);
    }

    [Fact]
    public async Task LazyLoadAsync_CacheMiss_CallsDatabaseFuncAndWritesTheResultBackOnSuccess()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);

        var result = await entry.LazyLoadAsync(() => Task.FromResult(BenzeneResult.Ok("from-database")));

        Assert.Equal("from-database", result.Payload);
        Assert.True(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task LazyLoadAsync_CacheMiss_DatabaseFuncFails_DoesNotWriteToTheCache()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);

        var result = await entry.LazyLoadAsync(() => Task.FromResult(BenzeneResult.NotFound<string>()));

        Assert.False(result.IsSuccessful);
        Assert.False(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task LazyLoadAsync_ValueType_CacheMiss_CallsDatabaseFuncAndReturnsTheDbValue()
    {
        // Regression: for an unconstrained value-type T, a cold cache returns default(T) (e.g. 0),
        // and `default(int) != null` (boxed) is always true - so LazyLoad used to treat the MISS as a
        // hit, return 0, and never read the database. The hit decision must be presence-based.
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<int>(store);
        var databaseFuncCalled = false;

        var result = await entry.LazyLoadAsync(() =>
        {
            databaseFuncCalled = true;
            return Task.FromResult(BenzeneResult.Ok(42));
        });

        Assert.True(databaseFuncCalled);
        Assert.Equal(42, result.Payload);
        Assert.True(store.ContainsKey("the-key")); // the DB value was written back
    }

    [Fact]
    public async Task LazyLoadAsync_ValueType_CacheHitOfDefaultValue_IsAHitWithoutCallingDb()
    {
        // A genuinely-cached default value (0) is a hit, not a miss - presence, not `!= null`, decides.
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<int>(store);
        await entry.SetValueAsync(0);
        var databaseFuncCalled = false;

        var result = await entry.LazyLoadAsync(() =>
        {
            databaseFuncCalled = true;
            return Task.FromResult(BenzeneResult.Ok(99));
        });

        Assert.False(databaseFuncCalled);
        Assert.Equal(0, result.Payload);
    }

    [Fact]
    public async Task WriteThroughAsync_DefaultMapping_OkResult_SetsTheCache()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);

        await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Ok("new-value")));

        Assert.True(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task WriteThroughAsync_DefaultMapping_DeletedResult_InvalidatesTheCache()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        await entry.SetValueAsync("stale");

        await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Deleted<string>()));

        Assert.False(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task WriteThroughAsync_DefaultMapping_NotFoundResult_LeavesTheCacheUnchanged()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        await entry.SetValueAsync("unchanged");

        await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.NotFound<string>()));

        Assert.Equal("\"unchanged\"", store["the-key"]);
    }

    [Fact]
    public async Task WriteThroughAsync_CustomCacheValueMapping_UsesTheProvidedValue()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);

        await entry.WriteThroughAsync(
            () => Task.FromResult(BenzeneResult.Ok(42)),
            result => $"computed-{result.Payload}");

        Assert.True(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task WriteThroughAsync_CustomCacheActionMapping_UsesTheProvidedAction()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        await entry.SetValueAsync("stale");

        await entry.WriteThroughAsync(
            () => Task.FromResult(BenzeneResult.Ok(42)),
            result => $"computed-{result.Payload}",
            _ => CacheUpdateAction.Invalidate);

        Assert.False(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task WriteThroughInvalidateAsync_SuccessfulResult_InvalidatesTheCache()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        await entry.SetValueAsync("stale");

        await entry.WriteThroughInvalidateAsync(() => Task.FromResult(BenzeneResult.Ok()));

        Assert.False(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task WriteThroughInvalidateAsync_UnsuccessfulResult_LeavesTheCacheUnchanged()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        await entry.SetValueAsync("unchanged");

        await entry.WriteThroughInvalidateAsync(() => Task.FromResult(BenzeneResult.NotFound()));

        Assert.True(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task LazyLoadAsync_ReferenceType_ExplicitlyCachedNull_IsAHitWithoutCallingDb()
    {
        // #140: negative caching. An explicit SetValueAsync(default) - a caller deciding a null result
        // is itself cacheable - must be a real, repeatable hit, not a permanent miss that re-runs
        // databaseReadFunc on every single call (the cache-penetration amplification #140 described).
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        await entry.SetValueAsync(null!);
        var databaseFuncCalled = false;

        var result = await entry.LazyLoadAsync(() =>
        {
            databaseFuncCalled = true;
            return Task.FromResult(BenzeneResult.Ok("from-database"));
        });

        Assert.False(databaseFuncCalled);
        Assert.True(result.IsSuccessful);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task LazyLoadAsync_CacheMiss_PassesExpireInThroughToTheCacheWrite()
    {
        // #144: per-call TTL was previously unreachable through LazyLoadAsync - it always used
        // whatever the provider's SetEntryValueAsync did with a null expireIn.
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        var expireIn = TimeSpan.FromSeconds(42);

        await entry.LazyLoadAsync(() => Task.FromResult(BenzeneResult.Ok("from-database")), expireIn);

        Assert.Equal(expireIn, entry.LastExpireIn);
    }

    [Fact]
    public async Task WriteThroughAsync_SetAction_PassesExpireInThroughToTheCacheWrite()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);
        var expireIn = TimeSpan.FromSeconds(7);

        await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Ok("new-value")), expireIn);

        Assert.Equal(expireIn, entry.LastExpireIn);
    }

    [Fact]
    public async Task WriteThroughAsync_SetAction_CacheWriteThrows_StillReturnsTheSuccessfulDatabaseResult()
    {
        // #139: a cache-side exception AFTER the database write already committed must not surface as
        // this operation's own failure and invite a caller to retry an already-successful write.
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store) { ThrowOnSet = true };

        var result = await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Ok("new-value")));

        Assert.True(result.IsSuccessful);
        Assert.Equal("new-value", result.Payload);
    }

    [Fact]
    public async Task WriteThroughAsync_InvalidateAction_CacheInvalidateThrows_StillReturnsTheSuccessfulDatabaseResult()
    {
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store) { ThrowOnInvalidate = true };

        var result = await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Deleted<string>()));

        Assert.True(result.IsSuccessful);
        Assert.Equal(BenzeneResultStatus.Deleted, result.Status);
    }

    [Fact]
    public async Task WriteThroughInvalidateAsync_CacheInvalidateReturnsFalse_StillReturnsTheSuccessfulDatabaseResult_AndLogsAWarning()
    {
        // #139: InvalidateAsync's bool return used to be discarded here - a failed invalidate was
        // silently indistinguishable from a successful one at this layer.
        var store = new Dictionary<string, string>();
        var logger = new CapturingLogger();
        var entry = new FakeCacheEntry<string>(store, logger: logger) { FailInvalidate = true };

        var result = await entry.WriteThroughInvalidateAsync(() => Task.FromResult(BenzeneResult.Ok()));

        Assert.True(result.IsSuccessful);
        Assert.Contains(logger.Warnings, w => w.Contains("invalidate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WriteThroughAsync_GetCacheValueDelegateThrows_StillReturnsTheSuccessfulDatabaseResult()
    {
        // #199: getCacheValue/getCacheAction are caller-supplied delegates that run AFTER the
        // database write has already committed - a throw here used to propagate in place of the
        // result, indistinguishable at the call site from the database write itself having failed
        // (exactly the failure mode #139 was fixed to prevent, just reachable through this adjacent
        // path). It must instead degrade to "write succeeded, cache sync failed", per #139's contract.
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);

        var result = await entry.WriteThroughAsync(
            () => Task.FromResult(BenzeneResult.Ok("new-value")),
            (Func<IBenzeneResult<string>, string?>)(_ => throw new InvalidOperationException("boom")));

        Assert.True(result.IsSuccessful);
        Assert.Equal("new-value", result.Payload);
        Assert.False(store.ContainsKey("the-key")); // the throwing delegate meant nothing was ever cached
    }

    [Fact]
    public async Task WriteThroughAsync_GetCacheActionDelegateThrows_StillReturnsTheSuccessfulDatabaseResult()
    {
        // #199, the other caller-supplied delegate on the same overload.
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);

        var result = await entry.WriteThroughAsync(
            () => Task.FromResult(BenzeneResult.Ok(42)),
            result => $"computed-{result.Payload}",
            (Func<IBenzeneResult<int>, CacheUpdateAction>)(_ => throw new InvalidOperationException("boom")));

        Assert.True(result.IsSuccessful);
        Assert.Equal(42, result.Payload);
        Assert.False(store.ContainsKey("the-key"));
    }

    [Fact]
    public async Task WriteThroughAsync_GetCacheValueDelegateThrowsOperationCanceled_Propagates()
    {
        // The one exception that must NOT be swallowed by the #199 fix - a caller-driven cancellation
        // propagates like any other ambient cancellation, the same convention #141 established.
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store);

        await Assert.ThrowsAsync<OperationCanceledException>(() => entry.WriteThroughAsync(
            () => Task.FromResult(BenzeneResult.Ok("new-value")),
            (Func<IBenzeneResult<string>, string?>)(_ => throw new OperationCanceledException())));
    }

    /// <summary>
    /// A minimal <see cref="Benzene.Abstractions.Serialization.ISerializer"/> that encodes a null
    /// reference (or a value-type default) as the empty string rather than the stock
    /// <c>System.Text.Json</c> serializer's 4-character <c>"null"</c> - the exact seam #201 describes:
    /// the docs invite a custom <see cref="Benzene.Abstractions.Serialization.ISerializer"/> (#145)
    /// without warning that presence detection used to assume this never happens.
    /// </summary>
    private sealed class EmptyStringForNullSerializer : Benzene.Abstractions.Serialization.ISerializer
    {
        public string Serialize(Type type, object payload) => payload?.ToString() ?? "";

        public string Serialize<T>(T payload) => payload?.ToString() ?? "";

        public object? Deserialize(Type type, string payload) => string.IsNullOrEmpty(payload) ? null : payload;

        public T? Deserialize<T>(string payload) => string.IsNullOrEmpty(payload) ? default : (T)(object)payload;
    }

    [Fact]
    public async Task LazyLoadAsync_CustomSerializerEncodesNullAsEmptyString_ExplicitlyCachedNull_IsStillAHitWithoutCallingDb()
    {
        // #201: presence detection used to be `!string.IsNullOrEmpty(cacheValue)`, which conflates
        // "key absent" with "the serializer emitted an empty string" - silently reintroducing #140's
        // cache-penetration hazard for any ISerializer that encodes null/default as "" instead of the
        // stock serializer's "null". The fix (`cacheValue != null`) must treat this negative-cache
        // entry as a real hit again.
        var store = new Dictionary<string, string>();
        var entry = new FakeCacheEntry<string>(store, "the-key", logger: null, serializer: new EmptyStringForNullSerializer());
        await entry.SetValueAsync(null!);
        Assert.Equal("", store["the-key"]); // sanity: this serializer really does store "", not "null"
        var databaseFuncCalled = false;

        var result = await entry.LazyLoadAsync(() =>
        {
            databaseFuncCalled = true;
            return Task.FromResult(BenzeneResult.Ok("from-database"));
        });

        Assert.False(databaseFuncCalled);
        Assert.True(result.IsSuccessful);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task GetValueAsync_UnderlyingReadThrowsOperationCanceled_PropagatesRatherThanBeingSwallowedAsAMiss()
    {
        // #141: a caller-driven cancellation is not a cache failure to degrade to a miss - it must
        // propagate like any other ambient cancellation, the same convention already established for
        // health checks.
        var entry = new FakeCacheEntry<string>(new Dictionary<string, string>()) { ThrowOnGetOperationCanceled = true };

        await Assert.ThrowsAsync<OperationCanceledException>(() => entry.GetValueAsync());
    }
}
