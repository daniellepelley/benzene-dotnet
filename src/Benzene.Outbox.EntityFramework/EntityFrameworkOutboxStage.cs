using Microsoft.EntityFrameworkCore;

namespace Benzene.Outbox.EntityFramework;

/// <summary>
/// The <see cref="OutboxWriteMode.Transactional"/> unit of work for EF Core: stages an
/// <see cref="OutboxRecord"/> straight onto the caller's own, directly injected (scoped)
/// <typeparamref name="TDbContext"/> change tracker - and <b>never calls <c>SaveChanges</c></b>. The
/// application's own <c>SaveChangesAsync</c> (its normal state-write commit) is what actually persists
/// the row, in the same database transaction as everything else that <c>DbContext</c> instance is
/// tracking. That sharing - the same scoped <c>DbContext</c> instance <see cref="OutboxMiddleware"/>'s
/// capture and the handler's own state write both touch - is the entire atomicity story; see
/// <c>Benzene.Outbox.EntityFramework/CLAUDE.md</c>.
/// </summary>
/// <remarks>
/// Unlike <see cref="EntityFrameworkOutboxStore{TDbContext}"/> (which deliberately does NOT take a
/// directly injected <typeparamref name="TDbContext"/> - see its remarks), this type must, and must be
/// registered scoped: it only works when DI resolves the exact same <typeparamref name="TDbContext"/>
/// instance for this type as for the handler that will eventually call <c>SaveChangesAsync</c>.
/// </remarks>
/// <typeparam name="TDbContext">The application's own <see cref="DbContext"/> type, mapped via <see cref="ModelBuilderExtensions.AddOutboxEntities"/>.</typeparam>
public class EntityFrameworkOutboxStage<TDbContext> : IOutboxStage where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    /// <summary>Initializes a new instance of the <see cref="EntityFrameworkOutboxStage{TDbContext}"/> class.</summary>
    /// <param name="dbContext">The scoped <typeparamref name="TDbContext"/> the handler will itself commit through.</param>
    public EntityFrameworkOutboxStage(TDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Adds the row to the change tracker only - deliberately does not call <c>SaveChanges</c>. If the
    /// scope disposes (or the handler throws) before the handler's own <c>SaveChangesAsync</c> runs,
    /// the tracked-but-uncommitted row is simply discarded with the <c>DbContext</c> - consistent by
    /// construction, since no application state was written either.
    /// </remarks>
    public Task StageAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _dbContext.Set<OutboxRecord>().Add(OutboxRecord.FromEnvelope(envelope));
        return Task.CompletedTask;
    }
}
