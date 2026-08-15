using Benzene.Example.Outbox.Domain;

namespace Benzene.Example.Outbox.Store;

/// <summary>Stands in for a real database table — just enough to show an order was (or wasn't) written.</summary>
public sealed class Orders
{
    private readonly List<Order> _orders = [];

    public IReadOnlyList<Order> All => _orders;

    public void Add(Order order) => _orders.Add(order);
}
