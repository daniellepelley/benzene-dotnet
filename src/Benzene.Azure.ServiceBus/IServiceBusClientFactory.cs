using Azure.Messaging.ServiceBus;

namespace Benzene.Azure.ServiceBus;

/// <summary>
/// Creates the underlying <see cref="ServiceBusClient"/> used by <see cref="BenzeneServiceBusWorker"/>
/// to consume an entity. Lets the caller decide how the client is authenticated (connection string,
/// Managed Identity via a <c>TokenCredential</c>, emulator, ...) without the worker prescribing it.
/// </summary>
public interface IServiceBusClientFactory
{
    /// <summary>
    /// Creates a <see cref="ServiceBusClient"/>. The caller (whoever builds this factory) owns the
    /// returned client's lifetime and is responsible for disposing it - <see cref="BenzeneServiceBusWorker"/>
    /// never does, since the same factory may also back an auto-wired health check that keeps using
    /// the client after the worker stops (see <see cref="BenzeneServiceBusWorker.StopAsync"/>'s doc
    /// comment).
    /// </summary>
    /// <returns>The created client.</returns>
    ServiceBusClient Create();
}
