namespace Benzene.Outbox;

/// <summary>
/// Pluggable persistence for outbox envelopes. Swap the implementation to change where captured
/// envelopes live (in-memory for a single instance/tests, DynamoDB/EF Core for a real deployment)
/// without touching <see cref="OutboxMiddleware"/> or <see cref="IOutboxDispatcher"/>.
/// </summary>
/// <remarks>
/// Every method takes a <see cref="CancellationToken"/> and MUST forward it to any downstream I/O -
/// the same convention <c>Benzene.Idempotency</c>'s <c>IIdempotencyStore</c> follows.
/// </remarks>
public interface IOutboxStore
{
    /// <summary>
    /// Persists newly captured envelopes (<see cref="OutboxStatus.Pending"/>, due immediately).
    /// Called by <see cref="OutboxMiddleware"/> for a single envelope in
    /// <see cref="OutboxWriteMode.Immediate"/> mode, and by the outbox-aware unit of work that drains
    /// a <see cref="BufferedOutboxStage"/> for possibly several at once in
    /// <see cref="OutboxWriteMode.Transactional"/> mode.
    /// </summary>
    /// <param name="envelopes">The envelopes to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task AddAsync(IEnumerable<OutboxEnvelope> envelopes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> envelopes that are
    /// <see cref="OutboxStatus.Pending"/> and due (<c>NextAttemptAtUtc</c> is <see langword="null"/>
    /// or has passed), for <see cref="IOutboxDispatcher.RunOnceAsync"/>'s sweep.
    /// </summary>
    /// <remarks>
    /// Implementations MUST make the claim atomic/conditional per envelope (a lease, e.g. a
    /// conditional <c>UpdateItem</c>/<c>ExecuteUpdate</c> setting <c>leaseUntil</c> only when unleased
    /// or lapsed) - the same hard requirement <c>IIdempotencyStore.TryClaimAsync</c> places on its
    /// implementations - so a stream-triggered dispatcher and a sweeper racing the same envelope
    /// cannot both send it (except across a crash-after-send, the inherent at-least-once window; see
    /// <c>Benzene.Outbox/CLAUDE.md</c>).
    /// </remarks>
    /// <param name="batchSize">The maximum number of envelopes to claim.</param>
    /// <param name="lease">How long the claim is held before another claimer may take the envelope over.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The claimed envelopes, in ascending <c>CreatedAtUtc</c> order where the store can offer that cheaply.</returns>
    Task<IReadOnlyList<OutboxEnvelope>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims one specific envelope by id, for the stream-triggered relay path
    /// (<see cref="IOutboxDispatcher.DispatchOneAsync"/>). Same atomicity requirement as
    /// <see cref="ClaimDueAsync"/>, scoped to a single id.
    /// </summary>
    /// <param name="id">The envelope's id.</param>
    /// <param name="lease">How long the claim is held before another claimer may take the envelope over.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The claimed envelope, or <see langword="null"/> if it doesn't exist, isn't
    /// <see cref="OutboxStatus.Pending"/>, or is currently leased by another claimer (claim refused).
    /// </returns>
    Task<OutboxEnvelope?> ClaimAsync(string id, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an envelope as successfully sent (<see cref="OutboxStatus.Dispatched"/>) and releases its
    /// claim. A no-op if the envelope no longer exists.
    /// </summary>
    /// <param name="id">The envelope's id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task MarkDispatchedAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed dispatch attempt, advances the envelope's retry bookkeeping, and releases its
    /// claim so it becomes due again after <paramref name="delay"/>. A no-op if the envelope no longer
    /// exists.
    /// </summary>
    /// <param name="id">The envelope's id.</param>
    /// <param name="attemptCount">The new attempt count (the caller's responsibility to increment).</param>
    /// <param name="delay">How long until the envelope is due again (the computed backoff).</param>
    /// <param name="error">A description of the failure, stored as the envelope's <c>LastError</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task RescheduleAsync(string id, int attemptCount, TimeSpan delay, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an envelope as terminally failed (<see cref="OutboxStatus.Parked"/>) after it has
    /// exhausted <see cref="OutboxOptions.MaxAttempts"/>. Parked envelopes are never auto-deleted or
    /// auto-retried - they are the operator's evidence. A no-op if the envelope no longer exists.
    /// </summary>
    /// <param name="id">The envelope's id.</param>
    /// <param name="error">A description of the final failure, stored as the envelope's <c>LastError</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ParkAsync(string id, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes <see cref="OutboxStatus.Dispatched"/> envelopes that were dispatched before
    /// <paramref name="cutoff"/> (the retention window). A store with native TTL-based retention
    /// (e.g. DynamoDB) may implement this as a no-op returning <c>0</c>. Never deletes
    /// <see cref="OutboxStatus.Parked"/> envelopes.
    /// </summary>
    /// <param name="cutoff">Envelopes dispatched before this instant are eligible for deletion.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of envelopes deleted (<c>0</c> for a store that defers to native TTL).</returns>
    Task<int> DeleteDispatchedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
