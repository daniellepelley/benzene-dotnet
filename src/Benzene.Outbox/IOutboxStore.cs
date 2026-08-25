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
    // NOTE on claim fencing (P2, work/bug-fix-designs-2026-08.md WP-3): ClaimDueAsync/ClaimAsync stamp
    // every returned envelope's OutboxEnvelope.LeaseToken with a fresh opaque token. MarkDispatchedAsync/
    // RescheduleAsync/ParkAsync REQUIRE that token and return false (nothing written) when it no longer
    // matches the live lease - see each method's remarks for what that closes and what it doesn't.

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
    /// <para>
    /// Implementations MUST make the claim atomic/conditional per envelope (a lease, e.g. a
    /// conditional <c>UpdateItem</c>/<c>ExecuteUpdate</c> setting <c>leaseUntil</c> only when unleased
    /// or lapsed) - the same hard requirement <c>IIdempotencyStore.TryClaimAsync</c> places on its
    /// implementations - so a stream-triggered dispatcher and a sweeper racing the same envelope
    /// cannot both hold it at once, AND every returned envelope's <see cref="OutboxEnvelope.LeaseToken"/>
    /// is stamped with a fresh opaque token that the settle methods (<see cref="MarkDispatchedAsync"/>/
    /// <see cref="RescheduleAsync"/>/<see cref="ParkAsync"/>) require back and check.
    /// </para>
    /// <para>
    /// <b>What claim fencing closes, and what it doesn't.</b> A claimant that runs past its lease (slow,
    /// GC pause, network stall) can have its lease naturally lapse and get taken over by another
    /// claimant while the first is still working. Before fencing, whichever of the two settled last
    /// would silently overwrite the other's outcome - and if both then send, the message is sent twice
    /// with the double-send invisible at the store layer. Fencing closes that: only the settle call
    /// carrying the token of the <em>current</em> lease holder succeeds; the stale claimant's settle
    /// returns <see langword="false"/> and writes nothing, so the state clobber is prevented and the
    /// lost lease becomes visible (the stale claimant can log/alert on the "reclaimed" outcome).
    /// Closing the double-dispatch bug end to end additionally depends on <see cref="IOutboxDispatcher"/>
    /// checking that settle result before treating the message as delivered - the store-level fence
    /// alone stops state corruption, not the caller ignoring its result. What fencing does
    /// <b>not</b> do: it cannot un-send a message a stale claimant already handed to the transport
    /// before its lease lapsed - a send in flight when the lease lapses can still reach the transport,
    /// and a crash between "sent" and "settle recorded" is the same inherent at-least-once window every
    /// outbox has. Fencing narrows the window (state clobber, silent double-send-on-reclaim) without
    /// eliminating the crash-after-send case; only a downstream consumer/idempotency layer can fully
    /// absorb that. See <c>Benzene.Outbox/CLAUDE.md</c>.
    /// </para>
    /// </remarks>
    /// <param name="batchSize">The maximum number of envelopes to claim.</param>
    /// <param name="lease">How long the claim is held before another claimer may take the envelope over.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The claimed envelopes, each with <see cref="OutboxEnvelope.LeaseToken"/> set, in ascending <c>CreatedAtUtc</c> order where the store can offer that cheaply.</returns>
    Task<IReadOnlyList<OutboxEnvelope>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims one specific envelope by id, for the stream-triggered relay path
    /// (<see cref="IOutboxDispatcher.DispatchOneAsync"/>). Same atomicity and fencing contract as
    /// <see cref="ClaimDueAsync"/> (see its remarks), scoped to a single id.
    /// </summary>
    /// <param name="id">The envelope's id.</param>
    /// <param name="lease">How long the claim is held before another claimer may take the envelope over.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The claimed envelope (with <see cref="OutboxEnvelope.LeaseToken"/> set), or <see langword="null"/>
    /// if it doesn't exist, isn't <see cref="OutboxStatus.Pending"/>, or is currently leased by another
    /// claimer (claim refused).
    /// </returns>
    Task<OutboxEnvelope?> ClaimAsync(string id, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an envelope as successfully sent (<see cref="OutboxStatus.Dispatched"/>) and releases its
    /// claim, PROVIDED <paramref name="leaseToken"/> still matches the envelope's current lease.
    /// </summary>
    /// <remarks>
    /// <paramref name="leaseToken"/> MUST be the token from the <see cref="OutboxEnvelope.LeaseToken"/>
    /// this caller was handed by the claim that won the envelope (see <see cref="ClaimDueAsync"/>'s
    /// remarks for the fencing contract this enforces). There is no way to skip it - no default
    /// parameter, no overload - a skippable fence is no fence.
    /// </remarks>
    /// <param name="id">The envelope's id.</param>
    /// <param name="leaseToken">The token from the claim that won this envelope.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="leaseToken"/> matched the current lease and the
    /// envelope was marked dispatched; <see langword="false"/> if the envelope no longer exists or
    /// <paramref name="leaseToken"/> is no longer the current lease holder's token (reclaimed by
    /// another claimer, or already settled) - nothing is written in that case.
    /// </returns>
    Task<bool> MarkDispatchedAsync(string id, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed dispatch attempt, advances the envelope's retry bookkeeping, and releases its
    /// claim so it becomes due again after <paramref name="delay"/>, PROVIDED <paramref name="leaseToken"/>
    /// still matches the envelope's current lease.
    /// </summary>
    /// <remarks>Same fencing contract as <see cref="MarkDispatchedAsync"/> - see its remarks.</remarks>
    /// <param name="id">The envelope's id.</param>
    /// <param name="attemptCount">The new attempt count (the caller's responsibility to increment).</param>
    /// <param name="delay">How long until the envelope is due again (the computed backoff).</param>
    /// <param name="error">A description of the failure, stored as the envelope's <c>LastError</c>.</param>
    /// <param name="leaseToken">The token from the claim that won this envelope.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="leaseToken"/> matched and the reschedule was written;
    /// <see langword="false"/> if the envelope no longer exists or the token is stale - nothing is
    /// written in that case.
    /// </returns>
    Task<bool> RescheduleAsync(string id, int attemptCount, TimeSpan delay, string error, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an envelope as terminally failed (<see cref="OutboxStatus.Parked"/>) after it has
    /// exhausted <see cref="OutboxOptions.MaxAttempts"/>, PROVIDED <paramref name="leaseToken"/> still
    /// matches the envelope's current lease. Parked envelopes are never auto-deleted or auto-retried -
    /// they are the operator's evidence.
    /// </summary>
    /// <remarks>Same fencing contract as <see cref="MarkDispatchedAsync"/> - see its remarks.</remarks>
    /// <param name="id">The envelope's id.</param>
    /// <param name="error">A description of the final failure, stored as the envelope's <c>LastError</c>.</param>
    /// <param name="leaseToken">The token from the claim that won this envelope.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="leaseToken"/> matched and the park was written;
    /// <see langword="false"/> if the envelope no longer exists or the token is stale - nothing is
    /// written in that case.
    /// </returns>
    Task<bool> ParkAsync(string id, string error, string leaseToken, CancellationToken cancellationToken = default);

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
