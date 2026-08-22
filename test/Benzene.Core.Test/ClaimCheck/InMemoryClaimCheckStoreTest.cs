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
}
