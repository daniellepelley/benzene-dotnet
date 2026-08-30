using System;
using Benzene.Cache.Redis;
using Benzene.Diagnostics.Timers;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Cache.Redis.Instance;
using Benzene.Test.Cache.Redis.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Cache.Redis;

/// <summary>
/// Regression coverage for task board #262 (round 16, WP-A,
/// <c>work/bug-fix-plan-round16-2026-08.md</c>): <see cref="RedisCacheService"/> is
/// <see cref="IAsyncDisposable"/>-only, so any purely-synchronous container disposal path - the only
/// kind <c>Benzene.Abstractions.DI.IServiceResolver</c> exposes - used to throw
/// <see cref="InvalidOperationException"/> the moment Microsoft.Extensions.DependencyInjection needed
/// to tear it down, both for a per-message <c>AddScoped</c> registration (reproducing
/// <c>AwsLambdaEntryPoint.FunctionHandlerAsync</c>'s per-invocation scope) and an <c>AddSingleton</c>
/// registration disposed via <see cref="MicrosoftServiceResolverFactory.Dispose"/> (the ONLY disposal
/// path <c>Benzene.Aws.Lambda.Core</c> has - see <c>review-round16-infrastructure-2026-08.md</c> §1).
/// </summary>
public class RedisCacheServiceContainerDisposalTest
{
    [Fact]
    public void ScopedRedisCacheService_PerMessageScopeDisposal_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IProcessTimerFactory>(_ => new DebugTimerFactory());
        services.AddScoped<Benzene.Cache.Redis.IRedisConnectionFactory>(_ => new MockConnectionFactory());
        services.AddScoped<TestRedisCacheService>();

        using var factory = new MicrosoftServiceResolverFactory(services);
        var scope = factory.CreateScope();

        var service = scope.GetService<TestRedisCacheService>();
        Assert.NotNull(service);

        var ex = Record.Exception(() => scope.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public void SingletonRedisCacheService_SyncFactoryDisposal_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProcessTimerFactory>(_ => new DebugTimerFactory());
        services.AddSingleton<Benzene.Cache.Redis.IRedisConnectionFactory>(_ => new MockConnectionFactory());
        services.AddSingleton<TestRedisCacheService>();

        var factory = new MicrosoftServiceResolverFactory(services);
        var resolver = factory.CreateScope();
        var service = resolver.GetService<TestRedisCacheService>();
        Assert.NotNull(service);

        var ex = Record.Exception(() => factory.Dispose());

        Assert.Null(ex);

        resolver.Dispose();
    }
}
