namespace Benzene.Outbox;

/// <summary>The outcome of dispatching a single <see cref="OutboxEnvelope"/>.</summary>
public enum OutboxDispatchOutcome
{
    /// <summary>The envelope was sent successfully and marked <see cref="OutboxStatus.Dispatched"/>.</summary>
    Dispatched,

    /// <summary>The send failed but attempts remain; the envelope was rescheduled with backoff.</summary>
    Rescheduled,

    /// <summary>The send failed and <see cref="OutboxOptions.MaxAttempts"/> was reached; the envelope was parked.</summary>
    Parked,

    /// <summary>
    /// The envelope could not be claimed - it does not exist, is not <see cref="OutboxStatus.Pending"/>,
    /// or is already leased by another claimer. Only returned by <see cref="IOutboxDispatcher.DispatchOneAsync"/>.
    /// </summary>
    ClaimRefused
}
