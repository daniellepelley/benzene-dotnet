namespace Benzene.Idempotency;

/// <summary>
/// The outcome of an <see cref="IIdempotencyStore.TryClaimAsync"/> call: either this caller won the
/// claim and should process the message, or a record already existed (a duplicate).
/// </summary>
public class ClaimResult
{
    private ClaimResult(bool claimed, IdempotencyRecord? existingRecord, string? claimToken)
    {
        Claimed = claimed;
        ExistingRecord = existingRecord;
        ClaimToken = claimToken;
    }

    /// <summary>
    /// Gets whether this caller won the claim. When <c>true</c>, the caller is the first to see this
    /// key and should process the message; when <c>false</c>, the message is a duplicate.
    /// </summary>
    public bool Claimed { get; }

    /// <summary>
    /// Gets the record that already existed when the claim was refused. <c>null</c> when
    /// <see cref="Claimed"/> is <c>true</c>.
    /// </summary>
    public IdempotencyRecord? ExistingRecord { get; }

    /// <summary>
    /// Gets the opaque token the store minted for this claim. Non-<see langword="null"/> exactly when
    /// <see cref="Claimed"/> is <c>true</c>. The caller MUST present this token, unchanged, to
    /// <see cref="IIdempotencyStore.CompleteAsync"/>/<see cref="IIdempotencyStore.ReleaseAsync"/> - a
    /// settle call whose token no longer matches the live claim (it lapsed and was reclaimed by
    /// another worker, or was already settled) is refused rather than allowed to clobber whoever holds
    /// the claim now.
    /// </summary>
    public string? ClaimToken { get; }

    /// <summary>Creates a result indicating the caller won the claim.</summary>
    /// <param name="claimToken">The opaque token minted for this claim; presented back on settle.</param>
    public static ClaimResult Won(string claimToken) => new(true, null, claimToken);

    /// <summary>Creates a result indicating a record already existed (the message is a duplicate).</summary>
    /// <param name="existing">The record already present in the store.</param>
    public static ClaimResult AlreadyExists(IdempotencyRecord existing) => new(false, existing, null);
}
