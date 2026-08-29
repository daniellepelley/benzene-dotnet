using System.Text;
using Microsoft.Extensions.Logging;
using Benzene.Abstractions.Serialization;
using Benzene.Cache.Core;
using Benzene.Diagnostics.Timers;
using StackExchange.Redis;

namespace Benzene.Cache.Redis;

public abstract class RedisCacheService : ICacheService, IAsyncDisposable
{
    public ILogger Logger { get; }
    public IProcessTimerFactory ProcessTimerFactory { get; }

    /// <summary>
    /// The <see cref="ISerializer"/> shared by every <see cref="CacheEntry{T}"/>/<see cref="CacheWriteActions{T}"/>
    /// this service creates (<see cref="CreateCacheEntry{T}"/>, <see cref="CreateMultiKeyActions{T}"/>) - the
    /// constructor-injected value if one was supplied, otherwise a shared default (#145).
    /// </summary>
    public ISerializer Serializer { get; }

    private readonly IRedisConnectionFactory _connectionFactory;
    private readonly object _connectionLock = new();
    private Task<IConnectionMultiplexer>? _redisConnectionTask;
    private bool _disposed;

    public virtual TimeSpan DefaultCacheLifespan => TimeSpan.FromMinutes(5);

    /// <param name="serializer">
    /// The <see cref="ISerializer"/> to use for values this service's cache entries store - pass the
    /// DI-registered <see cref="ISerializer"/> (resolved automatically by DI when your subclass is
    /// constructed through it) to honor a non-default serialization format. Optional - a shared
    /// <c>System.Text.Json</c>-backed default is used when omitted or when nothing registers
    /// <see cref="ISerializer"/> (#145).
    /// </param>
    protected RedisCacheService(ILogger<RedisCacheService> logger, IProcessTimerFactory processTimerFactory, IRedisConnectionFactory connectionFactory, ISerializer? serializer = null)
    {
        Logger = logger;
        ProcessTimerFactory = processTimerFactory;
        _connectionFactory = connectionFactory;
        Serializer = serializer ?? CacheSerializerDefaults.Serializer;
    }

    protected abstract Task<ConfigurationOptions> GetConfigurationOptionsAsync();

    // Returns the shared connect task, (re)starting it if we've never connected or the previous
    // attempt faulted/was cancelled. A Lazy<Task<T>> would memoize the FIRST task object, so a
    // single connection blip at startup (Redis and the app coming up together, AbortOnConnectFail
    // default true) cached a faulted task for the process lifetime - the cache then stayed bypassed
    // and the health check red forever, even after Redis recovered. A successful multiplexer is kept
    // (StackExchange.Redis reconnects internally); only a failed connect is retried. The lock
    // serialises recreation so a fault can't spawn duplicate connects, and an in-flight (incomplete)
    // task is shared rather than restarted.
    private Task<IConnectionMultiplexer> GetConnectionTask()
    {
        lock (_connectionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_redisConnectionTask is null || _redisConnectionTask.IsFaulted || _redisConnectionTask.IsCanceled)
            {
                _redisConnectionTask = Task.Run(async () =>
                {
                    using var scope = ProcessTimerFactory.Create("RedisCacheService_Connect");
                    var options = await GetConfigurationOptionsAsync() ?? throw new InvalidOperationException("Redis configuration options are not set");
                    return await _connectionFactory.ConnectAsync(options);
                });
            }

            return _redisConnectionTask;
        }
    }

    protected void StartConnection()
    {
        _ = GetConnectionTask();
    }

    // The shared connect task above is intentionally NOT tied to any one caller's token - it's memoized
    // and awaited by every concurrent caller, so cancelling it for caller A would break caller B's
    // in-flight wait too. Instead each caller bounds only its OWN wait with its own token via
    // WaitAsync: a hung Redis connect (or a caller that disconnects/shuts down) no longer blocks this
    // call past its own ambient cancellation, even though the underlying connect attempt keeps running
    // in the background for whoever else is (or later becomes) awaiting it (#141 - previously there was
    // no deadline of any kind here, internal or caller-supplied).
    internal async Task<IDatabase> RedisSetup(CancellationToken cancellationToken = default)
    {
        var multiplexer = await GetConnectionTask().WaitAsync(cancellationToken);
        return multiplexer.GetDatabase();
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        var redisDatabase = await RedisSetup(cancellationToken);
        await redisDatabase.PingAsync().WaitAsync(cancellationToken);
        return true;
    }

    protected ICacheEntry<T> CreateCacheEntry<T>(string key)
    {
        return new RedisCacheEntry<T>(this, key);
    }

    protected ICacheWriteActions<T> CreateMultiKeyActions<T>(IEnumerable<string> keys)
    {
        return new RedisMultiKeyActions<T>(this, keys);
    }

    protected ICacheInvalidateActions CreatePrefixActions(string prefix)
    {
        // #198: an empty/whitespace prefix (a missing tenant id, an unset config value) would
        // otherwise build the literal pattern "*" below - matching, and so invalidating, every key
        // in the logical database. Fail fast and loud here (a startup/first-use error) rather than
        // silently wiping the entire keyspace the first time this runs.
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException(
                "The cache-key prefix must not be null, empty, or consist only of whitespace - an " +
                "empty prefix would build the wildcard pattern \"*\", which matches (and so " +
                "invalidates) every key in the cache (#198). Pass a real, non-empty prefix " +
                "(e.g. a tenant id) instead.", nameof(prefix));
        }

        // Escape glob metacharacters in the LITERAL prefix before appending the wildcard. Redis KEYS
        // treats * ? [ ] \ as glob syntax, so a prefix derived from data (tenant id, email, ...) that
        // contains one would otherwise match the wrong keys - under-invalidating (an unterminated "["
        // matches nothing, leaving stale data) or over-invalidating (a "*" matches unrelated keys).
        // CreateWildcardActions is left unescaped by design: its caller is passing an actual pattern.
        return new RedisWildcardActions(this, EscapeGlobLiteral(prefix) + "*");
    }

    protected ICacheInvalidateActions CreateWildcardActions(string pattern)
    {
        return new RedisWildcardActions(this, pattern);
    }

    private static string EscapeGlobLiteral(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '*' or '?' or '[' or ']')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Disposes the underlying <see cref="IConnectionMultiplexer"/> this service connected and
    /// cached, if a connect was ever started. Best-effort: a connect that never completed, or that
    /// faulted, has no multiplexer to dispose and is simply dropped. Idempotent - a second (or
    /// concurrent) call is a no-op. After this returns, <see cref="GetConnectionTask"/> (and so
    /// <see cref="RedisSetup"/>/<see cref="CanConnectAsync"/>/every cache operation) throws
    /// <see cref="ObjectDisposedException"/> instead of silently opening and leaking a brand new
    /// <see cref="IConnectionMultiplexer"/> that nothing will ever dispose (#146).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task<IConnectionMultiplexer>? connectionTask;
        lock (_connectionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connectionTask = _redisConnectionTask;
            _redisConnectionTask = null;
        }

        if (connectionTask is null)
        {
            return;
        }

        try
        {
            var multiplexer = await connectionTask.ConfigureAwait(false);
            await multiplexer.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The connect itself faulted/was cancelled - nothing was ever connected, so there is
            // nothing to dispose. Disposal must not throw for a connection that already failed.
        }

        GC.SuppressFinalize(this);
    }
}
