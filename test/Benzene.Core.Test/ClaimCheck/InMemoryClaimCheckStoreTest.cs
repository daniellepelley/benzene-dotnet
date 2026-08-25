using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.ClaimCheck;
using Xunit;

namespace Benzene.Test.ClaimCheck;

public class InMemoryClaimCheckStoreTest
{
    [Fact]
    public async Task PutThenGet_RoundTripsTheBodyVerbatim()
    {
        var store = new InMemoryClaimCheckStore();

        var reference = await store.PutAsync("hello world", new ClaimCheckPutContext("orders:create"));
        var body = await store.GetAsync(reference);

        Assert.Equal("hello world", body);
    }

    [Fact]
    public async Task Reference_UsesTheMemoryScheme_AndCarriesTheTopic()
    {
        var store = new InMemoryClaimCheckStore();

        var reference = await store.PutAsync("body", new ClaimCheckPutContext("orders:create"));

        Assert.StartsWith("memory://", reference);
        Assert.Contains("orders", reference);
    }

    [Fact]
    public async Task Get_UnknownReference_ReturnsNull()
    {
        var store = new InMemoryClaimCheckStore();

        var body = await store.GetAsync("memory://orders:create/does-not-exist");

        Assert.Null(body);
    }

    [Fact]
    public async Task Get_ForeignScheme_ThrowsMismatch()
    {
        var store = new InMemoryClaimCheckStore();

        var ex = await Assert.ThrowsAsync<ClaimCheckStoreMismatchException>(() =>
            store.GetAsync("s3://someone-elses-bucket/key"));

        Assert.Equal("s3://someone-elses-bucket/key", ex.Reference);
    }

    [Fact]
    public async Task AfterTtlExpiry_GetReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryClaimCheckStore(timeToLive: TimeSpan.FromMinutes(10), now: () => now);
        var reference = await store.PutAsync("body", new ClaimCheckPutContext("orders:create"));

        Assert.Equal("body", await store.GetAsync(reference));

        now = now.AddMinutes(11);
        Assert.Null(await store.GetAsync(reference));
    }

    [Fact]
    public async Task DifferentPuts_AreIndependent()
    {
        var store = new InMemoryClaimCheckStore();

        var referenceA = await store.PutAsync("body-a", new ClaimCheckPutContext("topic-a"));
        var referenceB = await store.PutAsync("body-b", new ClaimCheckPutContext("topic-b"));

        Assert.Equal("body-a", await store.GetAsync(referenceA));
        Assert.Equal("body-b", await store.GetAsync(referenceB));
    }

    [Fact]
    public async Task Put_ConcurrentCallers_AllSucceed_AndEachRoundTripsItsOwnBody()
    {
        // PutAsync always mints a fresh, unique key (a GUID), so unlike the idempotency/outbox
        // stores there is no shared key for callers to race over and no "one winner" to assert.
        // The real concurrency hazard here is the shared Dictionary itself: concurrent writes to it
        // without the store's lock can corrupt its internal state or lose entries. This drives many
        // callers at the underlying dictionary at once and checks every one of them got back a
        // distinct, working reference - the lock around _entries is what makes that safe.
        var store = new InMemoryClaimCheckStore();
        const int callers = 50;

        var references = await Task.WhenAll(Enumerable.Range(0, callers)
            .Select(i => Task.Run(() => store.PutAsync($"body-{i}", new ClaimCheckPutContext("orders:create")))));

        Assert.Equal(callers, references.Distinct().Count());

        var bodies = await Task.WhenAll(references.Select(r => store.GetAsync(r)));
        Assert.All(bodies, Assert.NotNull);
        Assert.Equal(callers, bodies.Distinct().Count());
    }

    [Fact]
    public async Task Put_AlreadyCancelledToken_Throws()
    {
        var store = new InMemoryClaimCheckStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.PutAsync("body", new ClaimCheckPutContext("orders:create"), cts.Token));
    }

    // WP-7 #18: a Get on an expired entry must actually remove it from the backing dictionary, not
    // merely report null while leaving it in place forever (the old "expired lazily" wording implied a
    // release that never happened).
    [Fact]
    public async Task Get_OnExpiredEntry_RemovesItFromTheStore()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryClaimCheckStore(timeToLive: TimeSpan.FromMinutes(10), now: () => now);
        var reference = await store.PutAsync("body", new ClaimCheckPutContext("orders:create"));
        Assert.Equal(1, store.EntryCount);

        now = now.AddMinutes(11);
        var body = await store.GetAsync(reference);

        Assert.Null(body);
        Assert.Equal(0, store.EntryCount);
    }

    // WP-7 #18: PutAsync sweeps every expired entry - including ones that are never read back at all
    // (a fan-out sibling nobody consumes, an undelivered message) - so growth is bounded wherever it
    // originates, not just on the read path.
    [Fact]
    public async Task Put_SweepsExpiredEntries_EvenOnesNeverRead()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryClaimCheckStore(timeToLive: TimeSpan.FromMinutes(10), now: () => now);

        await store.PutAsync("never-read-1", new ClaimCheckPutContext("orders:create"));
        await store.PutAsync("never-read-2", new ClaimCheckPutContext("orders:create"));
        Assert.Equal(2, store.EntryCount);

        // Past the entries' TTL and past the sweep's own minimum interval.
        now = now.AddMinutes(11) + InMemoryClaimCheckStore.SweepInterval;
        await store.PutAsync("triggers-the-sweep", new ClaimCheckPutContext("orders:create"));

        // The two never-read, now-expired entries are gone; only the fresh one that triggered the
        // sweep (and isn't itself expired) remains.
        Assert.Equal(1, store.EntryCount);
    }

    // The sweep is time-gated (at most once per SweepInterval) - deliberately, so a busy producer does
    // not pay a full-dictionary scan on every single put.
    [Fact]
    public async Task Put_DoesNotSweep_MoreThanOncePerSweepInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryClaimCheckStore(timeToLive: TimeSpan.FromSeconds(10), now: () => now);

        // The first put's sweep runs immediately (nothing swept yet at construction-relative time
        // zero), which starts the SweepInterval clock.
        await store.PutAsync("never-read", new ClaimCheckPutContext("orders:create"));

        // Past the entry's TTL (10s), but well within the sweep's own SweepInterval (1 minute).
        now = now.AddSeconds(20);
        await store.PutAsync("too-soon-for-a-sweep", new ClaimCheckPutContext("orders:create"));

        // Both entries still present: the second put's sweep was skipped as too soon, even though the
        // first entry is already expired.
        Assert.Equal(2, store.EntryCount);
    }
}
