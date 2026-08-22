using System;
using System.Text.Json;
using System.Threading.Tasks;
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

        var result = await healthcheck.ExecuteAsync();

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

        var result = await healthcheck.ExecuteAsync();

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
        connectionFactory.DataBaseMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(true);
        connectionFactory.DataBaseMock.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), When.Always, CommandFlags.None)).ReturnsAsync(true);

        var service = new TestRedisCacheService(NullLogger<RedisCacheService>.Instance, new DebugTimerFactory(), connectionFactory);
        var actions = service.GetTestMultipleEntries(23, 45);

        Assert.True(await actions.SetValueAsync(new TestDataType { Id = 42, Name = "Test" }));

        Assert.True(await actions.InvalidateAsync());
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
