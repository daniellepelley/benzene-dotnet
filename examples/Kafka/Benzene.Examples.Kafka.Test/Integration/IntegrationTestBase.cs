using Benzene.Examples.App.Data;
using Benzene.Examples.Kafka.Test.Helpers;

namespace Benzene.Examples.Kafka.Test.Integration;

public abstract class InMemoryOrdersTestBase : IDisposable
{
    protected InMemoryOrdersTestBase()
    {
        SetUp();
    }

    public Order[] GetPersistedOrders()
    {
        return InMemoryOrderDbClient.Orders.ToArray();
    }

    public void AddOrder(Order order)
    {
        InMemoryOrderDbClient.Orders.Add(order);
    }

    private void SetUp()
    {
        KafkaSetUp.SetUpAsync().Wait();
        WorkerSetUp.SetUp();
        InMemoryOrderDbClient.Orders.Clear();
    }

    public void Dispose()
    {
        // Stop the worker before deleting its topics: a consumer whose subscribed topics vanish
        // under it does not stop within the host's shutdown timeout.
        WorkerSetUp.TearDownAsync().Wait();
        KafkaSetUp.TearDownAsync().Wait();
    }
}