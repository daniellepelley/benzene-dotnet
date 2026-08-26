namespace Benzene.Idempotency.DynamoDb;

/// <summary>
/// Thrown by <see cref="DynamoDbIdempotencyStore.TryClaimAsync"/> when every bounded retry of the
/// conditional claim write lost to a <c>ConditionalCheckFailedException</c> whose immediate read-back
/// found no live record — a persistently oscillating race on the same key (e.g. a concurrent
/// claim/release pair repeatedly interleaving with this caller's attempts).
/// </summary>
/// <remarks>
/// This is the deliberate alternative to synthesizing a <see cref="ClaimResult.Won"/> from an empty
/// read: every <see cref="ClaimResult.Won"/> the store returns corresponds to an actual successful
/// <c>PutItem</c>, so when the retries are exhausted without one, the store surfaces the contention
/// rather than fabricating an outcome. This should be rare in practice — it requires the key to flip
/// between absent and live faster than this caller can win a single conditional write across several
/// attempts.
/// </remarks>
public class IdempotencyClaimContentionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyClaimContentionException"/> class.
    /// </summary>
    /// <param name="key">The idempotency key that could not be claimed.</param>
    /// <param name="attempts">How many conditional <c>PutItem</c> attempts were made before giving up.</param>
    public IdempotencyClaimContentionException(string key, int attempts)
        : base($"Could not claim idempotency key '{key}' after {attempts} attempts: every conditional " +
               "write lost to a live record that was then observed absent on read-back (persistent contention).")
    {
        Key = key;
        Attempts = attempts;
    }

    /// <summary>Gets the idempotency key that could not be claimed.</summary>
    public string Key { get; }

    /// <summary>Gets how many conditional <c>PutItem</c> attempts were made before giving up.</summary>
    public int Attempts { get; }
}
