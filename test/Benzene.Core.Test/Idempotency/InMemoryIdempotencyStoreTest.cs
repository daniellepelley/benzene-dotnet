using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Idempotency;
using Xunit;

namespace Benzene.Test.Idempotency;

public class InMemoryIdempotencyStoreTest
{
    [Fact]
    public async Task TryClaim_FirstTime_Wins()
    {
        var store = new InMemoryIdempotencyStore();

        var claim = await store.TryClaimAsync("key-1");

        Assert.True(claim.Claimed);
        Assert.Null(claim.ExistingRecord);
        Assert.NotNull(claim.ClaimToken);
    }

    [Fact]
    public async Task TryClaim_SecondTimeWhileInProgress_IsRefusedWithInProgressRecord()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryClaimAsync("key-1");

        var claim = await store.TryClaimAsync("key-1");

        Assert.False(claim.Claimed);
        Assert.NotNull(claim.ExistingRecord);
        Assert.Equal(IdempotencyStatus.InProgress, claim.ExistingRecord!.Status);
    }

    [Fact]
    public async Task TryClaim_AfterComplete_IsRefusedWithCompletedOutcome()
    {
        var store = new InMemoryIdempotencyStore();
        var firstClaim = await store.TryClaimAsync("key-1");
        await store.CompleteAsync("key-1", firstClaim.ClaimToken!, wasSuccessful: true);

        var claim = await store.TryClaimAsync("key-1");

        Assert.False(claim.Claimed);
        Assert.Equal(IdempotencyStatus.Completed, claim.ExistingRecord!.Status);
        Assert.True(claim.ExistingRecord.WasSuccessful);
    }

    [Fact]
    public async Task Complete_WithMatchingToken_Succeeds()
    {
        var store = new InMemoryIdempotencyStore();
        var claim = await store.TryClaimAsync("key-1");

        var settled = await store.CompleteAsync("key-1", claim.ClaimToken!, wasSuccessful: true);

        Assert.True(settled);
    }

    [Fact]
    public async Task Complete_WithStaleToken_IsRefused_AndDoesNotClobberTheLiveClaim()
    {
        var store = new InMemoryIdempotencyStore();
        var claim = await store.TryClaimAsync("key-1");

        var settled = await store.CompleteAsync("key-1", "not-the-real-token", wasSuccessful: true);

        Assert.False(settled);
        // The live claim (still in progress under its real token) was not touched.
        var reclaim = await store.TryClaimAsync("key-1");
        Assert.False(reclaim.Claimed);
        Assert.Equal(IdempotencyStatus.InProgress, reclaim.ExistingRecord!.Status);
    }

    [Fact]
    public async Task Release_AllowsReclaim()
    {
        var store = new InMemoryIdempotencyStore();
        var firstClaim = await store.TryClaimAsync("key-1");

        await store.ReleaseAsync("key-1", firstClaim.ClaimToken!);
        var claim = await store.TryClaimAsync("key-1");

        Assert.True(claim.Claimed);
    }

    [Fact]
    public async Task Release_WithStaleToken_IsRefused_AndDoesNotRemoveTheLiveClaim()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryClaimAsync("key-1");

        var released = await store.ReleaseAsync("key-1", "not-the-real-token");

        Assert.False(released);
        var reclaim = await store.TryClaimAsync("key-1");
        Assert.False(reclaim.Claimed);
    }

    /// <summary>
    /// Regression test for the round-5 fenced-settle scenario: a stale/slow holder's claim naturally
    /// lapses (TTL) and a second worker reclaims the key before the first worker's late
    /// Complete/Release arrives. The stale writes must be rejected (return false) and must not clobber
    /// the new holder's own claim/outcome.
    /// </summary>
    [Fact]
    public async Task StaleHolder_LateCompleteAndRelease_AfterLegitimateReclaim_AreRejected_NotClobbered()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryIdempotencyStore(timeToLive: TimeSpan.FromMinutes(1), now: () => now);

        // Worker A claims the key.
        var claimA = await store.TryClaimAsync("key-1");
        Assert.True(claimA.Claimed);

        // Worker A stalls past the TTL - its claim legitimately lapses.
        now = now.AddMinutes(2);

        // Worker B reclaims the same key (a fresh claim, a new token).
        var claimB = await store.TryClaimAsync("key-1");
        Assert.True(claimB.Claimed);
        Assert.NotEqual(claimA.ClaimToken, claimB.ClaimToken);

        // Worker A, unaware it lost the claim, now tries to settle with its stale token.
        var staleComplete = await store.CompleteAsync("key-1", claimA.ClaimToken!, wasSuccessful: true);
        Assert.False(staleComplete);

        var staleRelease = await store.ReleaseAsync("key-1", claimA.ClaimToken!);
        Assert.False(staleRelease);

        // Worker B's own claim is untouched by A's stale writes - it can still settle successfully.
        var bSettled = await store.CompleteAsync("key-1", claimB.ClaimToken!, wasSuccessful: true);
        Assert.True(bSettled);

        var final = await store.TryClaimAsync("key-1");
        Assert.False(final.Claimed);
        Assert.Equal(IdempotencyStatus.Completed, final.ExistingRecord!.Status);
        Assert.True(final.ExistingRecord.WasSuccessful);
    }

    /// <summary>
    /// Regression test for #51: fencing is token match ALONE, matching every sibling implementation
    /// (<c>DynamoDbIdempotencyStore</c>, both Outbox stores). Previously <c>IsLiveClaim</c> also
    /// required <c>entry.ExpiresAt > now</c>, so a holder that merely outraced its own TTL - with no
    /// competing claimant, nobody having reclaimed the key - got a misleading "reclaimed by another
    /// worker" false return and its outcome was discarded.
    /// </summary>
    [Fact]
    public async Task Complete_WithOriginalToken_AfterOwnTtlExpiry_WithNoCompetingClaimant_Succeeds()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryIdempotencyStore(timeToLive: TimeSpan.FromMinutes(1), now: () => now);

        var claim = await store.TryClaimAsync("key-1");
        Assert.True(claim.Claimed);

        // The claim's own TTL lapses, but nobody else has reclaimed the key - claim.ClaimToken is
        // still the only, still-InProgress token on record.
        now = now.AddMinutes(2);

        var settled = await store.CompleteAsync("key-1", claim.ClaimToken!, wasSuccessful: true);

        Assert.True(settled);
        var reclaim = await store.TryClaimAsync("key-1");
        Assert.False(reclaim.Claimed);
        Assert.Equal(IdempotencyStatus.Completed, reclaim.ExistingRecord!.Status);
        Assert.True(reclaim.ExistingRecord.WasSuccessful);
    }

    [Fact]
    public async Task TryClaim_AfterTtlExpiry_AllowsReclaim()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryIdempotencyStore(timeToLive: TimeSpan.FromMinutes(10), now: () => now);
        await store.TryClaimAsync("key-1");

        // A duplicate within the TTL is still refused...
        Assert.False((await store.TryClaimAsync("key-1")).Claimed);

        // ...but once the record has expired, the key can be claimed again.
        now = now.AddMinutes(11);
        Assert.True((await store.TryClaimAsync("key-1")).Claimed);
    }

    [Fact]
    public async Task DifferentKeys_AreIndependent()
    {
        var store = new InMemoryIdempotencyStore();

        Assert.True((await store.TryClaimAsync("key-a")).Claimed);
        Assert.True((await store.TryClaimAsync("key-b")).Claimed);
    }

    [Fact]
    public async Task TryClaim_ConcurrentCallersOnTheSameKey_ExactlyOneWins()
    {
        var store = new InMemoryIdempotencyStore();
        const int callers = 50;

        var claims = await Task.WhenAll(Enumerable.Range(0, callers)
            .Select(_ => Task.Run(() => store.TryClaimAsync("key-1"))));

        // The lock around the read-then-write in TryClaimAsync is what makes this deterministic - if
        // that scope narrowed to just the write, two callers could both observe no live entry and
        // both win.
        Assert.Equal(1, claims.Count(c => c.Claimed));
        Assert.Equal(callers - 1, claims.Count(c => !c.Claimed));
    }

    [Fact]
    public async Task TryClaim_AlreadyCancelledToken_Throws()
    {
        var store = new InMemoryIdempotencyStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.TryClaimAsync("key-1", cts.Token));
    }

    [Fact]
    public async Task CompleteAndRelease_AlreadyCancelledToken_Throw()
    {
        var store = new InMemoryIdempotencyStore();
        var claim = await store.TryClaimAsync("key-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.CompleteAsync("key-1", claim.ClaimToken!, wasSuccessful: true, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReleaseAsync("key-1", claim.ClaimToken!, cts.Token));
    }
}
