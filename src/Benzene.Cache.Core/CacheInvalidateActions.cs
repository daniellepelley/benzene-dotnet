using Microsoft.Extensions.Logging;
using Benzene.Abstractions.Results;
using Benzene.Diagnostics.Timers;

namespace Benzene.Cache.Core;

#nullable enable

public abstract class CacheInvalidateActions : ICacheInvalidateActions
{
    protected abstract ILogger Logger { get; }
    protected abstract IProcessTimerFactory ProcessTimerFactory { get; }
    protected abstract string KeyDescription { get; }

    protected abstract Task<bool> InvalidateEntryAsync(CancellationToken cancellationToken);

    public Task<bool> InvalidateAsync(CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("Invalidating cache for key {key}", KeyDescription);
        return InvalidateEntryAsync(cancellationToken);
    }

    public async Task<TResult> WriteThroughInvalidateAsync<TResult>(Func<Task<TResult>> modifyDatabaseFunc, CancellationToken cancellationToken = default) where TResult : IBenzeneResult
    {
        using var timerScope = ProcessTimerFactory.Create("CacheActions_WriteThrough");

        var result = await modifyDatabaseFunc();

        if (result.IsSuccessful)
        {
            timerScope.SetTag("cache-action", "invalidate");
            // The database write already committed - it is the source of truth. A cache-side failure
            // here (thrown exception or a provider honestly reporting `false`) must not surface as
            // this operation's own failure and invite a caller to retry an already-successful write;
            // it also must not be silently discarded (the pre-fix behavior - see #139). Log it and
            // return the successful database result regardless.
            await SyncCacheAfterWriteAsync(ct => InvalidateAsync(ct), "invalidate", cancellationToken);
        }
        else
        {
            timerScope.SetTag("cache-action", "none");
            Logger.LogDebug("Cache unchanged for key {key}", KeyDescription);
        }

        return result;
    }

    /// <summary>
    /// Runs a cache-sync step (set/invalidate) that follows an already-committed database write. The
    /// database write is the source of truth for the overall operation's outcome, so a failure here -
    /// whether an exception (e.g. a serialization failure) or the cache action honestly returning
    /// <c>false</c> - is logged and swallowed rather than propagated or silently discarded (#139).
    /// A caller-driven cancellation is the one exception: that is not a cache failure to log and
    /// continue past, so it propagates like any other ambient cancellation.
    /// </summary>
    private protected async Task SyncCacheAfterWriteAsync(Func<CancellationToken, Task<bool>> cacheAction, string action, CancellationToken cancellationToken)
    {
        try
        {
            var succeeded = await cacheAction(cancellationToken);
            if (!succeeded)
            {
                Logger.LogWarning("Cache {action} failed for key {key} after the database write already succeeded; the cache may serve stale data until it next expires or is retried", action, KeyDescription);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Cache {action} threw for key {key} after the database write already succeeded; the cache may serve stale data until it next expires or is retried", action, KeyDescription);
        }
    }
}
