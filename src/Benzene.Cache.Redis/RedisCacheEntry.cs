using Microsoft.Extensions.Logging;
using Benzene.Cache.Core;
using Benzene.Diagnostics.Timers;

namespace Benzene.Cache.Redis;

internal class RedisCacheEntry<T> : CacheEntry<T>
{
    private readonly RedisCacheService _service;
    private readonly string _key;

    public RedisCacheEntry(RedisCacheService redisCacheService, string key) : base(redisCacheService.Serializer)
    {
        _service = redisCacheService;
        _key = key;
    }

    protected override ILogger Logger => _service.Logger;

    protected override IProcessTimerFactory ProcessTimerFactory => _service.ProcessTimerFactory;

    protected override string KeyDescription => _key;


    protected override async Task<string?> GetEntryValueAsync(CancellationToken cancellationToken)
    {
        try
        {
            var redisDatabase = await _service.RedisSetup(cancellationToken);
            return await redisDatabase.StringGetAsync(_key).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting value from cache");
            // #201: null, never "", is the miss marker CacheEntry<T> reads (cacheValue is not null).
            // A stored empty string is a legitimate cached value for some ISerializer implementations
            // - returning it here on a Redis error would masquerade a failed read as a real hit of an
            // empty value, deserializing "" instead of degrading to a genuine miss.
            return null;
        }
    }

    protected override async Task<bool> SetEntryValueAsync(string value, TimeSpan? expireIn, CancellationToken cancellationToken)
    {
        try
        {
            var redisDatabase = await _service.RedisSetup(cancellationToken);
            return await redisDatabase.StringSetAsync(_key, value, expireIn ?? _service.DefaultCacheLifespan).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error setting value in cache");
            return false;
        }
    }

    protected override async Task<bool> InvalidateEntryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var redisDatabase = await _service.RedisSetup(cancellationToken);
            return await redisDatabase.KeyDeleteAsync(_key).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error deleting key from cache");
            return false;
        }
    }
}
