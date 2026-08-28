using Autofac;
using Benzene.Autofac;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

/// <summary>
/// The <c>AddScoped(instance)</c> / <c>AddTransient(instance)</c> container overloads register an
/// existing object, so resolving the service must hand back that exact instance (like the singleton
/// instance overload does) rather than constructing a fresh one. The Autofac adapter previously
/// ignored the supplied instance and called <c>RegisterType</c>, diverging from both the documented
/// contract and the Microsoft adapter.
/// </summary>
public class ServiceContainerInstanceRegistrationTest
{
    private class Marker
    {
    }

    [Fact]
    public void Autofac_AddScopedInstance_ResolvesTheSuppliedInstance()
    {
        var builder = new ContainerBuilder();
        var instance = new Marker();

        new AutofacBenzeneServiceContainer(builder).AddScoped(instance);

        using var factory = new AutofacServiceResolverFactory(builder);
        using var scope = factory.CreateScope();

        Assert.Same(instance, scope.GetService<Marker>());
    }

    [Fact]
    public void Autofac_AddTransientInstance_ResolvesTheSuppliedInstance()
    {
        var builder = new ContainerBuilder();
        var instance = new Marker();

        new AutofacBenzeneServiceContainer(builder).AddTransient(instance);

        using var factory = new AutofacServiceResolverFactory(builder);
        using var scope = factory.CreateScope();

        Assert.Same(instance, scope.GetService<Marker>());
    }

    [Fact]
    public void Microsoft_AddScopedInstance_ResolvesTheSuppliedInstance()
    {
        var services = new ServiceCollection();
        var instance = new Marker();

        new MicrosoftBenzeneServiceContainer(services).AddScoped(instance);

        using var factory = new MicrosoftServiceResolverFactory(services);
        using var scope = factory.CreateScope();

        Assert.Same(instance, scope.GetService<Marker>());
    }

    [Fact]
    public void Microsoft_AddTransientInstance_ResolvesTheSuppliedInstance()
    {
        var services = new ServiceCollection();
        var instance = new Marker();

        new MicrosoftBenzeneServiceContainer(services).AddTransient(instance);

        using var factory = new MicrosoftServiceResolverFactory(services);
        using var scope = factory.CreateScope();

        Assert.Same(instance, scope.GetService<Marker>());
    }

    // #210: AutofacBenzeneServiceContainer's six generic-routing checks (AddScoped/AddTransient/
    // AddSingleton, both the Type and (Type,Type) overload) used to test IsGenericType, which is true
    // for BOTH an open generic type definition (typeof(GenericWidget<>) - the only shape Autofac's
    // RegisterGeneric accepts) and a CLOSED generic (typeof(GenericWidget<string>) - which
    // RegisterGeneric throws on, since it isn't a type definition). A discovered handler class that
    // happens to be a closed generic therefore threw under Autofac while resolving cleanly under the
    // Microsoft adapter (whose RegisterType/AddScoped(Type) draws no such distinction). Fixed to
    // IsGenericTypeDefinition - the check that actually distinguishes the two.
    private class ClosedGenericWidget<T>
    {
    }

    [Fact]
    public void Autofac_ClosedGenericHandlerType_RegisteredAndResolvedSuccessfully_LikeMicrosoft()
    {
        // AddScoped(Type) is the overload every generic-routing check guards - a closed generic
        // Type instance used to hit RegisterGeneric (which requires an open generic type
        // definition) and throw an ArgumentException at registration time.
        var containerBuilder = new ContainerBuilder();
        new AutofacBenzeneServiceContainer(containerBuilder).AddScoped(typeof(ClosedGenericWidget<string>));

        using var factory = new AutofacServiceResolverFactory(containerBuilder);
        using var scope = factory.CreateScope();

        Assert.IsType<ClosedGenericWidget<string>>(scope.GetService<ClosedGenericWidget<string>>());
    }

    [Fact]
    public void Microsoft_ClosedGenericHandlerType_RegisteredAndResolvedSuccessfully()
    {
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services).AddScoped(typeof(ClosedGenericWidget<string>));

        using var factory = new MicrosoftServiceResolverFactory(services);
        using var scope = factory.CreateScope();

        Assert.IsType<ClosedGenericWidget<string>>(scope.GetService<ClosedGenericWidget<string>>());
    }
}
