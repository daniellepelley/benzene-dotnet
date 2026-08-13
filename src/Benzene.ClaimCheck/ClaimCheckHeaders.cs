namespace Benzene.ClaimCheck;

/// <summary>
/// The reserved header name that carries a claim-check reference on the wire. A Tier C (add-on)
/// header per the spec's tier system (<c>wire-contracts.md</c> §2) and the naming principle that a
/// Benzene-invented name in a shared namespace carries the <c>benzene-</c> marker
/// (<c>Benzene/work/benzene-naming-principle.md</c>).
/// </summary>
/// <remarks>
/// This is the single definition of the DEFAULT name - <see cref="ClaimCheckOptions.HeaderName"/>
/// aliases it rather than repeating the literal, and can be overridden per route, the same
/// "reserved names are defaults, not hard-coded" rule <c>BenzeneWireNames</c> follows for the
/// envelope-level header keys.
/// </remarks>
public static class ClaimCheckHeaders
{
    /// <summary>
    /// The default header the claim-check reference travels on: <c>benzene-claim-check</c>.
    /// </summary>
    public const string ClaimCheck = "benzene-claim-check";
}
