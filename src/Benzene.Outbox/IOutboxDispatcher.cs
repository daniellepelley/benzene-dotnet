namespace Benzene.Outbox;

/// <summary>
/// The host-agnostic dispatch engine: claims captured envelopes and relays them through their
/// route's outbound pipeline. Reused, unchanged, by every relay host -
/// <see cref="OutboxDispatcherWorker"/>'s poll loop, a DynamoDB-Streams-triggered Lambda handler, and
/// a scheduled-sweep Lambda handler (Phase 2/3) all just call into this same engine.
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>
    /// Claims a due batch (<see cref="IOutboxStore.ClaimDueAsync"/>) and dispatches each envelope,
    /// then deletes retention-expired dispatched envelopes
    /// (<see cref="IOutboxStore.DeleteDispatchedBeforeAsync"/>).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The dispatched/rescheduled/parked/deleted counts for this run.</returns>
    Task<OutboxDispatchResult> RunOnceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims and dispatches one specific envelope by id - the stream-triggered relay path (e.g. a
    /// DynamoDB Streams <c>INSERT</c> handler).
    /// </summary>
    /// <param name="envelopeId">The envelope's id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The outcome, including <see cref="OutboxDispatchOutcome.ClaimRefused"/> if the envelope could
    /// not be claimed (already claimed by another dispatcher, already dispatched/parked, or unknown).
    /// </returns>
    Task<OutboxDispatchOutcome> DispatchOneAsync(string envelopeId, CancellationToken cancellationToken = default);
}
