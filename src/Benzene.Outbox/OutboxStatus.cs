namespace Benzene.Outbox;

/// <summary>
/// Lifecycle state of an <see cref="OutboxEnvelope"/> in an <see cref="IOutboxStore"/>.
/// </summary>
public enum OutboxStatus
{
    /// <summary>Captured and waiting to be dispatched (or waiting for its next retry attempt).</summary>
    Pending,

    /// <summary>Successfully sent through the outbound route's pipeline. Retained until <see cref="OutboxOptions.RetentionPeriod"/> elapses.</summary>
    Dispatched,

    /// <summary>
    /// Repeatedly failed to dispatch and reached <see cref="OutboxOptions.MaxAttempts"/>. Terminal
    /// until a human intervenes - Benzene never auto-deletes or auto-retries a parked envelope (no
    /// dead-letter forwarding; see <c>Benzene.Outbox/CLAUDE.md</c>).
    /// </summary>
    Parked
}
