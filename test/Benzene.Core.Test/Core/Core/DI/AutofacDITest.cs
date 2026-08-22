using System;
using Autofac;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Autofac;
using Benzene.Core;
using Benzene.Core.Exceptions;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Test.Examples;
using Moq;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

public class AutofacDITest
{
    private sealed class DisposalSpy : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private interface IWidget
    {
    }

    private class Widget : IWidget
    {
    }

    [Fact]
    public void Dispose_ProviderBuiltByFactory_DisposesSingletons()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterType<DisposalSpy>().SingleInstance();

        // The factory built the container (the only constructor takes a ContainerBuilder), so it owns
        // and disposes it - which runs the container's IDisposable singletons. Previously Dispose() was
        // a no-op and they leaked, mirroring the bug already fixed on the Microsoft DI adapter.
        var factory = new AutofacServiceResolverFactory(containerBuilder);
        DisposalSpy spy;
        using (var scope = factory.CreateScope())
        {
            spy = scope.GetService<DisposalSpy>();
        }
        factory.Dispose();

        Assert.True(spy.Disposed);
    }

    [Fact]
    public void Dispose_Scope_DisposesInstancePerLifetimeScopeServices_ButLeavesSingletonsForTheFactory()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterType<DisposalSpy>().InstancePerLifetimeScope();

        using var factory = new AutofacServiceResolverFactory(containerBuilder);

        DisposalSpy spy;
        using (var scope = factory.CreateScope())
        {
            spy = scope.GetService<DisposalSpy>();
            Assert.False(spy.Disposed);
        }

        // Disposing the scope (not the factory) already released a scoped instance.
        Assert.True(spy.Disposed);
    }

    [Fact]
    public void AddSingleton_ResolvesTheSameInstanceAcrossScopes()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.UsingBenzene(x => x.AddSingleton<Widget>());

        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var scope1 = factory.CreateScope();
        using var scope2 = factory.CreateScope();

        var first = scope1.GetService<Widget>();
        var second = scope2.GetService<Widget>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddScoped_ResolvesTheSameInstanceWithinAScope_ButADifferentInstancePerScope()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.UsingBenzene(x => x.AddScoped<Widget>());

        using var factory = new AutofacServiceResolverFactory(containerBuilder);

        using var scope1 = factory.CreateScope();
        var firstFromScope1 = scope1.GetService<Widget>();
        var secondFromScope1 = scope1.GetService<Widget>();
        Assert.Same(firstFromScope1, secondFromScope1);

        using var scope2 = factory.CreateScope();
        var fromScope2 = scope2.GetService<Widget>();
        Assert.NotSame(firstFromScope1, fromScope2);
    }

    [Fact]
    public void AddTransient_ResolvesADifferentInstanceEachTime()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.UsingBenzene(x => x.AddTransient<Widget>());

        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var scope = factory.CreateScope();

        var first = scope.GetService<Widget>();
        var second = scope.GetService<Widget>();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void AddScoped_AsServiceAndImplementation_ResolvesByTheServiceType()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.UsingBenzene(x => x.AddScoped<IWidget, Widget>());

        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var scope = factory.CreateScope();

        var widget = scope.GetService<IWidget>();

        Assert.NotNull(widget);
        Assert.IsType<Widget>(widget);
    }

    [Fact]
    public void AddMessageHandlers()
    {
        var containerBuilder = new ContainerBuilder();
        containerBuilder.Register(_ => Mock.Of<IExampleService>()).InstancePerLifetimeScope();
        containerBuilder.UsingBenzene(x => x.AddMessageHandlers(typeof(ExampleRequestPayload).Assembly));

        using var factory = new AutofacServiceResolverFactory(containerBuilder);

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
        var containerBuilder = new ContainerBuilder();

        using var factory = new AutofacServiceResolverFactory(containerBuilder);

        using var serviceResolver = factory.CreateScope();

        var serviceResolver2 = serviceResolver.GetService<IServiceResolver>();
        Assert.NotNull(serviceResolver2);
    }

    [Fact]
    public void TryGetService_BuiltInTypes_ResolveSymmetricallyWithGetService()
    {
        var containerBuilder = new ContainerBuilder();
        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var serviceResolver = factory.CreateScope();

        // TryGetService must special-case the built-in types the same way GetService does.
        Assert.NotNull(serviceResolver.TryGetService<IServiceResolver>());
        Assert.NotNull(serviceResolver.TryGetService<IServiceResolverFactory>());
        Assert.NotNull(serviceResolver.GetService<IServiceResolverFactory>());
    }

    [Fact]
    public void GetService_Unregistered_ThrowsBenzeneException_WithHint_PreservingTheOriginalError()
    {
        var containerBuilder = new ContainerBuilder();
        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var serviceResolver = factory.CreateScope();

        // BenzeneResolutionException, not the base BenzeneException: the specific type is what lets a
        // transport tell a wiring failure from a business one without matching on message text.
        var ex = Assert.Throws<BenzeneResolutionException>(() => serviceResolver.GetService<IMiddlewareFactory>());

        Assert.True(BenzeneFailure.IsInfrastructure(ex));

        // The container's real error is preserved (never masked by the diagnostic), and the message
        // carries the actionable registration hint derived from the requested type itself. The hint
        // text is container-agnostic (RegistrationCheck), so it matches the Microsoft DI adapter's.
        Assert.NotNull(ex.InnerException);
        Assert.Contains("IMiddlewareFactory", ex.Message);
        Assert.Contains(".UsingBenzene(x => x.AddBenzene())", ex.Message);
    }
}
