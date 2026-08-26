using Benzene.Abstractions.Results;

namespace Benzene.Cache.Core;

#nullable enable

public interface ICacheWriteActions<T> : ICacheInvalidateActions
{
    Task<bool> SetValueAsync(T value, TimeSpan? expireIn = null, CancellationToken cancellationToken = default);

    Task<TResult> WriteThroughAsync<TResult>(Func<Task<TResult>> modifyDatabaseFunc, TimeSpan? expireIn = null, CancellationToken cancellationToken = default) where TResult : IBenzeneResult<T>;

    Task<TResult> WriteThroughAsync<TResult>(Func<Task<TResult>> modifyDatabaseFunc, Func<TResult, T?> getCacheValue, TimeSpan? expireIn = null, CancellationToken cancellationToken = default) where TResult : IBenzeneResult;

    Task<TResult> WriteThroughAsync<TResult>(Func<Task<TResult>> modifyDatabaseFunc, Func<TResult, T?> getCacheValue, Func<TResult, CacheUpdateAction> getCacheAction, TimeSpan? expireIn = null, CancellationToken cancellationToken = default) where TResult : IBenzeneResult;
}
