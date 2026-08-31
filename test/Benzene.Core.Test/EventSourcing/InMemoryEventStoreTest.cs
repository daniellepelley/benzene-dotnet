using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
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

    [Fact]
    public async Task Append_WhenABatchElementThrowsMidBatch_LeavesTheStreamUnaffected()
    {
        // #125: a mid-batch failure (here, a null event) must not leave a partial append visible —
        // the store's atomicity guarantee must match DynamoDbEventStore's all-or-nothing transaction.
        var store = new InMemoryEventStore();
        await store.AppendAsync("acct-1", 0, new[] { Event("Opened") });   // stream now at v1

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            store.AppendAsync("acct-1", expectedVersion: 1, new[] { Event("Debited"), null!, Event("Credited") }));

        var events = await store.ReadAsync("acct-1");
        Assert.Equal(new[] { "Opened" }, events.Select(e => e.EventType));
        Assert.Equal(new long[] { 1 }, events.Select(e => e.Version));

        // The stream must still be exactly at v1, so a correct retry with expectedVersion=1 succeeds.
        var newVersion = await store.AppendAsync("acct-1", expectedVersion: 1, new[] { Event("Debited") });
        Assert.Equal(2, newVersion);
    }

    [Fact]
    public async Task AppendAsync_WithAnAlreadyCancelledToken_ThrowsWithoutMutatingState()
    {
        // #130
        var store = new InMemoryEventStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.AppendAsync("acct-1", 0, new[] { Event("Opened") }, cts.Token));

        Assert.Empty(await store.ReadAsync("acct-1"));
    }

    [Fact]
    public async Task ReadAsync_WithAnAlreadyCancelledToken_Throws()
    {
        // #130
        var store = new InMemoryEventStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.ReadAsync("acct-1", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Append_MoreThanMaxEventsPerAppend_Throws()
    {
        // #131 — mirrors DynamoDbEventStore's transaction-size limit so app code written against
        // either store observes the same ceiling.
        var store = new InMemoryEventStore();
        var tooMany = Enumerable.Range(0, 101).Select(_ => Event("E")).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync("acct-1", 0, tooMany));
    }

    [Fact]
    public async Task Append_WithANegativeExpectedVersion_Throws()
    {
        // #258 — mirrors DynamoDbEventStore's guard (round 11's #121 fix): before this fix a negative
        // expectedVersion fell through to an EventStoreConcurrencyException here instead of the same
        // ArgumentOutOfRangeException DynamoDbEventStore throws for the identical caller mistake - a
        // test-vs-prod divergence in exception type for the same input.
        var store = new InMemoryEventStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.AppendAsync("acct-1", expectedVersion: -1, new[] { Event("Debited") }));
    }

    [Fact]
    public async Task Append_Exactly100Events_AtExpectedVersionZero_Succeeds()
    {
        // #271 contract parity — a brand-new stream (expectedVersion == 0) has no reserved
        // condition-check slot, so a genuine 100-event append must still succeed here too.
        var store = new InMemoryEventStore();
        var exactly100 = Enumerable.Range(0, 100).Select(_ => Event("E")).ToArray();

        var version = await store.AppendAsync("acct-1", 0, exactly100);

        Assert.Equal(100, version);
    }

    [Fact]
    public async Task Append_100EventsAtAnExpectedVersionGreaterThanZero_Throws()
    {
        // #271 contract parity — DynamoDbEventStore reserves one of its 100 transact-write items for
        // the version ConditionCheck when appending onto an EXISTING stream (expectedVersion > 0), so
        // its effective per-call cap there is 99, not 100. InMemoryEventStore must enforce the SAME
        // observable contract so app code written against either store behaves identically, even
        // though this store has no physical condition-check item of its own.
        var store = new InMemoryEventStore();
        var exactly100 = Enumerable.Range(0, 100).Select(_ => Event("E")).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync("acct-1", expectedVersion: 5, exactly100));
    }

    [Fact]
    public async Task Append_99EventsAtAnExpectedVersionGreaterThanZero_Succeeds()
    {
        // #271 contract parity — 99 is exactly at the effective cap for an existing-stream append.
        var store = new InMemoryEventStore();
        var exactly99 = Enumerable.Range(0, 99).Select(_ => Event("E")).ToArray();

        // Seed the stream up to version 5 so expectedVersion: 5 is valid.
        await store.AppendAsync("acct-1", 0, new[] { Event("Seed") });
        for (var v = 1; v < 5; v++)
        {
            await store.AppendAsync("acct-1", v, new[] { Event("Seed") });
        }

        var version = await store.AppendAsync("acct-1", expectedVersion: 5, exactly99);

        Assert.Equal(104, version);
    }

    [Fact]
    public async Task Append_RejectedAgainstAnUnknownStream_DoesNotLeakAnEmptyStreamEntry()
    {
        // #132 — a rejected append must not register the unknown stream id at all; otherwise every
        // rejected append against an unknown id leaks an empty List<StoredEvent> forever.
        var store = new InMemoryEventStore();

        await Assert.ThrowsAsync<EventStoreConcurrencyException>(() =>
            store.AppendAsync("never-existed", expectedVersion: 5, new[] { Event("X") }));

        var streamsField = typeof(InMemoryEventStore).GetField("_streams", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var streams = (IDictionary)streamsField.GetValue(store)!;
        Assert.Empty(streams.Keys.Cast<string>());
    }
}
