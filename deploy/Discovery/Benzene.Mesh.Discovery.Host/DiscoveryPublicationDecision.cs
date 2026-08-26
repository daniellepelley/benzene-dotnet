namespace Benzene.Mesh.Discovery.Host;

/// <summary>
/// Decides whether a discovery run's registry should be published, given how many providers were
/// configured and how many of them failed. Pulled out of <c>Program.cs</c>'s top-level statements so
/// this one decision - the part of a one-shot job worth getting exactly right - is unit-testable
/// without running the whole process.
/// </summary>
/// <remarks>
/// <para>
/// [DECISION 2026-08] Since <c>MeshDiscoveryRunner</c> gained per-provider failure isolation (#148), a
/// registry can come back empty for two very different reasons: "no providers configured" (this
/// job's own README documents that as intentional - a run with nothing wired publishes an empty
/// registry) and "every configured provider failed". Treating those the same would let a total outage
/// of every discovery source silently publish an empty registry that reads as "the fleet is gone" to
/// whatever mesh host reads it back next - actively worse than the stale-but-real registry already on
/// disk. So: every configured provider failing refuses to publish at all (leaving the previous
/// registry document, if any, exactly as it was); some providers failing does not - the registry from
/// whichever providers succeeded is still published, because a partial result is strictly more useful
/// than none, and the caller is expected to still surface which providers failed (e.g. on stderr).
/// </para>
/// </remarks>
public static class DiscoveryPublicationDecision
{
    /// <summary>
    /// Whether this run's registry should be published.
    /// </summary>
    /// <param name="providerCount">How many discovery providers were configured for this run.</param>
    /// <param name="failureCount">How many of them failed (see <c>MeshDiscoveryProviderFailure</c>).</param>
    /// <returns>
    /// <c>false</c> only when at least one provider was configured and every single one of them
    /// failed. <c>true</c> for zero configured providers (nothing could have failed) and for any run
    /// where at least one provider succeeded.
    /// </returns>
    public static bool ShouldPublish(int providerCount, int failureCount) =>
        providerCount == 0 || failureCount < providerCount;
}
