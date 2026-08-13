namespace Benzene.ClaimCheck;

/// <summary>
/// Thrown by an <see cref="IClaimCheckStore"/> implementation when a reference passed to
/// <see cref="IClaimCheckStore.GetAsync"/> lies outside that store's own configuration - the wrong
/// scheme, or (for a cloud store) a bucket/container/prefix it was not configured for. This is a
/// security boundary, not a not-found case: a store MUST refuse and throw here rather than attempt to
/// fetch an attacker-supplied or otherwise foreign location, and MUST NOT silently fall through to
/// treating the reference as merely missing (<see cref="ClaimCheckNotFoundException"/>).
/// </summary>
public class ClaimCheckStoreMismatchException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimCheckStoreMismatchException"/> class.
    /// </summary>
    /// <param name="reference">The claim-check reference that does not belong to this store.</param>
    public ClaimCheckStoreMismatchException(string reference)
        : base($"The claim-check reference '{reference}' does not belong to this store's own configuration (scheme/location/prefix). Refusing to resolve a reference outside the store's configuration.")
    {
        Reference = reference;
    }

    /// <summary>Gets the claim-check reference that does not belong to this store.</summary>
    public string Reference { get; }
}
