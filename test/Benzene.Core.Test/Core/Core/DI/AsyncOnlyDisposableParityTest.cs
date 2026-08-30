using System;
using System.Threading.Tasks;
using Autofac;
using Benzene.Autofac;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

/// <summary>
/// Task board #266 (round 16, WP-A, <c>work/bug-fix-plan-round16-2026-08.md</c>): both DI adapters
/// must dispose an <see cref="IAsyncDisposable"/>-only, container-owned service identically -
/// without throwing, and by actually running its <c>DisposeAsync</c> - when torn down via the
/// synchronous <c>IDisposable</c> path that <c>Benzene.Abstractions.DI.IServiceResolver</c> exposes
/// (the only disposal contract that exists; see the <c>[OPEN]</c> entry in
/// <c>work/outstanding-bugs.md</c> about whether that should eventually change). Before the #266
/// fix, Autofac already got this right (its own <c>ILifetimeScope.Dispose()</c> bridges
/// IAsyncDisposable-only components) while Microsoft.Extensions.DependencyInjection threw
/// <see cref="InvalidOperationException"/> - this test pins both adapters to the same, correct
/// behavior going forward.
/// </summary>
public class AsyncOnlyDisposableParityTest
{
    private sealed class AsyncOnlyResource : IAsyncDisposable
    {
        public bool DisposedAsync { get; private set; }
        public ValueTask DisposeAsync() { DisposedAsync = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void Autofac_And_Microsoft_BothDisposeAnAsyncOnlyDisposableSingleton_WithoutThrowing_AndActuallyRunDisposeAsync()
    {
        // Microsoft.Extensions.DependencyInjection adapter.
        var services = new ServiceCollection();
        services.AddSingleton<AsyncOnlyResource>();
        var microsoftFactory = new MicrosoftServiceResolverFactory(services);
        AsyncOnlyResource microsoftResource;
        using (var scope = microsoftFactory.CreateScope())
        {
            microsoftResource = scope.GetService<AsyncOnlyResource>();
        }

        var microsoftEx = Record.Exception(() => microsoftFactory.Dispose());

        // Autofac adapter.
        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterType<AsyncOnlyResource>().SingleInstance();
        var autofacFactory = new AutofacServiceResolverFactory(containerBuilder);
        AsyncOnlyResource autofacResource;
        using (var scope = autofacFactory.CreateScope())
        {
            autofacResource = scope.GetService<AsyncOnlyResource>();
        }

        var autofacEx = Record.Exception(() => autofacFactory.Dispose());

        Assert.Null(microsoftEx);
        Assert.Null(autofacEx);
        Assert.True(microsoftResource.DisposedAsync);
        Assert.True(autofacResource.DisposedAsync);
    }

    [Fact]
    public void Autofac_And_Microsoft_BothDisposeAnAsyncOnlyDisposableScopedService_WithoutThrowing_AndActuallyRunDisposeAsync()
    {
        // Microsoft.Extensions.DependencyInjection adapter.
        var services = new ServiceCollection();
        services.AddScoped<AsyncOnlyResource>();
        using var microsoftFactory = new MicrosoftServiceResolverFactory(services);
        var microsoftScope = microsoftFactory.CreateScope();
        var microsoftResource = microsoftScope.GetService<AsyncOnlyResource>();

        var microsoftEx = Record.Exception(() => microsoftScope.Dispose());

        // Autofac adapter.
        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterType<AsyncOnlyResource>().InstancePerLifetimeScope();
        using var autofacFactory = new AutofacServiceResolverFactory(containerBuilder);
        var autofacScope = autofacFactory.CreateScope();
        var autofacResource = autofacScope.GetService<AsyncOnlyResource>();

        var autofacEx = Record.Exception(() => autofacScope.Dispose());

        Assert.Null(microsoftEx);
        Assert.Null(autofacEx);
        Assert.True(microsoftResource.DisposedAsync);
        Assert.True(autofacResource.DisposedAsync);
    }
}
