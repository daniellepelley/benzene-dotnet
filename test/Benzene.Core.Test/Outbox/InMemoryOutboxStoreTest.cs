using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Outbox;
using Xunit;

namespace Benzene.Test.Outbox;

public class InMemoryOutboxStoreTest
{
    private static OutboxEnvelope NewEnvelope(string id = "env-1", string topic = "test:topic", DateTimeOffset? createdAtUtc = null)
    {
        return new OutboxEnvelope(
            id,
            topic,
            "\"payload\"",
            typeof(string).AssemblyQualifiedName!,
            new Dictionary<string, string>(),
            createdAtUtc ?? DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ClaimDue_FreshlyAddedEnvelope_IsClaimable()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope()]);

        var due = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        var envelope = Assert.Single(due);
        Assert.Equal("env-1", envelope.Id);
        Assert.Equal(OutboxStatus.Pending, envelope.Status);
        Assert.NotNull(envelope.LeaseToken);
    }

    [Fact]
    public async Task ClaimDue_WhileLeaseIsLive_DoesNotReturnTheSameEnvelopeTwice()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope()]);

        var first = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        var second = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task ClaimDue_AfterLeaseLapses_BecomesClaimableAgain()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutboxStore(() => now);
        await store.AddAsync([NewEnvelope()]);

        await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        now = now.AddMinutes(2);
        var reclaimed = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        Assert.Single(reclaimed);
    }

    [Fact]
    public async Task ClaimDue_RespectsBatchSize()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope("env-1"), NewEnvelope("env-2"), NewEnvelope("env-3")]);

        var due = await store.ClaimDueAsync(2, TimeSpan.FromMinutes(1));

        Assert.Equal(2, due.Count);
    }

    [Fact]
    public async Task ClaimDue_NotYetDueEnvelope_IsNotClaimed()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutboxStore(() => now);
        await store.AddAsync([NewEnvelope()]);
        var claimed = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));
        await store.RescheduleAsync("env-1", 1, TimeSpan.FromMinutes(5), "boom", claimed.LeaseToken!);

        var due = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        Assert.Empty(due);
    }

    [Fact]
    public async Task Reschedule_MakesEnvelopeDueAfterTheDelay_AndRecordsAttemptAndError()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutboxStore(() => now);
        await store.AddAsync([NewEnvelope()]);
        var claimed = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        var rescheduled = await store.RescheduleAsync("env-1", 1, TimeSpan.FromMinutes(5), "transient failure", claimed.LeaseToken!);
        Assert.True(rescheduled);

        // Not due yet.
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        now = now.AddMinutes(6);
        var due = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        var envelope = Assert.Single(due);
        Assert.Equal(1, envelope.AttemptCount);
        Assert.Equal("transient failure", envelope.LastError);
        Assert.Equal(OutboxStatus.Pending, envelope.Status);
    }

    [Fact]
    public async Task Reschedule_WithStaleLeaseToken_IsRefused_AndDoesNotClobberTheNewHolder()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutboxStore(() => now);
        await store.AddAsync([NewEnvelope()]);
        var claimA = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromSeconds(1)));

        now = now.AddSeconds(2); // A's lease lapses
        var claimB = await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1));
        Assert.NotNull(claimB);
        Assert.NotEqual(claimA.LeaseToken, claimB!.LeaseToken);

        var staleReschedule = await store.RescheduleAsync("env-1", 99, TimeSpan.FromMinutes(5), "stale", claimA.LeaseToken!);

        Assert.False(staleReschedule);
        // B's own claim is untouched: still leased, attempt count unchanged by A's stale write.
        Assert.Null(await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Park_MarksEnvelopeParked_AndItIsNeverClaimedAgain()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope()]);
        var claimed = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        var parked = await store.ParkAsync("env-1", "exhausted retries", claimed.LeaseToken!);

        Assert.True(parked);
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));
        Assert.Null(await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Park_WithStaleLeaseToken_IsRefused()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope()]);
        await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        var parked = await store.ParkAsync("env-1", "boom", "not-the-real-token");

        Assert.False(parked);
    }

    [Fact]
    public async Task MarkDispatched_RemovesEnvelopeFromDueClaims()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope()]);
        var claimed = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        var dispatched = await store.MarkDispatchedAsync("env-1", claimed.LeaseToken!);

        Assert.True(dispatched);
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task MarkDispatched_WithStaleLeaseToken_IsRefused_AndDoesNotClobberTheNewHolder()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutboxStore(() => now);
        await store.AddAsync([NewEnvelope()]);
        var claimA = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromSeconds(1)));

        now = now.AddSeconds(2); // A's lease lapses
        var claimB = await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1));
        Assert.NotNull(claimB);

        var staleDispatch = await store.MarkDispatchedAsync("env-1", claimA.LeaseToken!);
        Assert.False(staleDispatch);

        // B's own settle still works - its lease was never touched by A's stale write.
        var bDispatch = await store.MarkDispatchedAsync("env-1", claimB!.LeaseToken!);
        Assert.True(bDispatch);
    }

    [Fact]
    public async Task MarkDispatched_UnknownId_ReturnsFalse()
    {
        var store = new InMemoryOutboxStore();

        Assert.False(await store.MarkDispatchedAsync("does-not-exist", "any-token"));
    }

    [Fact]
    public async Task DeleteDispatchedBefore_OnlyDeletesDispatchedEnvelopesOlderThanCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutboxStore(() => now);
        await store.AddAsync([NewEnvelope("old-dispatched"), NewEnvelope("recent-dispatched"), NewEnvelope("still-pending")]);
        var claimed = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        await store.MarkDispatchedAsync("old-dispatched", claimed.Single(e => e.Id == "old-dispatched").LeaseToken!);

        now = now.AddDays(1);
        await store.MarkDispatchedAsync("recent-dispatched", claimed.Single(e => e.Id == "recent-dispatched").LeaseToken!);

        var deleted = await store.DeleteDispatchedBeforeAsync(now.AddHours(-1));

        Assert.Equal(1, deleted);
        // The still-pending envelope survives the sweep untouched.
        Assert.NotNull(await store.ClaimAsync("still-pending", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ClaimAsync_UnknownId_ReturnsNull()
    {
        var store = new InMemoryOutboxStore();

        Assert.Null(await store.ClaimAsync("does-not-exist", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ClaimAsync_AlreadyLeased_RefusesTheClaim()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope()]);
        await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1));

        Assert.Null(await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ClaimAsync_ConcurrentCallersOnTheSameId_ExactlyOneWins()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope()]);
        const int callers = 50;

        var claims = await Task.WhenAll(Enumerable.Range(0, callers)
            .Select(_ => Task.Run(() => store.ClaimAsync("env-1", TimeSpan.FromMinutes(1)))));

        // Guards the lock scope around the due-check + lease write in ClaimAsync - if it narrowed to
        // just the write, two racing callers could both observe the envelope as unleased and both win.
        Assert.Single(claims, c => c is not null);
        Assert.Equal(callers - 1, claims.Count(c => c is null));
    }

    [Fact]
    public async Task DifferentEnvelopes_AreIndependent()
    {
        var store = new InMemoryOutboxStore();
        await store.AddAsync([NewEnvelope("env-a"), NewEnvelope("env-b")]);

        Assert.NotNull(await store.ClaimAsync("env-a", TimeSpan.FromMinutes(1)));
        Assert.NotNull(await store.ClaimAsync("env-b", TimeSpan.FromMinutes(1)));
    }
}
