using Benzene.Abstractions.Results;

namespace Benzene.Cache.Core;

#nullable enable

public interface ICacheInvalidateActions
{
    Task<bool> InvalidateAsync(CancellationToken cancellationToken = default);

    Task<TResult> WriteThroughInvalidateAsync<TResult>(Func<Task<TResult>> modifyDatabaseFunc, CancellationToken cancellationToken = default) where TResult : IBenzeneResult;
}
