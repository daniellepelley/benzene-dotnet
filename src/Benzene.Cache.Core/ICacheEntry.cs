using Benzene.Abstractions.Results;

namespace Benzene.Cache.Core;

#nullable enable

public interface ICacheEntry<T> : ICacheWriteActions<T>
{
    Task<T?> GetValueAsync(CancellationToken cancellationToken = default);

    Task<IBenzeneResult<T>> LazyLoadAsync(Func<Task<IBenzeneResult<T>>> databaseReadFunc, TimeSpan? expireIn = null, CancellationToken cancellationToken = default);

    Task<TResult> LazyLoadAsync<TResult>(Func<Task<TResult>> databaseReadFunc, Func<T, TResult> createResult, TimeSpan? expireIn = null, CancellationToken cancellationToken = default) where TResult : IBenzeneResult<T>;
}
