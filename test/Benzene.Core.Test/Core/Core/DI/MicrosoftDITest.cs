using System;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Benzene.Core.Exceptions;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

public class MicrosoftDependencyInjectionTest
{
    private sealed class DisposalSpy : IDisposable, IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public bool DisposedAsync { get; private set; }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { DisposedAsync = true; return ValueTask.CompletedTask; }
    }

    // Task board #266 (round 16, WP-A) - a user-registered scoped/transient service that implements
    // ONLY IAsyncDisposable (an entirely ordinary, idiomatic .NET pattern for an async-native
    // client/connection) used to crash MicrosoftServiceResolverAdapter.Dispose() with
    // InvalidOperationException on every message that resolved it - the exact
    // "MiddlewareApplication.HandleAsync's `using var serviceResolver = ...`" path - and the resource's
    // own DisposeAsync never ran (crash AND leak). See review-round16-core-2026-08.md §1.
    private sealed class AsyncOnlyResource : IAsyncDisposable
    {
        public bool DisposedAsync { get; private set; }
        public ValueTask DisposeAsync() { DisposedAsync = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void Issue266_ScopedAsyncOnlyDisposable_ScopeDisposal_DoesNotThrow_AndActuallyDisposesAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<AsyncOnlyResource>();

        using var factory = new MicrosoftServiceResolverFactory(services);

        AsyncOnlyResource resource;
        using (var resolver = factory.CreateScope())
        {
            resource = resolver.GetService<AsyncOnlyResource>();
            Assert.False(resource.DisposedAsync);
        }

        Assert.True(resource.DisposedAsync);
    }

    [Fact]
    public void Issue266_SingletonAsyncOnlyDisposable_FactoryDisposal_DoesNotThrow_AndActuallyDisposesAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AsyncOnlyResource>();

        var factory = new MicrosoftServiceResolverFactory(services);
        AsyncOnlyResource resource;
        using (var scope = factory.CreateScope())
        {
            resource = scope.GetService<AsyncOnlyResource>();
        }

        var ex = Record.Exception(() => factory.Dispose());

        Assert.Null(ex);
        Assert.True(resource.DisposedAsync);
    }

    [Fact]
    public void Dispose_ProviderBuiltByFactory_DisposesSingletons()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DisposalSpy>();

        // The factory built the provider (IServiceCollection ctor), so it owns and disposes it - which
        // runs the container's disposable singletons. Previously Dispose() was a no-op and they leaked.
        //
        // #266 (round 16, WP-A) changed WHICH of the two disposal methods actually gets invoked on a
        // singleton that implements both: MicrosoftServiceResolverFactory.Dispose() now bridges to the
        // provider's own DisposeAsync() (unbounded wait) whenever the provider supports it, rather than
        // calling its synchronous Dispose() - because Microsoft.Extensions.DependencyInjection's
        // synchronous Dispose() throws InvalidOperationException the moment ANY container-owned
        // instance implements only IAsyncDisposable, and the adapter has no way to know in advance
        // whether that's true without attempting disposal. Routing every synchronous factory-level
        // Dispose() through the provider's DisposeAsync() sidesteps that failure mode uniformly (see
        // MicrosoftServiceResolverAdapter.Dispose()'s equivalent, matching fix) - which means a
        // singleton implementing BOTH interfaces (like DisposalSpy here) now observes DisposedAsync,
        // not Disposed, same as calling factory.DisposeAsync() directly always did. An IDisposable-ONLY
        // singleton is unaffected - the provider's own DisposeAsync() still calls Dispose() on it, since
        // that is Microsoft.Extensions.DependencyInjection's own documented fallback behavior.
        var factory = new MicrosoftServiceResolverFactory(services);
        DisposalSpy spy;
        using (var scope = factory.CreateScope())
        {
            spy = scope.GetService<DisposalSpy>();
        }
        factory.Dispose();

        Assert.True(spy.DisposedAsync);
    }

    [Fact]
    public async Task DisposeAsync_ProviderBuiltByFactory_AsyncDisposesSingletons()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DisposalSpy>();

        var factory = new MicrosoftServiceResolverFactory(services);
        DisposalSpy spy;
        using (var scope = factory.CreateScope())
        {
            spy = scope.GetService<DisposalSpy>();
        }
        await factory.DisposeAsync();

        Assert.True(spy.DisposedAsync);
    }

    [Fact]
    public void Dispose_ExternallySuppliedProvider_IsNotDisposedByTheFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DisposalSpy>();
        var provider = services.BuildServiceProvider();
        var spy = provider.GetService<DisposalSpy>();

        // The factory was handed a provider it did not build (IServiceProvider ctor); disposing the
        // factory must NOT dispose that provider - the caller owns its lifetime.
        var factory = new MicrosoftServiceResolverFactory(provider);
        factory.Dispose();

        Assert.False(spy.Disposed);
        provider.Dispose(); // now the real owner disposes it
        Assert.True(spy.Disposed);
    }
    [Fact]
    public void AddMessageHandlers()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Mock.Of<IExampleService>());
        services.UsingBenzene(x => x.AddMessageHandlers(typeof(ExampleRequestPayload).Assembly));

        using var factory = new MicrosoftServiceResolverFactory(services);

        using var serviceResolver = factory.CreateScope();

        var handler = serviceResolver.GetService<ExampleMessageHandler>();
        Assert.NotNull(handler);

        var tryHandler = serviceResolver.TryGetService<ExampleMessageHandler>();
        Assert.NotNull(tryHandler);

        var tryFail = serviceResolver.TryGetService<ExampleRequestPayload>();
        Assert.Null(tryFail);
    }

    [Fact]
    public void AddServiceResolver()
    {
        var services = new ServiceCollection();

        using var factory = new MicrosoftServiceResolverFactory(services);

        using var serviceResolver = factory.CreateScope();

        var serviceResolver2 = serviceResolver.GetService<IServiceResolver>();
        Assert.NotNull(serviceResolver2);
    }

    [Fact]
    public void TryGetService_BuiltInTypes_ResolveSymmetricallyWithGetService()
    {
        var services = new ServiceCollection();
        using var factory = new MicrosoftServiceResolverFactory(services);
        using var serviceResolver = factory.CreateScope();

        // TryGetService must special-case the built-in types the same way GetService does (the two
        // used to diverge - GetService handled IServiceResolverFactory, TryGetService didn't).
        Assert.NotNull(serviceResolver.TryGetService<IServiceResolver>());
        Assert.NotNull(serviceResolver.TryGetService<IServiceResolverFactory>());
        Assert.NotNull(serviceResolver.GetService<IServiceResolverFactory>());
    }

    [Fact]
    public void GetService_Unregistered_ThrowsBenzeneException_WithHint_PreservingTheOriginalError()
    {
        var services = new ServiceCollection();
        using var factory = new MicrosoftServiceResolverFactory(services);
        using var serviceResolver = factory.CreateScope();

        // BenzeneResolutionException, not the base BenzeneException: the specific type is what lets a
        // transport tell a wiring failure from a business one without matching on message text.
        var ex = Assert.Throws<BenzeneResolutionException>(() => serviceResolver.GetService<IMiddlewareFactory>());

        Assert.True(BenzeneFailure.IsInfrastructure(ex));

        // The container's real error is preserved (never masked by the diagnostic), and the message
        // carries the actionable registration hint derived from the requested type itself.
        Assert.NotNull(ex.InnerException);
        Assert.Contains("IMiddlewareFactory", ex.Message);
        Assert.Contains(".UsingBenzene(x => x.AddBenzene())", ex.Message);
    }
}
