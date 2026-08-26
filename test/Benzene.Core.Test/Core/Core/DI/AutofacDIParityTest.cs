using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Autofac;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Test.Examples;
using Moq;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

/// <summary>
/// Regression coverage for the round 7-10 Autofac DI adapter parity fixes (task board #82-#85, ruling
/// <c>work/bug-fix-designs-round7-10-2026-08.md</c> WP-Q). Every test here runs against the real
/// Autofac 6.5.0 package - no mocking of the container - since these are subtle container-lifecycle
/// bugs that a mock would paper over.
/// </summary>
public class AutofacDIParityTest
{
    private interface IWidget
    {
    }

    private class WidgetA : IWidget
    {
    }

    private class WidgetB : IWidget
    {
    }

    // A genuine constructor-injected consumer of IServiceResolver - resolving THIS through Autofac (as
    // a dependency of some other component) is what actually exercises the single-IComponentContext-arg
    // AutofacServiceResolverAdapter constructor. Calling scope.GetService<IServiceResolver>() directly
    // does NOT exercise it: IServiceResolver's own GetService<IServiceResolver>() short-circuits to
    // "return this", handing back the already-fully-constructed scope adapter (built via the two-arg
    // constructor, which always has its factory set) without ever going through AddServiceResolver()'s
    // registration delegate.
    private sealed class ServiceResolverConsumer
    {
        public IServiceResolver Resolver { get; }

        public ServiceResolverConsumer(IServiceResolver resolver)
        {
            Resolver = resolver;
        }
    }

    private sealed class AsyncDisposalSpy : IAsyncDisposable
    {
        public bool DisposedAsync { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposedAsync = true;
            return ValueTask.CompletedTask;
        }
    }

    // #82 - IsTypeRegistered read ComponentRegistryBuilder before Build() ran, so it always returned
    // false and every TryAdd* silently became an unconditional last-write-wins Add*.
    [Fact]
    public void Issue82_TryAddSingleton_SecondCallForSameServiceType_IsIgnored_FirstRegistrationWins()
    {
        var containerBuilder = new ContainerBuilder();
        var services = new AutofacBenzeneServiceContainer(containerBuilder);

        services.TryAddSingleton<IWidget, WidgetA>();
        services.TryAddSingleton<IWidget, WidgetB>();

        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var scope = factory.CreateScope();

        Assert.IsType<WidgetA>(scope.GetService<IWidget>());
    }

    [Fact]
    public void Issue82_IsTypeRegistered_ReflectsRegistrationsMadeBeforeBuild()
    {
        var containerBuilder = new ContainerBuilder();
        var services = new AutofacBenzeneServiceContainer(containerBuilder);

        // The bug under test: IsTypeRegistered read ComponentRegistryBuilder, which is only populated
        // by Build() - so this always reported false pre-Build, no matter what had been registered.
        Assert.False(services.IsTypeRegistered<IWidget>());
        services.AddSingleton<IWidget, WidgetA>();
        Assert.True(services.IsTypeRegistered<IWidget>());
    }

    // The real-world case the ruling calls out: AddMessageHandlers' finder-lock-in fix relies on
    // TryAddSingletonImplementation<IStartUpCheck, ...> being genuinely idempotent, so calling
    // AddMessageHandlers twice (e.g. an outer app scan plus an inner UseBenzeneMessage pipeline scan)
    // must not duplicate every start-up check.
    [Fact]
    public void Issue82_AddMessageHandlersCalledTwice_DoesNotDuplicateStartUpCheckRegistrations()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.Register(_ => Mock.Of<IExampleService>()).InstancePerLifetimeScope();
        containerBuilder.UsingBenzene(x =>
        {
            x.AddMessageHandlers(typeof(ExampleRequestPayload).Assembly);
            x.AddMessageHandlers(typeof(ExampleRequestPayload).Assembly);
        });

        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var scope = factory.CreateScope();

        var checks = scope.GetServices<IStartUpCheck>().ToArray();
        var checksByType = checks.GroupBy(c => c.GetType()).ToDictionary(g => g.Key, g => g.Count());

        // Each concrete IStartUpCheck implementation registered by RegisterHandlerFinderInfrastructure
        // must appear exactly once, no matter how many times AddMessageHandlers ran.
        Assert.All(checksByType, kvp => Assert.Equal(1, kvp.Value));
        Assert.True(checksByType.Count >= 4, $"Expected at least 4 distinct start-up checks, found {checksByType.Count}.");
    }

    // #83 - CreateServiceResolverFactory() called ContainerBuilder.Build(), which throws on a second
    // call. GrpcMethodHandlerFactory.Create() calls IBenzeneServiceContainer.CreateServiceResolverFactory()
    // on every gRPC request, so the second request handled with Autofac wired in used to throw.
    [Fact]
    public void Issue83_CreateServiceResolverFactory_CalledTwice_BothCallsSucceed()
    {
        var containerBuilder = new ContainerBuilder();
        var services = new AutofacBenzeneServiceContainer(containerBuilder);
        services.AddSingleton<IWidget, WidgetA>();

        using var firstFactory = services.CreateServiceResolverFactory();
        using var secondFactory = services.CreateServiceResolverFactory();

        using var firstScope = firstFactory.CreateScope();
        using var secondScope = secondFactory.CreateScope();

        Assert.IsType<WidgetA>(firstScope.GetService<IWidget>());
        Assert.IsType<WidgetA>(secondScope.GetService<IWidget>());
    }

    [Fact]
    public void Issue83_CreateServiceResolverFactory_CalledRepeatedly_SimulatingPerGrpcRequestPattern()
    {
        var containerBuilder = new ContainerBuilder();
        var services = new AutofacBenzeneServiceContainer(containerBuilder);
        services.AddSingleton<IWidget, WidgetA>();
        services.AddScoped<WidgetB>();

        // GrpcMethodHandlerFactory.Create() calls _services.CreateServiceResolverFactory() on every
        // request; simulate a burst of requests against the same, already-registered container.
        for (var i = 0; i < 50; i++)
        {
            using var factory = services.CreateServiceResolverFactory();
            using var scope = factory.CreateScope();

            Assert.IsType<WidgetA>(scope.GetService<IWidget>());
            Assert.NotNull(scope.GetService<WidgetB>());
        }
    }

    // #84 - the single-IComponentContext-arg AutofacServiceResolverAdapter constructor (used by
    // AddServiceResolver()'s registration, and by every AddScoped/AddTransient/AddSingleton
    // (Func<IServiceResolver,T>) overload) never set the IServiceResolverFactory field, so a
    // constructor-injected IServiceResolver could not produce its own IServiceResolverFactory.
    [Fact]
    public void Issue84_ConstructorInjectedServiceResolver_CanResolveItsOwnServiceResolverFactory()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterType<WidgetA>().As<IWidget>().InstancePerDependency();
        containerBuilder.RegisterType<ServiceResolverConsumer>().InstancePerDependency();
        containerBuilder.UsingBenzene(x => x.AddServiceResolver());

        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var scope = factory.CreateScope();

        // Resolving ServiceResolverConsumer makes Autofac construct it via AddServiceResolver()'s
        // registration delegate for its IServiceResolver constructor parameter - the single-
        // IComponentContext-arg AutofacServiceResolverAdapter constructor path.
        var consumer = scope.GetService<ServiceResolverConsumer>();
        var resolver = consumer.Resolver;

        var resolverFactory = resolver.GetService<IServiceResolverFactory>();
        Assert.NotNull(resolverFactory);

        // The factory must actually work - not just be non-null - producing a scope that can resolve
        // registered services (the "spin up nested scopes" use case the ruling calls out).
        using var nestedScope = resolverFactory.CreateScope();
        Assert.IsType<WidgetA>(nestedScope.GetService<IWidget>());

        // TryGetService must special-case IServiceResolverFactory the same way GetService does (mirrors
        // the existing TryGetService_BuiltInTypes_ResolveSymmetricallyWithGetService coverage).
        Assert.NotNull(resolver.TryGetService<IServiceResolverFactory>());
    }

    [Fact]
    public void Issue84_ConstructorInjectedServiceResolver_ServiceResolverFactoryIsStableAcrossCalls()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterType<ServiceResolverConsumer>().InstancePerDependency();
        containerBuilder.UsingBenzene(x => x.AddServiceResolver());

        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var scope = factory.CreateScope();
        var resolver = scope.GetService<ServiceResolverConsumer>().Resolver;

        var first = resolver.GetService<IServiceResolverFactory>();
        var second = resolver.GetService<IServiceResolverFactory>();

        Assert.Same(first, second);
    }

    // #85 - AutofacServiceResolverFactory didn't implement IAsyncDisposable, unlike
    // MicrosoftServiceResolverFactory.
    [Fact]
    public async Task Issue85_DisposeAsync_OwnedContainer_DisposesAsyncDisposableSingletons()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterType<AsyncDisposalSpy>().SingleInstance();

        var factory = new AutofacServiceResolverFactory(containerBuilder);
        AsyncDisposalSpy spy;
        using (var scope = factory.CreateScope())
        {
            spy = scope.GetService<AsyncDisposalSpy>();
        }

        Assert.False(spy.DisposedAsync);

        await factory.DisposeAsync();

        Assert.True(spy.DisposedAsync);
    }

    [Fact]
    public async Task Issue85_DisposeAsync_NonOwningFactory_DoesNotDisposeSharedContainer()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterType<AsyncDisposalSpy>().SingleInstance();

        var services = new AutofacBenzeneServiceContainer(containerBuilder);

        var firstFactory = services.CreateServiceResolverFactory();
        var secondFactory = services.CreateServiceResolverFactory();

        AsyncDisposalSpy spy;
        using (var scope = firstFactory.CreateScope())
        {
            spy = scope.GetService<AsyncDisposalSpy>();
        }

        // Disposing one per-request factory must not tear down the shared underlying container - the
        // other factory (and any future CreateServiceResolverFactory() call) still needs it.
        await ((IAsyncDisposable)firstFactory).DisposeAsync();
        Assert.False(spy.DisposedAsync);

        using var secondScope = secondFactory.CreateScope();
        Assert.Same(spy, secondScope.GetService<AsyncDisposalSpy>());
    }
}
