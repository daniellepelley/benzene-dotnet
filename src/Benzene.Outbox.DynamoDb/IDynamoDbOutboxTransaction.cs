using Amazon.DynamoDBv2.Model;

namespace Benzene.Outbox.DynamoDb;

/// <summary>
/// The outbox-aware unit of work a handler commits through in
/// <see cref="OutboxWriteMode.Transactional"/> mode (see <c>Benzene.Outbox/CLAUDE.md</c>'s
/// "Capability boundary" section and this package's <c>CLAUDE.md</c>): one DynamoDB
/// <c>TransactWriteItems</c> call containing the caller's own application writes plus one <c>Put</c>
/// per envelope staged via <c>UseOutbox(o =&gt; o.WriteMode = OutboxWriteMode.Transactional)</c> - all
/// or nothing.
/// </summary>
public interface IDynamoDbOutboxTransaction
{
    /// <summary>
    /// Drains every envelope staged on the current scope's <c>IOutboxStage</c> and commits them,
    /// together with <paramref name="applicationItems"/>, as a single atomic
    /// <c>TransactWriteItemsAsync</c> call.
    /// </summary>
    /// <param name="applicationItems">
    /// The caller's own application-state writes (e.g. an order's <c>Put</c>) to include in the same
    /// transaction. May be empty if the handler has nothing of its own to write and is only using
    /// this to commit staged envelopes atomically with... itself (which, absent any application
    /// items, degrades to "just persist the envelopes" - still fine, but usually you have at least
    /// one application item, or you'd use <see cref="OutboxWriteMode.Immediate"/> instead).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the combined item count (<paramref name="applicationItems"/> + staged envelopes)
    /// exceeds DynamoDB's 100-item transaction limit, or when there is nothing to commit at all
    /// (no staged envelopes and no application items).
    /// </exception>
    Task CommitAsync(IReadOnlyList<TransactWriteItem> applicationItems, CancellationToken cancellationToken = default);
}
