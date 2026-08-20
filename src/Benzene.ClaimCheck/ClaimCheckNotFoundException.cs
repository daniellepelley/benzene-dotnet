namespace Benzene.ClaimCheck;

/// <summary>
/// Thrown by <see cref="ClaimCheckHydrateMiddleware{TContext}"/> when a message carries a
/// claim-check reference but <see cref="IClaimCheckStore.GetAsync"/> returns <c>null</c> for it - the
/// payload was never stored, was already expired, or was stored by a different (foreign) service
/// that this store cannot see. This is a fail-loud failure by design (§3 of
/// <c>work/archive/claim-check-plan-2026-08.md</c>): there is no silent skip and no placeholder-processing, so the
/// transport's normal failure semantics (nack, redelivery, eventually a DLQ) apply exactly as they
/// would for any other unprocessable message.
/// </summary>
public class ClaimCheckNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimCheckNotFoundException"/> class.
    /// </summary>
    /// <param name="reference">The claim-check reference that could not be resolved.</param>
    public ClaimCheckNotFoundException(string reference)
        : base($"No claim-check payload was found for reference '{reference}'. It may have expired, or never existed.")
    {
        Reference = reference;
    }

    /// <summary>Gets the claim-check reference that could not be resolved.</summary>
    public string Reference { get; }
}
