namespace Benzene.Mesh.Contracts;

/// <summary>
/// Records that one <see cref="IMeshDiscoveryProvider"/> failed (threw, or exceeded
/// <c>MeshDiscoveryRunner</c>'s per-provider timeout) during a <see cref="MeshDiscoveryRunner.DiscoverAsync"/>
/// run. A failed provider contributes no entries and the run continues with the rest - this is the
/// surfaced record of that, so a caller can tell "nothing from this provider" apart from "this
/// provider genuinely found nothing".
/// </summary>
/// <param name="ProviderKey">The failing provider's <see cref="IMeshDiscoveryProvider.Key"/>.</param>
/// <param name="ErrorType">
/// The type name of the exception (never the message - this can end up in a log/artifact with wider
/// visibility than the failure itself, same posture as <see cref="MeshServiceSnapshot.Error"/>).
/// </param>
public sealed record MeshDiscoveryProviderFailure(string ProviderKey, string ErrorType);
