using Microsoft.EntityFrameworkCore;

namespace Benzene.Outbox.EntityFramework;

/// <summary>
/// The relational <see cref="IOutboxStore"/>: <see cref="OutboxRecord"/> rows in the application's own
/// <typeparamref name="TDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="IDbContextFactory{TContext}"/> and not a directly injected, scoped
/// <typeparamref name="TDbContext"/>.</b> Unlike <see cref="EntityFrameworkOutboxStage{TDbContext}"/>
/// (which deliberately shares the caller's scoped <c>DbContext</c> instance - that sharing IS the
/// atomicity story), this store has no such requirement, and Phase 1's <c>OutboxDispatcher</c> holds
/// its <see cref="IOutboxStore"/> for the process's entire lifetime as a singleton (constructed once,
/// reused by every <c>RunOnceAsync</c> poll tick and every <c>DispatchOneAsync</c> call, potentially
/// concurrently with each other). A directly injected scoped <c>DbContext</c> captured at that
/// singleton's construction time would either be resolved from a disposed/invalid scope or - worse -
/// silently shared and mutated across concurrent calls, and <c>DbContext</c> is not thread-safe. Using
/// <see cref="IDbContextFactory{TContext}"/> instead - itself a thread-safe, long-lived singleton
/// service designed for exactly this "many short-lived contexts, one long-lived owner" shape - lets
/// this store be registered singleton (same lifetime as <c>InMemoryOutboxStore</c>) while every method
/// creates and disposes its own short-lived <typeparamref name="TDbContext"/>. See
/// <c>Benzene.Outbox.EntityFramework/CLAUDE.md</c> for the resulting registration requirement (both
/// <c>AddDbContext&lt;TDbContext&gt;</c> AND <c>AddDbContextFactory&lt;TDbContext&gt;</c>).
/// </para>
/// <para>
/// <b>Claim atomicity.</b> <see cref="ClaimAsync"/> (and <see cref="ClaimDueAsync"/>, which claims
/// each of its candidates through it) first attempts a single conditioned <c>ExecuteUpdateAsync</c> -
/// an <c>UPDATE ... WHERE</c> statement that only touches a row still unleased/lapsed, executed
/// entirely in the database, so "rows updated" (0 or 1) IS the atomic claim decision; no
/// read-then-write race window. Providers that don't support <c>ExecuteUpdate</c> (the EF Core
/// InMemory provider used in this package's own tests, notably) throw
/// <see cref="InvalidOperationException"/> for it - this store catches exactly that and falls back to
/// optimistic concurrency instead: load the row tracked, check the same claim predicate in memory,
/// set the lease, bump <see cref="OutboxRecord.RowVersion"/>, and <c>SaveChanges</c> - a concurrent
/// claimant's own bumped <see cref="OutboxRecord.RowVersion"/> makes this one's <c>SaveChanges</c>
/// throw <see cref="DbUpdateConcurrencyException"/>, which is retried a bounded number of times before
/// giving up (treated the same as a refused claim - consistent with the inherent at-least-once
/// discipline every <see cref="IOutboxStore"/> implementation follows). Document this fallback loudly
/// wherever a real relational provider is chosen: SQL Server/PostgreSQL/SQLite all support
/// <c>ExecuteUpdate</c>, so the fast, single-statement path is what production deployments actually
/// take.
/// </para>
/// </remarks>
/// <typeparam name="TDbContext">The application's own <see cref="DbContext"/> type, mapped via <see cref="ModelBuilderExtensions.AddOutboxEntities"/>.</typeparam>
public class EntityFrameworkOutboxStore<TDbContext> : IOutboxStore where TDbContext : DbContext
{
    private const int MaxOptimisticConcurrencyAttempts = 5;

    private readonly IDbContextFactory<TDbContext> _dbContextFactory;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Initializes a new instance of the <see cref="EntityFrameworkOutboxStore{TDbContext}"/> class.</summary>
    /// <param name="dbContextFactory">Creates a short-lived <typeparamref name="TDbContext"/> per operation - see the class remarks.</param>
    /// <param name="now">A clock, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public EntityFrameworkOutboxStore(IDbContextFactory<TDbContext> dbContextFactory, Func<DateTimeOffset>? now = null)
    {
        _dbContextFactory = dbContextFactory;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task AddAsync(IEnumerable<OutboxEnvelope> envelopes, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.Set<OutboxRecord>().AddRange(envelopes.Select(OutboxRecord.FromEnvelope));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxEnvelope>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = _now();

        List<string> candidateIds;
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            candidateIds = await dbContext.Set<OutboxRecord>()
                .AsNoTracking()
                .Where(r => r.Status == OutboxStatus.Pending
                    && (r.NextAttemptAtUtc == null || r.NextAttemptAtUtc <= now)
                    && (r.LeaseUntil == null || r.LeaseUntil <= now))
                .OrderBy(r => r.CreatedAtUtc)
                .Take(batchSize)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
        }

        // Claimed one at a time through ClaimAsync (rather than one bulk conditioned update) so every
        // row still gets its own atomic, conditioned claim - the same discipline
        // Benzene.Outbox.DynamoDb's per-item conditional UpdateItem uses in its own sweep.
        var claimed = new List<OutboxEnvelope>(candidateIds.Count);
        foreach (var id in candidateIds)
        {
            var envelope = await ClaimAsync(id, lease, cancellationToken);
            if (envelope != null)
            {
                claimed.Add(envelope);
            }
        }

        return claimed;
    }

    /// <inheritdoc />
    public async Task<OutboxEnvelope?> ClaimAsync(string id, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = _now();
        var newLeaseUntil = now + lease;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        int affected;
        try
        {
            affected = await dbContext.Set<OutboxRecord>()
                .Where(r => r.Id == id
                    && r.Status == OutboxStatus.Pending
                    && (r.NextAttemptAtUtc == null || r.NextAttemptAtUtc <= now)
                    && (r.LeaseUntil == null || r.LeaseUntil <= now))
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.LeaseUntil, newLeaseUntil), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The provider doesn't support ExecuteUpdate (e.g. EF Core InMemory) - fall back to
            // optimistic concurrency. See the class remarks.
            return await ClaimViaOptimisticConcurrencyAsync(dbContext, id, now, newLeaseUntil, cancellationToken);
        }

        if (affected == 0)
        {
            return null;
        }

        var claimedRecord = await dbContext.Set<OutboxRecord>().AsNoTracking().SingleAsync(r => r.Id == id, cancellationToken);
        return claimedRecord.ToEnvelope();
    }

    private static async Task<OutboxEnvelope?> ClaimViaOptimisticConcurrencyAsync(
        TDbContext dbContext, string id, DateTimeOffset now, DateTimeOffset newLeaseUntil, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxOptimisticConcurrencyAttempts; attempt++)
        {
            var record = await dbContext.Set<OutboxRecord>().AsTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (record == null || !record.IsDue(now))
            {
                return null;
            }

            record.LeaseUntil = newLeaseUntil;
            record.Touch();

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return record.ToEnvelope();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else claimed/modified the row between our read and our SaveChanges - detach
                // so the next attempt's read isn't blocked by an already-tracked stale instance, and
                // retry (the retry's own read will see the loser's own claim and correctly refuse).
                dbContext.Entry(record).State = EntityState.Detached;
            }
        }

        // Exhausted retries under contention - treat as a refused claim, consistent with every other
        // "someone else has it" outcome this method can return.
        return null;
    }

    /// <inheritdoc />
    public async Task MarkDispatchedAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await dbContext.Set<OutboxRecord>().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (record == null)
        {
            return;
        }

        record.Status = OutboxStatus.Dispatched;
        record.LeaseUntil = null;
        record.DispatchedAtUtc = _now();
        record.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RescheduleAsync(string id, int attemptCount, TimeSpan delay, string error, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await dbContext.Set<OutboxRecord>().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (record == null)
        {
            return;
        }

        record.Status = OutboxStatus.Pending;
        record.AttemptCount = attemptCount;
        record.NextAttemptAtUtc = _now() + delay;
        record.LastError = error;
        record.LeaseUntil = null;
        record.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ParkAsync(string id, string error, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await dbContext.Set<OutboxRecord>().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (record == null)
        {
            return;
        }

        record.Status = OutboxStatus.Parked;
        record.LastError = error;
        record.LeaseUntil = null;
        record.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A relational store has no native TTL, so unlike <c>Benzene.Outbox.DynamoDb</c>'s no-op this
    /// performs a real, immediate delete of every <see cref="OutboxStatus.Dispatched"/> row past
    /// retention. <see cref="OutboxStatus.Parked"/> rows are never touched here.
    /// </remarks>
    public async Task<int> DeleteDispatchedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var toDelete = await dbContext.Set<OutboxRecord>()
            .Where(r => r.Status == OutboxStatus.Dispatched && r.DispatchedAtUtc != null && r.DispatchedAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        if (toDelete.Count == 0)
        {
            return 0;
        }

        dbContext.Set<OutboxRecord>().RemoveRange(toDelete);
        await dbContext.SaveChangesAsync(cancellationToken);
        return toDelete.Count;
    }
}
