namespace Benzene.ClaimCheck;

/// <summary>
/// The tiny payload an offloaded outbound message's request becomes once
/// <see cref="ClaimCheckOffloadMiddleware"/> has offloaded the real body: a single field carrying the
/// claim-check reference, for a human reading a raw queue message. The
/// <c>benzene-claim-check</c> header (<see cref="ClaimCheckOptions.HeaderName"/>) is authoritative -
/// a consumer with <c>UseClaimCheck&lt;TContext&gt;()</c> wired never inspects this body at all; it
/// is replaced wholesale, verbatim, with the stored body before deserialization.
/// </summary>
/// <remarks>
/// The property is deliberately named with the literal wire key - the underscore-reserved
/// <c>_benzeneClaimCheck</c>, matching the <c>_benzeneHeaders</c> embedded-header key's naming
/// rationale (<c>Benzene.Aws.Lambda.EventBridge.EventBridgeMessageHeadersGetter.EmbeddedHeadersKey</c>)
/// - rather than a PascalCase property mapped via a serializer-specific attribute. An underscore has
/// no upper/lower case, so it round-trips identically through every
/// <see cref="Benzene.Abstractions.Serialization.ISerializer"/> implementation regardless of its
/// naming policy (camelCase, PascalCase, or none), which keeps the placeholder's shape
/// serializer-agnostic the same way the rest of the claim-check mechanism is format-agnostic.
/// </remarks>
public class ClaimCheckPlaceholder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimCheckPlaceholder"/> class.
    /// </summary>
    /// <param name="reference">The claim-check reference.</param>
    public ClaimCheckPlaceholder(string reference)
    {
        _benzeneClaimCheck = reference;
    }

    /// <summary>
    /// The claim-check reference. Present for humans reading the raw message; the wire header is the
    /// authoritative value a conforming consumer actually hydrates from.
    /// </summary>
    // ReSharper disable once InconsistentNaming - the literal wire key, deliberately (see remarks).
    public string _benzeneClaimCheck { get; set; }
}
