namespace Benzene.Outbox;

/// <summary>The outcome of dispatching a single <see cref="OutboxEnvelope"/>.</summary>
public enum OutboxDispatchOutcome
{
    /// <summary>The envelope was sent successfully and marked <see cref="OutboxStatus.Dispatched"/>.</summary>
    Dispatched,

    /// <summary>The send failed but attempts remain; the envelope was rescheduled with backoff.</summary>
    Rescheduled,

    /// <summary>
    /// The send itself succeeded, but recording that outcome (<see cref="IOutboxStore.MarkDispatchedAsync"/>)
    /// threw on every attempt (a retry was made once) - a routine transient store failure, not a failed
    /// send. The envelope is deliberately left claimed/<see cref="OutboxStatus.Pending"/> rather than
    /// rescheduled or parked (both of which would treat this as a failed send and guarantee a duplicate
    /// delivery); it becomes reclaimable by the sweeper once its lease naturally lapses. See
    /// <c>Benzene.Outbox/CLAUDE.md</c>'s "Claim fencing" and settle-failure-after-send handling.
    /// </summary>
    SentButUnsettled,

    /// <summary>The send failed and <see cref="OutboxOptions.MaxAttempts"/> was reached; the envelope was parked.</summary>
    Parked,

    /// <summary>
    /// The envelope could not be claimed - it does not exist, is not <see cref="OutboxStatus.Pending"/>,
    /// or is already leased by another claimer. Only returned by <see cref="IOutboxDispatcher.DispatchOneAsync"/>.
    /// </summary>
    ClaimRefused
}
