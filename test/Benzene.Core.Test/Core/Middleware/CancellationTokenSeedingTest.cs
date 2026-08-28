using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Middleware;

/// <summary>
/// Coverage for seeding the scope's ambient <see cref="ICancellationTokenAccessor"/>: the
/// <see cref="MiddlewareApplication{TEvent,TContext}"/> token overload sets it so a component resolved
/// during the pipeline observes the transport's cancellation token, while the original no-token
/// overload leaves it at <see cref="CancellationToken.None"/>. Also covers the
/// <see cref="CancellationTokenAccessorExtensions.SeedCancellationToken"/> helper directly.
/// </summary>
public class CancellationTokenSeedingTest
{
    private sealed class CapturingPipeline<TContext> : IMiddlewarePipeline<TContext>
    {
        public CancellationToken Observed { get; private set; }

        public Task HandleAsync(TContext context, IServiceResolver serviceResolver)
        {
            Observed = serviceResolver.GetService<ICancellationTokenAccessor>().CancellationToken;
            return Task.CompletedTask;
        }
    }

    private static IServiceResolverFactory CreateFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped<CancellationTokenAccessor>();
        services.AddScoped<ICancellationTokenAccessor>(x => x.GetService<CancellationTokenAccessor>());
        return new MicrosoftServiceResolverFactory(services);
    }

    [Fact]
    public async Task HandleAsync_WithToken_SeedsTheAccessorForThePipeline()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = new CapturingPipeline<string>();
        var application = new MiddlewareApplication<string, string>(pipeline, e => e);

        await application.HandleAsync("event", CreateFactory(), cts.Token);

        Assert.Equal(cts.Token, pipeline.Observed);
        Assert.True(pipeline.Observed.CanBeCanceled);
    }

    [Fact]
    public async Task HandleAsync_WithoutToken_LeavesTheAccessorAtNone()
    {
        var pipeline = new CapturingPipeline<string>();
        var application = new MiddlewareApplication<string, string>(pipeline, e => e);

        await application.HandleAsync("event", CreateFactory());

        Assert.Equal(CancellationToken.None, pipeline.Observed);
    }

    [Fact]
    public async Task HandleAsync_WithResult_WithToken_SeedsTheAccessor()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = new CapturingPipeline<string>();
        var application = new MiddlewareApplication<string, string, string>(pipeline, e => e, c => c);

        await application.HandleAsync("event", CreateFactory(), cts.Token);

        Assert.Equal(cts.Token, pipeline.Observed);
    }

    [Fact]
    public void SeedCancellationToken_WithRealToken_SetsTheAccessor()
    {
        using var cts = new CancellationTokenSource();
        var resolver = CreateFactory().CreateScope();

        resolver.SeedCancellationToken(cts.Token);

        Assert.Equal(cts.Token, resolver.GetService<ICancellationTokenAccessor>().CancellationToken);
    }

    [Fact]
    public void SeedCancellationToken_WithNone_LeavesTheAccessorAtNone()
    {
        var resolver = CreateFactory().CreateScope();

        resolver.SeedCancellationToken(CancellationToken.None);

        Assert.Equal(CancellationToken.None, resolver.GetService<ICancellationTokenAccessor>().CancellationToken);
    }

    [Fact]
    public void SeedCancellationToken_WhenNoAccessorRegistered_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var resolver = new MicrosoftServiceResolverFactory(new ServiceCollection()).CreateScope();

        // No CancellationTokenAccessor is registered; seeding must be a safe no-op.
        resolver.SeedCancellationToken(cts.Token);
    }

    // --- Phase 1: token overloads on IEntryPointMiddlewareApplication / IMiddlewareApplication /
    // MiddlewareMultiApplication -------------------------------------------------------------

    private sealed class CountingMiddlewareApplication : IMiddlewareApplication<string>
    {
        public int NoTokenCalls { get; private set; }
        public CancellationToken? LastToken { get; private set; }

        public Task HandleAsync(string @event, IServiceResolverFactory serviceResolverFactory)
        {
            NoTokenCalls++;
            LastToken = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A legacy-style implementor that only ever wrote the original two-argument
    /// <see cref="IMiddlewareApplication{TEvent}.HandleAsync(TEvent,IServiceResolverFactory)"/>
    /// method (as every implementor did before this token overload existed) and never overrides the
    /// new default interface method. It must still compile against the interface and behave exactly
    /// as before when driven through the token overload, proving the DIM is a safe, non-breaking
    /// addition for existing (including third-party) implementors.
    /// </summary>
    private sealed class LegacyEntryPointApplication : IEntryPointMiddlewareApplication<string>
    {
        public int Calls { get; private set; }

        public Task SendAsync(string @event)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class LegacyEntryPointApplicationWithResult : IEntryPointMiddlewareApplication<string, string>
    {
        public int Calls { get; private set; }

        public Task<string> SendAsync(string @event)
        {
            Calls++;
            return Task.FromResult(@event);
        }
    }

    [Fact]
    public async Task IMiddlewareApplication_HandleAsync_DimDefault_DelegatesToTokenlessOverload()
    {
        IMiddlewareApplication<string> application = new CountingMiddlewareApplication();

        await application.HandleAsync("event", CreateFactory(), CancellationToken.None);

        Assert.Equal(1, ((CountingMiddlewareApplication)application).NoTokenCalls);
    }

    [Fact]
    public async Task IEntryPointMiddlewareApplication_SendAsync_DimDefault_DelegatesToTokenlessOverload_AndCompiles()
    {
        // The whole point of a default interface method: a legacy implementor that never wrote an
        // override still compiles against the interface and still works when called through the new
        // token-taking member.
        IEntryPointMiddlewareApplication<string> application = new LegacyEntryPointApplication();

        await application.SendAsync("event", CancellationToken.None);

        Assert.Equal(1, ((LegacyEntryPointApplication)application).Calls);
    }

    [Fact]
    public async Task IEntryPointMiddlewareApplication_WithResult_SendAsync_DimDefault_DelegatesToTokenlessOverload_AndCompiles()
    {
        IEntryPointMiddlewareApplication<string, string> application = new LegacyEntryPointApplicationWithResult();

        var result = await application.SendAsync("event", CancellationToken.None);

        Assert.Equal("event", result);
        Assert.Equal(1, ((LegacyEntryPointApplicationWithResult)application).Calls);
    }

    [Fact]
    public async Task EntryPointMiddlewareApplication_SendAsync_WithToken_SeedsTheAccessorForThePipeline()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = new CapturingPipeline<string>();
        var middlewareApplication = new MiddlewareApplication<string, string>(pipeline, e => e);
        IEntryPointMiddlewareApplication<string> entryPoint =
            new EntryPointMiddlewareApplication<string>(middlewareApplication, CreateFactory());

        await entryPoint.SendAsync("event", cts.Token);

        Assert.Equal(cts.Token, pipeline.Observed);
    }

    [Fact]
    public async Task EntryPointMiddlewareApplication_WithResult_SendAsync_WithToken_SeedsTheAccessorForThePipeline()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = new CapturingPipeline<string>();
        var middlewareApplication = new MiddlewareApplication<string, string, string>(pipeline, e => e, c => c);
        IEntryPointMiddlewareApplication<string, string> entryPoint =
            new EntryPointMiddlewareApplication<string, string>(middlewareApplication, CreateFactory());

        await entryPoint.SendAsync("event", cts.Token);

        Assert.Equal(cts.Token, pipeline.Observed);
    }

    private sealed class RecordingMultiPipeline : IMiddlewarePipeline<string>
    {
        private readonly ConcurrentBag<CancellationToken> _observed = [];

        public IReadOnlyCollection<CancellationToken> Observed => _observed;

        public Task HandleAsync(string context, IServiceResolver serviceResolver)
        {
            _observed.Add(serviceResolver.GetService<ICancellationTokenAccessor>().CancellationToken);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MiddlewareMultiApplication_WithToken_SeedsEveryRecordsScope()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = new RecordingMultiPipeline();
        var application = new MiddlewareMultiApplication<string[], string>(pipeline, e => e);

        await application.HandleAsync(["a", "b", "c"], CreateFactory(), cts.Token);

        Assert.Equal(3, pipeline.Observed.Count);
        Assert.All(pipeline.Observed, token => Assert.Equal(cts.Token, token));
    }

    [Fact]
    public async Task MiddlewareMultiApplication_WithoutToken_LeavesEveryRecordsScopeAtNone()
    {
        var pipeline = new RecordingMultiPipeline();
        var application = new MiddlewareMultiApplication<string[], string>(pipeline, e => e);

        await application.HandleAsync(["a", "b"], CreateFactory());

        Assert.Equal(2, pipeline.Observed.Count);
        Assert.All(pipeline.Observed, token => Assert.Equal(CancellationToken.None, token));
    }

    [Fact]
    public async Task MiddlewareMultiApplication_WithResult_WithToken_SeedsEveryRecordsScope()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = new RecordingMultiPipeline();
        var application = new MiddlewareMultiApplication<string[], string, string>(pipeline, e => e, c => c);

        var results = await application.HandleAsync(["a", "b", "c"], CreateFactory(), cts.Token);

        Assert.Equal(3, results.Length);
        Assert.Equal(3, pipeline.Observed.Count);
        Assert.All(pipeline.Observed, token => Assert.Equal(cts.Token, token));
    }

    // --- #225: MiddlewareRouter forwards the ambient token into its nested dispatch -----------------

    /// <summary>
    /// A router that mirrors <c>BenzeneMessageEventHubHandler</c>/<c>BenzeneMessageQueueStorageHandler</c>'s
    /// shape: it overrides the new cancellation-aware 4-arg <c>HandleFunction</c> overload and forwards
    /// the token into a nested <see cref="MiddlewareApplication{TEvent,TContext}"/> dispatch via its own
    /// 3-arg <c>HandleAsync</c> overload.
    /// </summary>
    private sealed class NestedDispatchRouter : MiddlewareRouter<string, string>
    {
        private readonly MiddlewareApplication<string, string> _nestedApplication;

        public NestedDispatchRouter(IServiceResolver serviceResolver, MiddlewareApplication<string, string> nestedApplication)
            : base(serviceResolver)
        {
            _nestedApplication = nestedApplication;
        }

        protected override bool CanHandle(string request) => true;

        protected override string TryExtractRequest(string context) => context;

        protected override Task HandleFunction(string request, string context, IServiceResolverFactory serviceResolverFactory)
            => HandleFunction(request, context, serviceResolverFactory, CancellationToken.None);

        protected override Task HandleFunction(string request, string context, IServiceResolverFactory serviceResolverFactory, CancellationToken cancellationToken)
            => _nestedApplication.HandleAsync(request, serviceResolverFactory, cancellationToken);
    }

    /// <summary>
    /// A router written before the #225 seam existed - it only ever overrides the required abstract
    /// 3-arg <c>HandleFunction</c>. Proves the new virtual 4-arg overload's default implementation
    /// (delegating to the 3-arg one, ignoring the token) keeps such a subclass compiling and behaving
    /// unchanged even when the outer scope has a real ambient token seeded.
    /// </summary>
    private sealed class LegacyRouter : MiddlewareRouter<string, string>
    {
        public int Calls { get; private set; }

        public LegacyRouter(IServiceResolver serviceResolver) : base(serviceResolver) { }

        protected override bool CanHandle(string request) => true;

        protected override string TryExtractRequest(string context) => context;

        protected override Task HandleFunction(string request, string context, IServiceResolverFactory serviceResolverFactory)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MiddlewareRouter_HandleAsync_ForwardsTheAmbientTokenIntoNestedDispatch()
    {
        using var cts = new CancellationTokenSource();
        var innerPipeline = new CapturingPipeline<string>();
        var nestedApplication = new MiddlewareApplication<string, string>(innerPipeline, e => e);

        var outerResolver = CreateFactory().CreateScope();
        outerResolver.SeedCancellationToken(cts.Token);

        var router = new NestedDispatchRouter(outerResolver, nestedApplication);

        await router.HandleAsync("event", () => Task.CompletedTask);

        Assert.Equal(cts.Token, innerPipeline.Observed);
        Assert.True(innerPipeline.Observed.CanBeCanceled);
    }

    [Fact]
    public async Task MiddlewareRouter_HandleAsync_WithoutAmbientToken_NestedDispatchObservesNone()
    {
        var innerPipeline = new CapturingPipeline<string>();
        var nestedApplication = new MiddlewareApplication<string, string>(innerPipeline, e => e);

        var outerResolver = CreateFactory().CreateScope();

        var router = new NestedDispatchRouter(outerResolver, nestedApplication);

        await router.HandleAsync("event", () => Task.CompletedTask);

        Assert.Equal(CancellationToken.None, innerPipeline.Observed);
    }

    [Fact]
    public async Task MiddlewareRouter_HandleAsync_LegacySubclass_DimDefault_DelegatesToTheThreeArgOverload_AndCompiles()
    {
        using var cts = new CancellationTokenSource();
        var outerResolver = CreateFactory().CreateScope();
        outerResolver.SeedCancellationToken(cts.Token);

        var router = new LegacyRouter(outerResolver);

        await router.HandleAsync("event", () => Task.CompletedTask);

        Assert.Equal(1, router.Calls);
    }
}
