using Microsoft.Extensions.Logging;
using Benzene.Cache.Core;
using Benzene.Diagnostics.Timers;
using StackExchange.Redis;

namespace Benzene.Cache.Redis;

internal class RedisWildcardActions : CacheInvalidateActions
{
    private const int MaxKeyForDelete = 1048000;

    private readonly RedisCacheService _service;
    private readonly string _pattern;

    public RedisWildcardActions(RedisCacheService redisCacheService, string pattern)
    {
        _service = redisCacheService;
        _pattern = pattern;
    }

    protected override ILogger Logger => _service.Logger;

    protected override IProcessTimerFactory ProcessTimerFactory => _service.ProcessTimerFactory;

    protected override string KeyDescription => _pattern;

    protected override async Task<bool> InvalidateEntryAsync(CancellationToken cancellationToken)
    {
        // #198 defense-in-depth: CreatePrefixActions already rejects an empty/whitespace prefix
        // before it ever reaches here, but this type is also reachable directly via
        // CreateWildcardActions (an unescaped, caller-supplied pattern by design) and this is the
        // last point before a real Redis KEYS scan runs. Never execute a bare/effectively-universal
        // pattern - that would delete every key in the logical database.
        if (IsEffectivelyUniversalPattern(_pattern))
        {
            Logger.LogError(
                "Refusing to run cache invalidation for pattern {pattern}: it would match the entire keyspace",
                _pattern);
            throw new InvalidOperationException(
                $"Refusing to run cache invalidation for pattern '{_pattern}': it would match the " +
                "entire keyspace. This is a defense-in-depth guard (#198) - check what produced this pattern.");
        }

        long deletedKeys = 0;
        try
        {
            var redisDatabase = await _service.RedisSetup(cancellationToken);
            Logger.LogDebug("Sending {pattern} search to cache", _pattern);
            var result = (RedisKey[]?)await redisDatabase.ExecuteAsync("KEYS", _pattern).WaitAsync(cancellationToken);
            Logger.LogDebug("BenzeneResult for {pattern} - {benzeneResult.Length} keys.", _pattern, result?.Length);
            for (var i = 0; i < result?.Length; i += MaxKeyForDelete)
            {
                var keysForSending = result.Skip(i).Take(MaxKeyForDelete).ToArray();
                Logger.LogDebug("Deleting batch of {keysForSending.Length} keys.", keysForSending.Length);
                deletedKeys += await redisDatabase.KeyDeleteAsync(keysForSending).WaitAsync(cancellationToken);
            }
            Logger.LogDebug("Deleted {deletedKeys} keys.", deletedKeys);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error deleting keys from cache");
            // #252: a genuine failure is the ONLY case that reports false below - the caller
            // (CacheInvalidateActions.SyncCacheAfterWriteAsync) logs a "cache may serve stale data"
            // warning for a false return, and a pattern that legitimately matches zero keys (the
            // prefix was never populated, or everything under it already expired) is not that: it ran
            // to completion and correctly invalidated everything the pattern currently matches, which
            // is nothing. Returning early here (rather than falling through to `deletedKeys > 0`) is
            // what keeps that distinction: only this catch, reached on a genuine Redis exception,
            // reports failure now.
            return false;
        }

        // Ran to completion without exception - success, regardless of how many keys the pattern
        // matched. Unlike RedisMultiKeyActions (a caller-supplied, presumed-existing key set, where
        // zero-deleted plausibly means the caller's assumption was wrong), a wildcard PATTERN
        // legitimately matching nothing is the normal, expected outcome for plenty of calls (a
        // per-tenant/per-entity prefix that was never populated, or whose entries already expired) -
        // not a failure to report upstream.
        Logger.LogDebug("{pattern} matched {deletedKeys} key(s); reporting success.", _pattern, deletedKeys);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="pattern"/> would match every key in the keyspace: null/empty/
    /// whitespace-only, or - after trimming - composed entirely of the glob wildcard <c>*</c>
    /// (Redis glob syntax treats one or more consecutive <c>*</c> identically to a single one, so
    /// <c>"*"</c>, <c>"**"</c>, and <c>" * "</c> are all equally universal).
    /// </summary>
    private static bool IsEffectivelyUniversalPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        var trimmed = pattern.Trim();
        foreach (var c in trimmed)
        {
            if (c != '*')
            {
                return false;
            }
        }

        return true;
    }
}
