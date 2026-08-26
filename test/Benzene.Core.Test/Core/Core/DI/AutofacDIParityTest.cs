using System.Linq;
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
}
