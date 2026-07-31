using Benzene.Abstractions.Serialization;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.DI;

/// <summary>
/// Registering message handlers pulls in the <c>AddBenzene</c> baseline on its own — the router and
/// factory <c>AddMessageHandlers</c> registers depend on it (<see cref="IDefaultStatuses"/>,
/// <see cref="ISerializer"/>, the service resolver, core middleware), so ensuring it there removes the
/// old footgun where a pipeline compiled cleanly and then failed on the first message with
/// <c>IDefaultStatuses</c> unresolvable from inside <c>MessageHandlerFactory</c>. No caller — no
/// transport, no hand-composed function — has to remember <c>AddBenzene()</c> anymore.
/// </summary>
public class AddMessageHandlersEnsuresBaselineTest
{
    [Fact]
    public void AddMessageHandlers_FromAssemblies_RegistersTheBaseline_WithoutAddBenzene()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Deliberately no .AddBenzene() — only handler registration.
        services.UsingBenzene(x => x.AddMessageHandlers(typeof(Defaults).Assembly));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IDefaultStatuses>());
        Assert.NotNull(provider.GetService<ISerializer>());
    }

    [Fact]
    public void AddMessageHandlers_NoArgs_RegistersTheBaseline_WithoutAddBenzene()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The explicit-registration overload takes the same guarantee.
        services.UsingBenzene(x => x.AddMessageHandlers());

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IDefaultStatuses>());
        Assert.NotNull(provider.GetService<ISerializer>());
    }
}
