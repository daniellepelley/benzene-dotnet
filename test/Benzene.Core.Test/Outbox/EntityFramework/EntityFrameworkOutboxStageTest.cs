using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Outbox;
using Benzene.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Benzene.Test.Outbox.EntityFramework;

public class EntityFrameworkOutboxStageTest
{
    private static OutboxEnvelope NewEnvelope(string id = "env-1", string topic = "test:topic")
    {
        return new OutboxEnvelope(
            id,
            topic,
            "\"payload\"",
            typeof(string).AssemblyQualifiedName!,
            new Dictionary<string, string> { ["traceparent"] = "abc" },
            DateTimeOffset.UtcNow);
    }

    private static DbContextOptions<TestOutboxDbContext> NewDatabaseOptions(string? name = null)
    {
        return new DbContextOptionsBuilder<TestOutboxDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options;
    }

    [Fact]
    public async Task StageAsync_AddsToChangeTracker_ButDoesNotPersist()
    {
        var options = NewDatabaseOptions();
        await using var dbContext = new TestOutboxDbContext(options);
        var stage = new EntityFrameworkOutboxStage<TestOutboxDbContext>(dbContext);

        await stage.StageAsync(NewEnvelope());

        // Tracked...
        var entry = Assert.Single(dbContext.ChangeTracker.Entries<OutboxRecord>());
        Assert.Equal(EntityState.Added, entry.State);

        // ...but not persisted - a query against the underlying store (not the local change tracker
        // cache) sees nothing, because StageAsync deliberately never calls SaveChanges.
        var persistedCount = await dbContext.Set<OutboxRecord>().AsNoTracking().CountAsync();
        Assert.Equal(0, persistedCount);
    }

    [Fact]
    public async Task HandlerOwnSaveChanges_CommitsTheStagedEnvelope_TogetherWithApplicationState()
    {
        var options = NewDatabaseOptions();
        await using var dbContext = new TestOutboxDbContext(options);
        var stage = new EntityFrameworkOutboxStage<TestOutboxDbContext>(dbContext);

        // The handler's own state write, in the same scoped DbContext.
        dbContext.Orders.Add(new TestOrder { Id = 1, Name = "widget" });
        await stage.StageAsync(NewEnvelope());

        // This is the "handler's own SaveChangesAsync is the commit" story - one call persists both.
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, await dbContext.Set<TestOrder>().AsNoTracking().CountAsync());
        var record = Assert.Single(await dbContext.Set<OutboxRecord>().AsNoTracking().ToListAsync());
        Assert.Equal("env-1", record.Id);
        Assert.Equal(OutboxStatus.Pending, record.Status);
    }

    [Fact]
    public async Task ScopeDisposedWithoutSaveChanges_DiscardsTheStagedEnvelope_NoLeak()
    {
        var options = NewDatabaseOptions("discard-test-db");

        await using (var dbContext = new TestOutboxDbContext(options))
        {
            var stage = new EntityFrameworkOutboxStage<TestOutboxDbContext>(dbContext);
            await stage.StageAsync(NewEnvelope());
            // The handler throws (or otherwise never calls SaveChangesAsync) - the scope disposes
            // with the row only ever tracked, never committed.
        }

        // A fresh context against the same underlying database sees nothing - consistent by
        // construction, since no application state was written either.
        await using var freshContext = new TestOutboxDbContext(options);
        var count = await freshContext.Set<OutboxRecord>().AsNoTracking().CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task StageAsync_MultipleEnvelopes_AllStagedAndCommittedTogether()
    {
        var options = NewDatabaseOptions();
        await using var dbContext = new TestOutboxDbContext(options);
        var stage = new EntityFrameworkOutboxStage<TestOutboxDbContext>(dbContext);

        await stage.StageAsync(NewEnvelope("env-1", "payments:capture"));
        await stage.StageAsync(NewEnvelope("env-2", "order:placed"));

        Assert.Equal(0, await dbContext.Set<OutboxRecord>().AsNoTracking().CountAsync());

        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.Set<OutboxRecord>().AsNoTracking().CountAsync());
    }
}
