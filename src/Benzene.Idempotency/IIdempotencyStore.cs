namespace Benzene.Idempotency;

/// <summary>
/// Pluggable persistence for idempotency keys. Records which messages have already been (or are
/// currently being) processed so that redeliveries on an at-least-once transport can be
/// de-duplicated. Swap the implementation to change where records live (in-memory for a single
/// instance, Redis/a database for a multi-instance deployment) without touching the middleware.
/// </summary>
/// <remarks>
/// The store owns its own retention policy (time-to-live); the middleware never passes an expiry.
/// Keep records long enough to outlive the transport's maximum redelivery window.
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims <paramref name="key"/> for first-time processing.
    /// <list type="bullet">
    /// <item>If no live record exists, persists a new <see cref="IdempotencyStatus.InProgress"/>
    /// record, mints a fresh opaque claim token, and returns <see cref="ClaimResult.Won"/> carrying
    /// it in <see cref="ClaimResult.ClaimToken"/>.</item>
    /// <item>If a live record already exists, returns
    /// <see cref="ClaimResult.AlreadyExists"/> with that record and leaves it unchanged.</item>
    /// </list>
    /// Implementations MUST make the check-and-insert atomic (e.g. Redis <c>SET key val NX</c>, a
    /// unique-key insert) so concurrent redeliveries cannot both win the claim.
    /// </summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<ClaimResult> TryClaimAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a previously-claimed key to <see cref="IdempotencyStatus.Completed"/>, recording the
    /// outcome so future duplicates can be short-circuited.
    /// </summary>
    /// <remarks>
    /// <paramref name="claimToken"/> MUST be the token <see cref="ClaimResult.ClaimToken"/> returned
    /// when this caller won the claim. Implementations MUST make the settle write conditional on that
    /// token still being the live claim's token, and return <see langword="false"/> without writing
    /// anything when it is not - the claim lapsed and was reclaimed by another worker, or was already
    /// settled. A fenced write never clobbers whoever holds the claim now; there is no way to skip the
    /// token (no default parameter, no overload) - a skippable fence is no fence.
    /// </remarks>
    /// <param name="key">The idempotency key.</param>
    /// <param name="claimToken">The token returned by the winning <see cref="TryClaimAsync"/> call.</param>
    /// <param name="wasSuccessful">Whether the first processing attempt succeeded.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="claimToken"/> matched the live claim and the record
    /// was written; <see langword="false"/> if there was no live claim with that token and nothing was
    /// written.
    /// </returns>
    Task<bool> CompleteAsync(string key, string claimToken, bool wasSuccessful, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a claim so the message can be reprocessed when the transport redelivers it. Called
    /// when the handler throws or reports failure, so a transient error does not permanently
    /// suppress the message.
    /// </summary>
    /// <remarks>
    /// Same token-fencing contract as <see cref="CompleteAsync"/>: <paramref name="claimToken"/> MUST
    /// be the token returned by the winning <see cref="TryClaimAsync"/> call, the release is
    /// conditional on it still matching the live claim, and a mismatch returns <see langword="false"/>
    /// without removing anything - the claim already lapsed/was reclaimed, so there is nothing this
    /// caller still owns to release.
    /// </remarks>
    /// <param name="key">The idempotency key.</param>
    /// <param name="claimToken">The token returned by the winning <see cref="TryClaimAsync"/> call.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="claimToken"/> matched the live claim and it was
    /// removed; <see langword="false"/> if there was no live claim with that token and nothing was
    /// written.
    /// </returns>
    Task<bool> ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken = default);
}
