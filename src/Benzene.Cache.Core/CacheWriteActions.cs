using Microsoft.Extensions.Logging;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Cache.Core;

#nullable enable

public abstract class CacheWriteActions<T> : CacheInvalidateActions, ICacheWriteActions<T>
{
    /// <summary>
    /// The <see cref="ISerializer"/> this entry serializes values through on write, and (in
    /// <see cref="CacheEntry{T}"/>) deserializes through on read.
    /// </summary>
    /// <remarks>
    /// <b>The <see cref="ISerializer"/> seam note (#201):</b> the empty string is a valid,
    /// legitimately-produced serialized representation for some <see cref="ISerializer"/>
    /// implementations (e.g. a format whose empty-payload encoding happens to be <c>""</c>), and the
    /// cache layer must round-trip it as a real cached value, not mistake it for "nothing cached
    /// here" - <c>null</c> alone is the miss marker every provider uses (see
    /// <c>CacheEntry{T}.TryReadEntryAsync</c>'s presence check). An <see cref="ISerializer"/>
    /// implementation whose empty output is <c>""</c> therefore needs no special handling here; one
    /// planning to use <c>null</c> itself as a serialized-value marker would collide with the cache
    /// layer's own miss signal and must not.
    /// </remarks>
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

        // #199: the database write above has already committed - SyncCacheAfterWriteAsync (#139)
        // guarantees a cache-side Set/Invalidate failure past this point never surfaces as this
        // operation's own failure. getCacheAction/getCacheValue are caller-supplied delegates that
        // feed that same cache-side step, and until #199 they ran OUTSIDE #139's protection: a
        // throwing mapping delegate propagated straight out of WriteThroughAsync, turning an
        // already-successful write into a thrown exception. Each delegate is now evaluated in its
        // own try/catch, extending #139's contract to them: a throw is logged and falls through to
        // the same no-op outcome as CacheUpdateAction.None (result returned, cache left untouched).
        CacheUpdateAction action;
        try
        {
            action = getCacheAction(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Cache mapping delegate failed after the database write; result returned, cache not updated (key {key})", KeyDescription);
            action = CacheUpdateAction.None;
        }

        switch (action)
        {
            case CacheUpdateAction.Set:
                timerScope.SetTag("cache-action", "set");
                T? cacheValue;
                try
                {
                    // getCacheValue can legitimately produce null (e.g. the default Payload-based
                    // mapping for a reference-type T) - nothing to write back to the cache in that case.
                    cacheValue = getCacheValue(result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Cache mapping delegate failed after the database write; result returned, cache not updated (key {key})", KeyDescription);
                    break;
                }

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
