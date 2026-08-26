using Microsoft.Extensions.Logging;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Cache.Core;

#nullable enable

public abstract class CacheEntry<T> : CacheWriteActions<T>, ICacheEntry<T>
{
    protected CacheEntry() : base()
    {
    }

    /// <param name="serializer">See <see cref="CacheWriteActions{T}(ISerializer?)"/>.</param>
    protected CacheEntry(ISerializer? serializer) : base(serializer)
    {
    }

    protected abstract Task<string?> GetEntryValueAsync(CancellationToken cancellationToken);

    public async Task<T?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var (_, value) = await TryReadEntryAsync(cancellationToken);
        return value;
    }

    /// <summary>
    /// Reads the entry, returning whether the key was <em>present</em> (a real cache hit) separately
    /// from the deserialized value. The presence flag is what <see cref="GetEntryValueAsync"/> already
    /// knows (a non-empty stored string), and it's the only reliable hit signal for an unconstrained
    /// generic <typeparamref name="T"/>: for a value type, a genuine miss returns <c>default(T)</c>,
    /// and <c>default(T) != null</c> (via boxing) is always <c>true</c> - so deciding hit/miss from
    /// <c>value != null</c> mistakes every value-type miss for a hit of the default value. The same
    /// presence flag also makes an intentionally-cached <c>null</c> a real hit for a reference-type
    /// <typeparamref name="T"/> (see <see cref="LazyLoadAsync{TResult}"/>): the JSON serialization of
    /// <c>null</c> is the 4-character string <c>"null"</c>, never an empty stored value, so presence
    /// and "the stored value deserializes to null" are never confused with each other.
    /// </summary>
    private async Task<(bool Found, T? Value)> TryReadEntryAsync(CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogDebug("Trying to hit cache key {key}", KeyDescription);
            var cacheValue = await GetEntryValueAsync(cancellationToken);
            if (!string.IsNullOrEmpty(cacheValue))
            {
                return (true, Serializer.Deserialize<T>(cacheValue));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error occurred when trying to read from cache");
        }
        return (false, default);
    }

    public Task<IBenzeneResult<T>> LazyLoadAsync(Func<Task<IBenzeneResult<T>>> databaseReadFunc, TimeSpan? expireIn = null, CancellationToken cancellationToken = default)
    {
        return LazyLoadAsync(databaseReadFunc, value => BenzeneResult.Ok(value), expireIn, cancellationToken);
    }

    public async Task<TResult> LazyLoadAsync<TResult>(Func<Task<TResult>> databaseReadFunc, Func<T, TResult> createResult, TimeSpan? expireIn = null, CancellationToken cancellationToken = default) where TResult : IBenzeneResult<T>
    {
        using var timerScope = ProcessTimerFactory.Create("CacheEntry_LazyLoad");

        var (found, cacheValue) = await TryReadEntryAsync(cancellationToken);

        // A hit is decided purely by presence (`found`), never by whether the deserialized value is
        // itself null. This covers the value-type miss-as-hit hazard described on TryReadEntryAsync,
        // and - since #140 - also lets a reference-type T be intentionally negative-cached: an explicit
        // SetValueAsync(default) (or a write-through mapping that legitimately caches null) is now a
        // real hit here instead of a permanent, unavoidable miss that re-runs databaseReadFunc on every
        // call (the cache-penetration amplification #140 described).
        if (found)
        {
            timerScope.SetTag("cache-status", "hit");
            Logger.LogDebug("Cache hit for key {key}", KeyDescription);
            return createResult(cacheValue!);
        }
        else
        {
            timerScope.SetTag("cache-status", "miss");
            Logger.LogDebug("No hit in cache for key {key}", KeyDescription);

            var benzeneResult = await databaseReadFunc();

            // A successful result's Payload can itself be null (e.g. a reference-type T the database
            // read legitimately produced no value for) - there's nothing to write back to the cache in
            // that case, so skip the write rather than caching a "null" placeholder. Callers that want
            // that null to become a genuine negative-cache hit on the next LazyLoadAsync call should
            // call SetValueAsync(default, ...) themselves once they've decided it's cacheable - this
            // cache-aside path stays conservative by default.
            if (benzeneResult.IsSuccessful && benzeneResult.Payload is not null)
            {
                await SetValueAsync(benzeneResult.Payload, expireIn, cancellationToken);
            }

            return benzeneResult;
        }
    }
}
