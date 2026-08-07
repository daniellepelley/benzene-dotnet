using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.EventSourcing;
using Xunit;

namespace Benzene.Test.EventSourcing;

public class InMemoryEventStoreTest
{
    private static EventEnvelope Event(string type, string payload = "{}") => new(type, payload);

    [Fact]
    public async Task Append_ToNewStream_AssignsSequentialVersions_AndReadsBackInOrder()
    {
        var store = new InMemoryEventStore();

        var newVersion = await store.AppendAsync("acct-1", expectedVersion: 0,
            new[] { Event("Opened"), Event("Debited") });

        Assert.Equal(2, newVersion);
        var events = await store.ReadAsync("acct-1");
        Assert.Equal(new[] { "Opened", "Debited" }, events.Select(e => e.EventType));
        Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.Version));
    }

    [Fact]
    public async Task Append_WithStaleExpectedVersion_ThrowsConcurrency()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("acct-1", 0, new[] { Event("Opened") });   // stream now at v1

        var ex = await Assert.ThrowsAsync<EventStoreConcurrencyException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 0, new[] { Event("Debited") }));

        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task Append_Incrementally_ContinuesTheVersionSequence()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("acct-1", 0, new[] { Event("Opened"), Event("Debited") });   // v1, v2

        var newVersion = await store.AppendAsync("acct-1", expectedVersion: 2, new[] { Event("Credited") });

        Assert.Equal(3, newVersion);
        var events = await store.ReadAsync("acct-1");
        Assert.Equal(new long[] { 1, 2, 3 }, events.Select(e => e.Version));
    }

    [Fact]
    public async Task Read_FromVersion_ReturnsOnlyLaterEvents()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("acct-1", 0, new[] { Event("A"), Event("B"), Event("C") });

        var events = await store.ReadAsync("acct-1", fromVersion: 1);

        Assert.Equal(new[] { "B", "C" }, events.Select(e => e.EventType));
    }

    [Fact]
    public async Task Read_UnknownStream_IsEmpty()
    {
        var store = new InMemoryEventStore();

        var events = await store.ReadAsync("nope");

        Assert.Empty(events);
    }

    [Fact]
    public async Task Rehydrate_FoldsTheStreamIntoState()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("acct-1", 0, new[] { Event("Debited", "10"), Event("Credited", "30") });

        // A pure fold over the stream — the essence of rehydration.
        var events = await store.ReadAsync("acct-1");
        var balance = events.Aggregate(0, (bal, e) => e.EventType == "Debited"
            ? bal - int.Parse(e.Payload)
            : bal + int.Parse(e.Payload));

        Assert.Equal(20, balance);
    }
}
