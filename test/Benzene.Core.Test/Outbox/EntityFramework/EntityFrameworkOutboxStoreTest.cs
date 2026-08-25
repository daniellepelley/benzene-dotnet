using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Outbox;
using Benzene.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Benzene.Test.Outbox.EntityFramework;

// The test project's only registered EF Core provider is InMemory, which does not support
// ExecuteUpdateAsync/ExecuteDeleteAsync (see DatabaseHealthCheckTest's note on the same provider
// limitation). That means every claim exercised below goes through
// EntityFrameworkOutboxStore<TDbContext>'s optimistic-concurrency fallback path, not the fast
// single-statement ExecuteUpdateAsync path a real relational provider (SQL Server/PostgreSQL/SQLite)
// would take - both paths share the same claim contract (atomic, exclusive), which is what these
// tests assert.
public class EntityFrameworkOutboxStoreTest
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

    private static EntityFrameworkOutboxStore<TestOutboxDbContext> NewStore(string databaseName, Func<DateTimeOffset>? now = null)
    {
        var options = new DbContextOptionsBuilder<TestOutboxDbContext>().UseInMemoryDatabase(databaseName).Options;
        var factory = new TestOutboxDbContextFactory(options);
        return new EntityFrameworkOutboxStore<TestOutboxDbContext>(factory, now);
    }

    [Fact]
    public async Task AddAsync_ThenClaimDue_ReturnsTheFreshlyAddedEnvelope()
    {
        var store = NewStore(nameof(AddAsync_ThenClaimDue_ReturnsTheFreshlyAddedEnvelope));
        await store.AddAsync([NewEnvelope()]);

        var due = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        var envelope = Assert.Single(due);
        Assert.Equal("env-1", envelope.Id);
        Assert.Equal(OutboxStatus.Pending, envelope.Status);
    }

    [Fact]
    public async Task ClaimDue_WhileLeaseIsLive_DoesNotReturnTheSameEnvelopeTwice()
    {
        var store = NewStore(nameof(ClaimDue_WhileLeaseIsLive_DoesNotReturnTheSameEnvelopeTwice));
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
        var store = NewStore(nameof(ClaimDue_AfterLeaseLapses_BecomesClaimableAgain), () => now);
        await store.AddAsync([NewEnvelope()]);

        await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        now = now.AddMinutes(2);
        var reclaimed = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        Assert.Single(reclaimed);
    }

    [Fact]
    public async Task ClaimDue_RespectsBatchSize()
    {
        var store = NewStore(nameof(ClaimDue_RespectsBatchSize));
        await store.AddAsync([NewEnvelope("env-1"), NewEnvelope("env-2"), NewEnvelope("env-3")]);

        var due = await store.ClaimDueAsync(2, TimeSpan.FromMinutes(1));

        Assert.Equal(2, due.Count);
    }

    [Fact]
    public async Task ClaimAsync_UnknownId_ReturnsNull()
    {
        var store = NewStore(nameof(ClaimAsync_UnknownId_ReturnsNull));

        Assert.Null(await store.ClaimAsync("does-not-exist", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ClaimAsync_AlreadyLeased_RefusesTheClaim()
    {
        var store = NewStore(nameof(ClaimAsync_AlreadyLeased_RefusesTheClaim));
        await store.AddAsync([NewEnvelope()]);
        await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1));

        Assert.Null(await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Claim_IsExclusiveAcrossTwoSeparateStoreInstances_SharingOneDatabase()
    {
        var databaseName = nameof(Claim_IsExclusiveAcrossTwoSeparateStoreInstances_SharingOneDatabase);
        var writer = NewStore(databaseName);
        await writer.AddAsync([NewEnvelope()]);

        // Two independent EntityFrameworkOutboxStore instances - each with its own
        // IDbContextFactory/DbContext, exactly as two separate dispatcher processes (or a
        // stream-triggered dispatch racing a sweep in the same process) would have - both pointed at
        // the same underlying database.
        var storeA = NewStore(databaseName);
        var storeB = NewStore(databaseName);

        var claimedByA = await storeA.ClaimAsync("env-1", TimeSpan.FromMinutes(1));
        var claimedByB = await storeB.ClaimAsync("env-1", TimeSpan.FromMinutes(1));

        Assert.NotNull(claimedByA);
        Assert.Null(claimedByB);
    }

    [Fact]
    public async Task ClaimDue_IsExclusiveAcrossTwoSeparateStoreInstances_SharingOneDatabase()
    {
        var databaseName = nameof(ClaimDue_IsExclusiveAcrossTwoSeparateStoreInstances_SharingOneDatabase);
        var writer = NewStore(databaseName);
        await writer.AddAsync([NewEnvelope("env-1"), NewEnvelope("env-2"), NewEnvelope("env-3")]);

        var storeA = NewStore(databaseName);
        var storeB = NewStore(databaseName);

        var claimedByA = await storeA.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        var claimedByB = await storeB.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        Assert.Equal(3, claimedByA.Count);
        Assert.Empty(claimedByB);
    }

    [Fact]
    public async Task Reschedule_MakesEnvelopeDueAfterTheDelay_AndRecordsAttemptAndError()
    {
        var now = DateTimeOffset.UtcNow;
        var store = NewStore(nameof(Reschedule_MakesEnvelopeDueAfterTheDelay_AndRecordsAttemptAndError), () => now);
        await store.AddAsync([NewEnvelope()]);
        var claimed = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        var rescheduled = await store.RescheduleAsync("env-1", 1, TimeSpan.FromMinutes(5), "transient failure", claimed.LeaseToken!);
        Assert.True(rescheduled);

        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        now = now.AddMinutes(6);
        var due = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        var envelope = Assert.Single(due);
        Assert.Equal(1, envelope.AttemptCount);
        Assert.Equal("transient failure", envelope.LastError);
        Assert.Equal(OutboxStatus.Pending, envelope.Status);
    }

    [Fact]
    public async Task Reschedule_WithStaleLeaseToken_IsRefused()
    {
        var store = NewStore(nameof(Reschedule_WithStaleLeaseToken_IsRefused));
        await store.AddAsync([NewEnvelope()]);
        await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        var rescheduled = await store.RescheduleAsync("env-1", 1, TimeSpan.FromMinutes(5), "boom", "not-the-real-token");

        Assert.False(rescheduled);
    }

    [Fact]
    public async Task Park_MarksEnvelopeParked_AndItIsNeverClaimedAgain()
    {
        var store = NewStore(nameof(Park_MarksEnvelopeParked_AndItIsNeverClaimedAgain));
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
        var store = NewStore(nameof(Park_WithStaleLeaseToken_IsRefused));
        await store.AddAsync([NewEnvelope()]);
        await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));

        var parked = await store.ParkAsync("env-1", "boom", "not-the-real-token");

        Assert.False(parked);
    }

    [Fact]
    public async Task MarkDispatched_RemovesEnvelopeFromDueClaims()
    {
        var store = NewStore(nameof(MarkDispatched_RemovesEnvelopeFromDueClaims));
        await store.AddAsync([NewEnvelope()]);
        var claimed = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));

        var dispatched = await store.MarkDispatchedAsync("env-1", claimed.LeaseToken!);

        Assert.True(dispatched);
        Assert.Empty(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task MarkDispatched_WithStaleLeaseToken_IsRefused_AndDoesNotClobberTheNewHolder()
    {
        var databaseName = nameof(MarkDispatched_WithStaleLeaseToken_IsRefused_AndDoesNotClobberTheNewHolder);
        var writer = NewStore(databaseName);
        await writer.AddAsync([NewEnvelope()]);

        var claimA = await writer.ClaimAsync("env-1", TimeSpan.FromSeconds(0));
        Assert.NotNull(claimA);

        // A's lease is already effectively expired (zero-length), so a second store can reclaim it.
        var storeB = NewStore(databaseName);
        var claimB = await storeB.ClaimAsync("env-1", TimeSpan.FromMinutes(5));
        Assert.NotNull(claimB);
        Assert.NotEqual(claimA!.LeaseToken, claimB!.LeaseToken);

        var staleDispatch = await writer.MarkDispatchedAsync("env-1", claimA.LeaseToken!);
        Assert.False(staleDispatch);

        // B's own settle still succeeds - A's stale write did not touch B's lease.
        var bDispatch = await storeB.MarkDispatchedAsync("env-1", claimB.LeaseToken!);
        Assert.True(bDispatch);
    }

    [Fact]
    public async Task DeleteDispatchedBefore_OnlyDeletesDispatchedEnvelopesOlderThanCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        var store = NewStore(nameof(DeleteDispatchedBefore_OnlyDeletesDispatchedEnvelopesOlderThanCutoff), () => now);
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
    public async Task DeleteDispatchedBefore_NeverDeletesParkedEnvelopes()
    {
        var now = DateTimeOffset.UtcNow;
        var store = NewStore(nameof(DeleteDispatchedBefore_NeverDeletesParkedEnvelopes), () => now);
        await store.AddAsync([NewEnvelope()]);
        var claimed = Assert.Single(await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));
        await store.ParkAsync("env-1", "poison", claimed.LeaseToken!);

        var deleted = await store.DeleteDispatchedBeforeAsync(now.AddDays(30));

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task DifferentEnvelopes_AreIndependent()
    {
        var store = NewStore(nameof(DifferentEnvelopes_AreIndependent));
        await store.AddAsync([NewEnvelope("env-a"), NewEnvelope("env-b")]);

        Assert.NotNull(await store.ClaimAsync("env-a", TimeSpan.FromMinutes(1)));
        Assert.NotNull(await store.ClaimAsync("env-b", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task AddAsync_RoundTripsHeaders()
    {
        var store = NewStore(nameof(AddAsync_RoundTripsHeaders));
        var envelope = new OutboxEnvelope(
            "env-1", "test:topic", "\"payload\"", typeof(string).AssemblyQualifiedName!,
            new Dictionary<string, string> { ["traceparent"] = "00-abc", ["idempotency-key"] = "env-1" },
            DateTimeOffset.UtcNow);
        await store.AddAsync([envelope]);

        var claimed = await store.ClaimAsync("env-1", TimeSpan.FromMinutes(1));

        Assert.NotNull(claimed);
        Assert.Equal("00-abc", claimed!.Headers["traceparent"]);
        Assert.Equal("env-1", claimed.Headers["idempotency-key"]);
    }
}
