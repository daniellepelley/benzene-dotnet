using System;
using Autofac;
using Benzene.Autofac;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

/// <summary>
/// Regression coverage for task board #210 (round 14, <c>work/bug-fix-designs-round14-2026-08.md</c>
/// §3): six methods on <see cref="AutofacBenzeneServiceContainer"/> routed on <c>Type.IsGenericType</c>
/// (true for both open AND closed generics) instead of <c>Type.IsGenericTypeDefinition</c> (true only
/// for open generics). A closed generic <see cref="Type"/> - e.g. a discovered handler class that
/// happens to be <c>ClosedGenericHandler&lt;Widget&gt;</c> rather than an open <c>ClosedGenericHandler&lt;&gt;</c>
/// - was handed to Autofac's <c>RegisterGeneric</c>, which requires an open generic type definition and
/// throws on a closed one. The Microsoft DI adapter has no generic branching at all (it forwards every
/// <see cref="Type"/> straight to <see cref="IServiceCollection"/>, which handles open and closed
/// generics uniformly) and so never had this asymmetry - used here as the control.
///
/// <c>IServiceResolver</c> only exposes the generic <c>GetService&lt;T&gt;()</c> form, so tests
/// resolve via the statically-known closed type (<c>ClosedGenericHandler&lt;Widget&gt;</c>) even though
/// registration goes through the <see cref="Type"/>-typed overloads under test.
/// </summary>
public class AutofacClosedGenericRoutingTest
{
    private interface IHandler<T>
    {
    }

    // A CLOSED generic type: ClosedGenericHandler<Widget> is a fully-constructed type
    // (IsGenericType == true, IsGenericTypeDefinition == false) - not an open generic definition like
    // typeof(ClosedGenericHandler<>) (IsGenericType == true, IsGenericTypeDefinition == true).
    private class ClosedGenericHandler<T> : IHandler<T>
    {
    }

    private class Widget
    {
    }

    private static readonly Type ClosedGenericType = typeof(ClosedGenericHandler<Widget>);
    private static readonly Type ClosedGenericServiceType = typeof(IHandler<Widget>);

    // Sanity check on the premise itself, so a future .NET/xUnit change can't silently invalidate this
    // whole test file without a loud failure.
    [Fact]
    public void Premise_ClosedGenericType_IsGenericType_ButNotGenericTypeDefinition()
    {
        Assert.True(ClosedGenericType.IsGenericType);
        Assert.False(ClosedGenericType.IsGenericTypeDefinition);
    }

    [Fact]
    public void MicrosoftAdapter_AddScoped_ClosedGenericType_Succeeds()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);

        container.AddScoped(ClosedGenericType);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService(ClosedGenericType));
    }

    [Fact]
    public void AutofacAdapter_AddScoped_ClosedGenericType_Succeeds_MatchingMicrosoftAdapter()
    {
        var containerBuilder = new ContainerBuilder();
        var container = new AutofacBenzeneServiceContainer(containerBuilder);

        // Before the #210 fix this threw ArgumentException from Autofac's RegisterGeneric, which
        // requires an open generic type definition - the closed ClosedGenericHandler<Widget> failed the
        // IsGenericType check into the generic-registration branch and blew up there. The Microsoft
        // adapter (above) has no such branch and succeeds unconditionally.
        container.AddScoped(ClosedGenericType);

        using var factory = container.CreateServiceResolverFactory();
        using var scope = factory.CreateScope();

        Assert.NotNull(scope.GetService<ClosedGenericHandler<Widget>>());
    }

    [Fact]
    public void AutofacAdapter_AddScoped_ServiceAndImplementation_ClosedGenericImplementation_Succeeds()
    {
        var containerBuilder = new ContainerBuilder();
        var container = new AutofacBenzeneServiceContainer(containerBuilder);

        container.AddScoped(ClosedGenericServiceType, ClosedGenericType);

        using var factory = container.CreateServiceResolverFactory();
        using var scope = factory.CreateScope();

        var resolved = scope.GetService<IHandler<Widget>>();
        Assert.NotNull(resolved);
        Assert.IsType<ClosedGenericHandler<Widget>>(resolved);
    }

    [Fact]
    public void AutofacAdapter_AddTransient_ClosedGenericType_Succeeds()
    {
        var containerBuilder = new ContainerBuilder();
        var container = new AutofacBenzeneServiceContainer(containerBuilder);

        container.AddTransient(ClosedGenericType);

        using var factory = container.CreateServiceResolverFactory();
        using var scope = factory.CreateScope();

        Assert.NotNull(scope.GetService<ClosedGenericHandler<Widget>>());
    }

    [Fact]
    public void AutofacAdapter_AddTransient_ServiceAndImplementation_ClosedGenericImplementation_Succeeds()
    {
        var containerBuilder = new ContainerBuilder();
        var container = new AutofacBenzeneServiceContainer(containerBuilder);

        container.AddTransient(ClosedGenericServiceType, ClosedGenericType);

        using var factory = container.CreateServiceResolverFactory();
        using var scope = factory.CreateScope();

        Assert.NotNull(scope.GetService<IHandler<Widget>>());
    }

    [Fact]
    public void AutofacAdapter_AddSingleton_ClosedGenericType_Succeeds()
    {
        var containerBuilder = new ContainerBuilder();
        var container = new AutofacBenzeneServiceContainer(containerBuilder);

        container.AddSingleton(ClosedGenericType);

        using var factory = container.CreateServiceResolverFactory();
        using var scope = factory.CreateScope();

        Assert.NotNull(scope.GetService<ClosedGenericHandler<Widget>>());
    }

    [Fact]
    public void AutofacAdapter_AddSingleton_ServiceAndImplementation_ClosedGenericImplementation_Succeeds()
    {
        var containerBuilder = new ContainerBuilder();
        var container = new AutofacBenzeneServiceContainer(containerBuilder);

        container.AddSingleton(ClosedGenericServiceType, ClosedGenericType);

        using var factory = container.CreateServiceResolverFactory();
        using var scope = factory.CreateScope();

        Assert.NotNull(scope.GetService<IHandler<Widget>>());
    }

    // The other half of the ruling: an OPEN generic registration must still take the generic-definition
    // path (Autofac's RegisterGeneric), not be broken by narrowing the check to IsGenericTypeDefinition.
    [Fact]
    public void AutofacAdapter_AddScoped_OpenGenericType_StillResolvesPerClosedRequest()
    {
        var containerBuilder = new ContainerBuilder();
        var container = new AutofacBenzeneServiceContainer(containerBuilder);

        container.AddScoped(typeof(IHandler<>), typeof(ClosedGenericHandler<>));

        using var factory = container.CreateServiceResolverFactory();
        using var scope = factory.CreateScope();

        var resolved = scope.GetService<IHandler<Widget>>();
        Assert.NotNull(resolved);
        Assert.IsType<ClosedGenericHandler<Widget>>(resolved);
    }
}
