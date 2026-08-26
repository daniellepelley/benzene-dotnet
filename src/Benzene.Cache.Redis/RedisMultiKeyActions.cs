using Microsoft.Extensions.Logging;
using Benzene.Cache.Core;
using Benzene.Diagnostics.Timers;
using StackExchange.Redis;

namespace Benzene.Cache.Redis;

internal class RedisMultiKeyActions<T> : CacheWriteActions<T>
{
    private readonly RedisCacheService _service;
    private readonly string[] _keys;
    private readonly RedisKey[] _redisKeys;

    public RedisMultiKeyActions(RedisCacheService redisCacheService, IEnumerable<string> keys) : base(redisCacheService.Serializer)
    {
        _service = redisCacheService;
        _keys = keys.ToArray();
        _redisKeys = Array.ConvertAll(_keys, key => (RedisKey)key);
    }

    protected override ILogger Logger => _service.Logger;

    protected override IProcessTimerFactory ProcessTimerFactory => _service.ProcessTimerFactory;

    protected override string KeyDescription => string.Join(", ", _keys);

    protected override async Task<bool> InvalidateEntryAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A single multi-key DEL - one atomic Redis command - rather than the previous sequential
            // per-key KeyDeleteAsync loop, which could throw partway through and lose track of keys it
            // had already (or hadn't yet) deleted (#147). Mirrors the batched
            // KeyDeleteAsync(RedisKey[]) already used by RedisWildcardActions for the equivalent
            // pattern-based invalidate path.
            var redisDatabase = await _service.RedisSetup(cancellationToken);
            var deletedKeys = await redisDatabase.KeyDeleteAsync(_redisKeys).WaitAsync(cancellationToken);
            return deletedKeys > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error deleting keys from cache");
            return false;
        }
    }

    protected override async Task<bool> SetEntryValueAsync(string value, TimeSpan? expireIn, CancellationToken cancellationToken)
    {
        // StackExchange.Redis has no native multi-key SET-with-per-key-TTL primitive (MSET has no
        // expiry support at all), so each key is still its own StringSetAsync call - but issued
        // concurrently, with each key's own outcome (success, `false`, or a thrown exception) captured
        // independently rather than accumulated by a sequential loop. The old sequential version could
        // throw on key 2 of 3, abandon key 3 entirely, and still report success purely because key 1
        // had already incremented its counter before the throw (#147) - a partial write silently
        // reported as if every key had been considered. Every key here is always attempted, and the
        // aggregate result reflects what actually happened to all of them.
        var redisDatabase = await GetDatabaseAsync(cancellationToken);
        if (redisDatabase is null)
        {
            return false;
        }

        var expiry = expireIn ?? _service.DefaultCacheLifespan;
        var results = await Task.WhenAll(_keys.Select(key => SetSingleKeyAsync(redisDatabase, key, value, expiry, cancellationToken)));
        return Array.Exists(results, r => r);
    }

    private async Task<IDatabase?> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _service.RedisSetup(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error setting value in cache");
            return null;
        }
    }

    private async Task<bool> SetSingleKeyAsync(IDatabase redisDatabase, string key, string value, TimeSpan expiry, CancellationToken cancellationToken)
    {
        try
        {
            return await redisDatabase.StringSetAsync(key, value, expiry).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error setting key {key} in cache", key);
            return false;
        }
    }
}
