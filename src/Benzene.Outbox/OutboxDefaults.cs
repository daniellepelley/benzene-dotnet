namespace Benzene.Outbox;

/// <summary>Shared default values for outbox handling.</summary>
public static class OutboxDefaults
{
    /// <summary>
    /// The header a captured envelope's id is stamped into by default (see
    /// <see cref="OutboxOptions.StampIdempotencyKey"/>), so a consumer running
    /// <c>Benzene.Idempotency</c>'s default key strategy dedups relay redeliveries with zero
    /// configuration.
    /// </summary>
    /// <remarks>
    /// This is a deliberate value duplicate of <c>Benzene.Idempotency.IdempotencyDefaults.HeaderName</c>
    /// - <c>Benzene.Outbox</c> does not take a package reference on <c>Benzene.Idempotency</c> just for
    /// one string, so it stays usable (and testable) without the idempotency package installed. If you
    /// change one, change the other; see the mirror note in both packages' <c>CLAUDE.md</c>.
    /// </remarks>
    public const string IdempotencyKeyHeaderName = "idempotency-key";
}
