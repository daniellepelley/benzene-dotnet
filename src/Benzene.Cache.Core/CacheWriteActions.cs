using Microsoft.Extensions.Logging;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Cache.Core;

#nullable enable

public abstract class CacheWriteActions<T> : CacheInvalidateActions, ICacheWriteActions<T>
{
    protected ISerializer Serializer { get; }

    protected CacheWriteActions() : this(null)
    {
    }

    /// <param name="serializer">
    /// The <see cref="ISerializer"/> to use for this entry's values. Pass the DI-registered
    /// <see cref="ISerializer"/> (e.g. resolved by the owning <c>RedisCacheService</c> subclass, itself
    /// constructor-injected) to honor a non-default serialization format; <c>null</c> falls back to a
    /// shared <c>System.Text.Json</c>-backed default (#145).
    /// </param>
    protected CacheWriteActions(ISerializer? serializer)
    {
        Serializer = serializer ?? CacheSerializerDefaults.Serializer;
    }

    protected abstract Task<bool> SetEntryValueAsync(string value, TimeSpan? expireIn, CancellationToken cancellationToken);

    public async Task<bool> SetValueAsync(T value, TimeSpan? expireIn = null, CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("Setting cache for key {key}", KeyDescription);
        var cacheValue = Serializer.Serialize(value);
        return await SetEntryValueAsync(cacheValue, expireIn, cancellationToken);
    }

    private static CacheUpdateAction DefaultCacheActionMapping<TResult>(TResult result) where TResult : IBenzeneResult
    {
        return result.Status switch
        {
            BenzeneResultStatus.Accepted or
            BenzeneResultStatus.Ok or
            BenzeneResultStatus.Created or
            BenzeneResultStatus.Updated => CacheUpdateAction.Set,
            BenzeneResultStatus.Deleted => CacheUpdateAction.Invalidate,
            _ => CacheUpdateAction.None,
        };
    }

    public Task<TResult> WriteThroughAsync<TResult>(Func<Task<TResult>> modifyDatabaseFunc, TimeSpan? expireIn = null, CancellationToken cancellationToken = default) where TResult : IBenzeneResult<T>
    {
        return WriteThroughAsync(modifyDatabaseFunc, result => result.Payload, DefaultCacheActionMapping, expireIn, cancellationToken);
    }

    public Task<TResult> WriteThroughAsync<TResult>(Func<Task<TResult>> modifyDatabaseFunc, Func<TResult, T?> getCacheValue, TimeSpan? expireIn = null, CancellationToken cancellationToken = default) where TResult : IBenzeneResult
    {
        return WriteThroughAsync(modifyDatabaseFunc, getCacheValue, DefaultCacheActionMapping, expireIn, cancellationToken);
    }

    public async Task<TResult> WriteThroughAsync<TResult>(Func<Task<TResult>> modifyDatabaseFunc, Func<TResult, T?> getCacheValue, Func<TResult, CacheUpdateAction> getCacheAction, TimeSpan? expireIn = null, CancellationToken cancellationToken = default) where TResult : IBenzeneResult
    {
        using var timerScope = ProcessTimerFactory.Create("CacheActions_WriteThrough");

        var result = await modifyDatabaseFunc();

        switch (getCacheAction(result))
        {
            case CacheUpdateAction.Set:
                timerScope.SetTag("cache-action", "set");
                // getCacheValue can legitimately produce null (e.g. the default Payload-based mapping
                // for a reference-type T) - nothing to write back to the cache in that case.
                var cacheValue = getCacheValue(result);
                if (cacheValue is not null)
                {
                    // The database write already committed - see SyncCacheAfterWriteAsync (#139): a
                    // cache-side failure here must not surface as this operation's own failure.
                    await SyncCacheAfterWriteAsync(ct => SetValueAsync(cacheValue, expireIn, ct), "set", cancellationToken);
                }
                break;

            case CacheUpdateAction.Invalidate:
                timerScope.SetTag("cache-action", "invalidate");
                await SyncCacheAfterWriteAsync(ct => InvalidateAsync(ct), "invalidate", cancellationToken);
                break;

            default:
                timerScope.SetTag("cache-action", "none");
                Logger.LogDebug("Cache unchanged for key {key}", KeyDescription);
                break;
        }

        return result;
    }
}
