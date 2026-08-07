using Benzene.Abstractions.DI;

namespace Benzene.EventSourcing;

/// <summary>
/// Registration helpers for event sourcing.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers the single-process <see cref="InMemoryEventStore"/> as the <see cref="IEventStore"/>.
    /// For a fleet, register a distributed store (e.g. the DynamoDB event store) instead.
    /// </summary>
    /// <param name="services">The Benzene service container.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddInMemoryEventStore(this IBenzeneServiceContainer services)
    {
        services.AddSingleton<IEventStore>(new InMemoryEventStore());
        return services;
    }
}
