using System;
using System.Text.Json;
using System.Threading.Tasks;
using Benzene.Abstractions.Serialization;
using Benzene.Cache.Core;
using Benzene.Cache.Redis;
using Benzene.Diagnostics.Timers;
using Benzene.Results;
using Benzene.Test.Cache.Redis.Instance;
using Benzene.Test.Cache.Redis.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;
using System.Threading;

namespace Benzene.Test.Cache.Redis;

public class RedisCacheServiceTest
{
    const string TEST_ERROR_MESSAGE = "Test Error Message";


    [Fact]
    public async Task HealthCheckTest()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IProcessTimerFactory>(_ => new DebugTimerFactory());
        services.AddScoped<IRedisConnectionFactory>(_ => new MockConnectionFactory());
        services.AddScoped<TestRedisCacheService>();

        var serviceResolver = new Microsoft.Dependencies.MicrosoftServiceResolverAdapter(services.BuildServiceProvider());

        var factory = new CacheHealthCheckFactory<TestRedisCacheService>();
        var healthcheck = factory.Create(serviceResolver);

        var result = await healthcheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.Equal("Cache", result.Type);
        Assert.Equal(true, result.Data["CanConnect"]);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Cache", dependency.Kind);
        Assert.Equal(nameof(TestRedisCacheService), dependency.Name);
    }

    [Fact]
    public async Task FailedHealthCheckTest()
    {
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.PingAsync(StackExchange.Redis.CommandFlags.None)).ThrowsAsync(new System.Exception(TEST_ERROR_MESSAGE));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IProcessTimerFactory>(_ => new DebugTimerFactory());
        services.AddScoped<IRedisConnectionFactory>(_ => connectionFactory);
        services.AddScoped<TestRedisCacheService>();

        var serviceResolver = new Microsoft.Dependencies.MicrosoftServiceResolverAdapter(services.BuildServiceProvider());

        var factory = new CacheHealthCheckFactory<TestRedisCacheService>();
        var healthcheck = factory.Create(serviceResolver);

        var result = await healthcheck.ExecuteAsync(CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("Cache", result.Type);
        Assert.Equal(false, result.Data["CanConnect"]);
        // The exception's type name, not its message - see the security fix in CacheHealthCheck
        // (avoids leaking connection details some providers embed in exception messages).
        Assert.Equal(nameof(Exception), result.Data["Error"]);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal("Cache", dependency.Kind);
        Assert.Equal(nameof(TestRedisCacheService), dependency.Name);
    }

    [Fact]
    public async Task CacheEntryLazyLoadCacheHitTest()
    {
        var testValue = new TestDataType { Id = 42, Name = "Test" };
        var cacheValue = JsonSerializer.Serialize(testValue);

        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(new RedisValue(cacheValue));

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.LazyLoadAsync(() => Task.FromResult(BenzeneResult.ServiceUnavailable<TestDataType>()));

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Equivalent(testValue, result.Payload);
    }

    [Fact]
    public async Task CacheEntryLazyLoadCacheMissTest()
    {
        var testValue = new TestDataType { Id = 42, Name = "Test" };

        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(RedisValue.Null);
        connectionFactory.DataBaseMock.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None)).ReturnsAsync(true).Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.LazyLoadAsync(() => Task.FromResult(BenzeneResult.Ok(testValue)));

        Assert.Equal(BenzeneResultStatus.Ok, result.Status);
        Assert.Equivalent(testValue, result.Payload);
        connectionFactory.DataBaseMock.Verify();
    }

    [Fact]
    public async Task CacheEntryWriteThroughSimpleSetTest()
    {
        var testValue = new TestDataType { Id = 42, Name = "Test" };

        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None)).ReturnsAsync(true).Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Created(testValue)));

        Assert.Equal(BenzeneResultStatus.Created, result.Status);
        Assert.Equivalent(testValue, result.Payload);
        connectionFactory.DataBaseMock.Verify();
    }

    [Fact]
    public async Task CacheEntryWriteThroughSimpleDeleteTest()
    {
        var testValue = new TestDataType { Id = 42, Name = "Test" };

        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(true).Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Deleted(testValue)));

        Assert.Equal(BenzeneResultStatus.Deleted, result.Status);
        Assert.Equivalent(testValue, result.Payload);
        connectionFactory.DataBaseMock.Verify();
    }

    [Fact]
    public async Task CacheEntryWriteThroughSimpleNoWriteTest()
    {
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.BadRequest<TestDataType>(TEST_ERROR_MESSAGE)));

        Assert.Equal(BenzeneResultStatus.BadRequest, result.Status);
        Assert.Equal(TEST_ERROR_MESSAGE, Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task CacheEntryWriteThroughConvertSetTest()
    {
        var testValue = new TestDataType { Id = 42, Name = "Test" };

        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None)).ReturnsAsync(true).Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Updated(new { TestValue = testValue })), x => x.Payload.TestValue);

        Assert.Equal(BenzeneResultStatus.Updated, result.Status);
        Assert.Equivalent(testValue, result.Payload.TestValue);
        connectionFactory.DataBaseMock.Verify();
    }

    [Fact]
    public async Task CacheEntryWriteThroughConvertDeleteTest()
    {
        var testValue = new TestDataType { Id = 42, Name = "Test" };

        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(true).Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.Deleted(new { TestValue = testValue })), x => x.Payload.TestValue);

        Assert.Equal(BenzeneResultStatus.Deleted, result.Status);
        Assert.Equivalent(testValue, result.Payload.TestValue);
        connectionFactory.DataBaseMock.Verify();
    }

    [Fact]
    public async Task CacheEntryWriteThroughConvertNoWriteTest()
    {
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.WriteThroughAsync(() => Task.FromResult(BenzeneResult.BadRequest<TestDataType>(TEST_ERROR_MESSAGE)), x => x.Payload);

        Assert.Equal(BenzeneResultStatus.BadRequest, result.Status);
        Assert.Equal(TEST_ERROR_MESSAGE, Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task CacheEntryWriteThroughInvlidateTest()
    {
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(true).Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.WriteThroughInvalidateAsync(() => Task.FromResult(BenzeneResult.Created("Test")));

        Assert.Equal(BenzeneResultStatus.Created, result.Status);
        Assert.Equivalent("Test", result.Payload);
        connectionFactory.DataBaseMock.Verify();
    }

    [Fact]
    public async Task CacheEntryWriteThroughInvlidateNoActionTest()
    {
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.WriteThroughInvalidateAsync(() => Task.FromResult(BenzeneResult.BadRequest<TestDataType>(TEST_ERROR_MESSAGE)));

        Assert.Equal(BenzeneResultStatus.BadRequest, result.Status);
        Assert.Equal(TEST_ERROR_MESSAGE, Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task CacheMultipleEntriesTest()
    {
        var connectionFactory = new MockConnectionFactory();
        // #147: InvalidateEntryAsync now issues a single atomic multi-key DEL rather than a per-key
        // loop - see CacheMultipleEntries_InvalidateAsync_UsesASingleAtomicMultiKeyDelete below.
        connectionFactory.DataBaseMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None)).ReturnsAsync(2);
        connectionFactory.DataBaseMock.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None)).ReturnsAsync(true);

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestMultipleEntries(23, 45);

        Assert.True(await actions.SetValueAsync(new TestDataType { Id = 42, Name = "Test" }));

        Assert.True(await actions.InvalidateAsync());
    }

    [Fact]
    public async Task CacheMultipleEntries_SetValueAsync_OneKeyThrows_TheOtherIsStillAttempted_AndResultReflectsPartialSuccess()
    {
        // #147: the old sequential loop aborted on the first exception, leaving later keys entirely
        // untouched while still reporting overall success purely because an earlier key had already
        // succeeded before the throw. Every key must always be attempted, concurrently and
        // independently of the others' outcome.
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock
            .Setup(x => x.StringSetAsync(new RedisKey("TEST_23"), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None))
            .ThrowsAsync(new Exception(TEST_ERROR_MESSAGE));
        connectionFactory.DataBaseMock
            .Setup(x => x.StringSetAsync(new RedisKey("TEST_45"), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None))
            .ReturnsAsync(true)
            .Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestMultipleEntries(23, 45);

        var result = await actions.SetValueAsync(new TestDataType { Id = 42, Name = "Test" });

        Assert.True(result); // "any key succeeded" contract, unchanged - but now honestly earned.
        connectionFactory.DataBaseMock.Verify();
    }

    [Fact]
    public async Task CacheMultipleEntries_SetValueAsync_EveryKeyThrows_ReturnsFalse()
    {
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock
            .Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None))
            .ThrowsAsync(new Exception(TEST_ERROR_MESSAGE));

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestMultipleEntries(23, 45);

        Assert.False(await actions.SetValueAsync(new TestDataType { Id = 42, Name = "Test" }));
    }

    [Fact]
    public async Task CacheMultipleEntries_InvalidateAsync_UsesASingleAtomicMultiKeyDeleteCommand()
    {
        // #147: one DEL <key1> <key2> Redis command rather than a sequential per-key loop - removes
        // the partial-failure hazard for this path entirely (there's only one command to succeed or
        // fail as a whole).
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock
            .Setup(x => x.KeyDeleteAsync(It.Is<RedisKey[]>(keys => keys.Length == 2), CommandFlags.None))
            .ReturnsAsync(2)
            .Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestMultipleEntries(23, 45);

        Assert.True(await actions.InvalidateAsync());
        connectionFactory.DataBaseMock.Verify();
    }

    [Fact]
    public async Task CachePrefixTest()
    {
        var keys = RedisResult.Create(new[]
        {
            RedisResult.Create(new RedisKey("TEST_1")),
            RedisResult.Create(new RedisKey("TEST_2")),
        });

        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.ExecuteAsync("KEYS", It.IsAny<string>())).ReturnsAsync(keys);
        connectionFactory.DataBaseMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None)).ReturnsAsync(2);

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestPrefixActions();

        var result = await actions.WriteThroughInvalidateAsync(() => Task.FromResult(BenzeneResult.Deleted()));

        Assert.Equal(BenzeneResultStatus.Deleted, result.Status);
    }

    [Fact]
    public async Task CachePrefix_EscapesGlobMetacharactersInTheLiteralPrefix()
    {
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.ExecuteAsync("KEYS", It.IsAny<string>()))
            .ReturnsAsync(RedisResult.Create(System.Array.Empty<RedisResult>()));
        connectionFactory.DataBaseMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None)).ReturnsAsync(0);

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        // A prefix containing a glob metacharacter ("[") - e.g. derived from a tenant id.
        var actions = service.GetTestPrefixActions("TEST_[a");

        await actions.WriteThroughInvalidateAsync(() => Task.FromResult(BenzeneResult.Deleted()));

        // The "[" must be escaped so KEYS matches it literally rather than as an (unterminated) char
        // class - unescaped, "TEST_[a*" matches nothing and the invalidation silently no-ops.
        connectionFactory.DataBaseMock.Verify(x => x.ExecuteAsync("KEYS", @"TEST_\[a*"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreatePrefixActions_EmptyOrWhitespacePrefix_ThrowsRatherThanBuildingAUniversalPattern(string prefix)
    {
        // #198: an empty/whitespace prefix used to build the literal wildcard pattern "*", which
        // matches (and so invalidates) every key in the cache. Fail fast instead.
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);

        var ex = Assert.Throws<ArgumentException>(() => service.GetTestPrefixActions(prefix));
        Assert.Contains("prefix", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePrefixActions_RealPrefix_StillProducesTheEscapedPrefixStarPatternAndInvalidates()
    {
        // A genuine, non-empty prefix must still work exactly as before the #198 fix.
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.ExecuteAsync("KEYS", It.IsAny<string>()))
            .ReturnsAsync(RedisResult.Create(System.Array.Empty<RedisResult>()));
        connectionFactory.DataBaseMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None)).ReturnsAsync(0);

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestPrefixActions("TENANT_1_");

        await actions.InvalidateAsync();

        connectionFactory.DataBaseMock.Verify(x => x.ExecuteAsync("KEYS", "TENANT_1_*"));
    }

    [Fact]
    public async Task WildcardActions_BarePattern_RefusesToRunRatherThanDeletingTheEntireKeyspace()
    {
        // #198 defense-in-depth: CreateWildcardActions is a lower-level escape hatch reachable
        // directly (unlike CreatePrefixActions, whose own guard is tested above) - the last line of
        // defense before a real Redis KEYS scan must also refuse a bare "*".
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestWildcardActions("*");

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.InvalidateAsync());

        // The dangerous KEYS scan must never have been issued.
        connectionFactory.DataBaseMock.Verify(x => x.ExecuteAsync("KEYS", It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("**")]
    [InlineData(" * ")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WildcardActions_OtherEffectivelyUniversalPatterns_AlsoRefuseToRun(string pattern)
    {
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestWildcardActions(pattern);

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.InvalidateAsync());
        connectionFactory.DataBaseMock.Verify(x => x.ExecuteAsync("KEYS", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WildcardActions_NonUniversalPattern_StillRunsNormally()
    {
        // The guard must not be so broad it rejects legitimate, non-universal patterns.
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.ExecuteAsync("KEYS", It.IsAny<string>()))
            .ReturnsAsync(RedisResult.Create(System.Array.Empty<RedisResult>()));

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestWildcardActions("TEST_*");

        var result = await actions.InvalidateAsync();

        Assert.False(result);
        connectionFactory.DataBaseMock.Verify(x => x.ExecuteAsync("KEYS", "TEST_*"), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_AfterConnecting_DisposesTheMultiplexer()
    {
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);

        // Force the connect to actually run (StartConnection in the constructor only kicks it off).
        await service.CanConnectAsync();

        await service.DisposeAsync();

        connectionFactory.ConnectionMultiplexerMock.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WithoutEverConnecting_DoesNotThrow()
    {
        // Unlike TestRedisCacheService, this never calls StartConnection - no connect is ever kicked
        // off, so there is genuinely nothing for DisposeAsync to dispose.
        var connectionFactory = new MockConnectionFactory();
        var service = new NeverConnectedTestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);

        await service.DisposeAsync();

        connectionFactory.ConnectionMultiplexerMock.Verify(x => x.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow_AndDisposesTheMultiplexerOnlyOnce()
    {
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        await service.CanConnectAsync();

        await service.DisposeAsync();
        await service.DisposeAsync();

        connectionFactory.ConnectionMultiplexerMock.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task CacheWildcardTest()
    {
        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock.Setup(x => x.ExecuteAsync("KEYS", It.IsAny<string>())).ThrowsAsync(new System.Exception(TEST_ERROR_MESSAGE));

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestWildcardActions();

        Assert.False(await actions.InvalidateAsync());
    }

    [Fact]
    public async Task DisposeAsync_ThenCanConnectAsync_ThrowsObjectDisposedException_RatherThanLeakingANewConnection()
    {
        // #146: a late call after disposal used to silently open (and leak) a brand new
        // IConnectionMultiplexer that DisposeAsync would never be asked to dispose again.
        var connectionFactory = new MockConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        await service.CanConnectAsync();

        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CanConnectAsync());
        // Only the one connect from before disposal ever happened - no second multiplexer was created.
        Assert.Equal(1, connectionFactory.ConnectCallCount);
    }

    [Fact]
    public async Task CanConnectAsync_ConnectNeverCompletes_CallersOwnCancellationTokenUnblocksTheWait()
    {
        // #141: previously there was no deadline of any kind (internal or caller-supplied) on the
        // connect - a hung Redis connect held the caller forever, past client disconnect/shutdown.
        var connectionFactory = new NeverConnectingConnectionFactory();
        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // ThrowsAnyAsync, not ThrowsAsync: Task.WaitAsync(cancellationToken) throws the derived
        // TaskCanceledException, not a bare OperationCanceledException - both are correct/expected
        // for a caller-driven cancellation (the production `catch (OperationCanceledException)`
        // guards already handle this polymorphically), but xUnit's ThrowsAsync<T> requires an exact
        // type match rather than "is a".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CanConnectAsync(cts.Token));
    }

    [Fact]
    public async Task Serializer_CustomSerializerConstructorInjected_IsUsedInsteadOfTheDefault()
    {
        // #145: the cache layer used to hard-wire System.Text.Json in CacheWriteActions()'s
        // constructor regardless of what ISerializer DI had registered.
        var customSerializer = new Mock<ISerializer>();
        customSerializer.Setup(s => s.Serialize(It.IsAny<TestDataType>())).Returns("custom-payload");

        var connectionFactory = new MockConnectionFactory();
        connectionFactory.DataBaseMock
            .Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), new RedisValue("custom-payload"), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None))
            .ReturnsAsync(true)
            .Verifiable();

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory, customSerializer.Object);
        var entry = service.GetTestCacheEntry(42);

        var result = await entry.SetValueAsync(new TestDataType { Id = 42, Name = "Test" });

        Assert.True(result);
        connectionFactory.DataBaseMock.Verify();
    }

    /// <summary>
    /// A <see cref="IRedisConnectionFactory"/> whose <see cref="IRedisConnectionFactory.ConnectAsync"/>
    /// never completes - used to prove a caller's own <see cref="CancellationToken"/> unblocks the wait
    /// (#141) rather than hanging on the connect forever.
    /// </summary>
    private sealed class NeverConnectingConnectionFactory : IRedisConnectionFactory
    {
        public Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions options) =>
            new TaskCompletionSource<IConnectionMultiplexer>().Task;
    }

    /// <summary>
    /// A <see cref="RedisCacheService"/> that, unlike <see cref="TestRedisCacheService"/>, never calls
    /// <c>StartConnection</c> - so it stays in the genuine "never connected" state a disposal test
    /// needs.
    /// </summary>
    private sealed class NeverConnectedTestRedisCacheService(
        ILogger<RedisCacheService> logger, IProcessTimerFactory processTimerFactory, IRedisConnectionFactory connectionFactory)
        : RedisCacheService(logger, processTimerFactory, connectionFactory)
    {
        protected override Task<ConfigurationOptions> GetConfigurationOptionsAsync() =>
            Task.FromResult(new ConfigurationOptions());
    }
}
