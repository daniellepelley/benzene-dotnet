using Benzene.Mesh.Discovery.Host;
using Xunit;

namespace Benzene.Mesh.Discovery.Host.Test;

/// <summary>
/// #148's residual decision: a failed provider no longer aborts the whole discovery run, so this is
/// what stops that from silently degrading into "every provider failed, publish an empty registry
/// anyway" - which would read as "the fleet is gone" to the mesh host that reads it back.
/// </summary>
public class DiscoveryPublicationDecisionTest
{
    [Fact]
    public void NoFailures_Publishes()
    {
        Assert.True(DiscoveryPublicationDecision.ShouldPublish(providerCount: 3, failureCount: 0));
    }

    [Fact]
    public void SomeProvidersFail_StillPublishes_APartialResultBeatsNone()
    {
        Assert.True(DiscoveryPublicationDecision.ShouldPublish(providerCount: 3, failureCount: 2));
    }

    [Fact]
    public void EveryConfiguredProviderFails_RefusesToPublish()
    {
        Assert.False(DiscoveryPublicationDecision.ShouldPublish(providerCount: 3, failureCount: 3));
        Assert.False(DiscoveryPublicationDecision.ShouldPublish(providerCount: 1, failureCount: 1));
    }

    [Fact]
    public void ZeroProvidersConfigured_StillPublishes_NotAllFailuresCase()
    {
        // README's documented, unrelated case: no discovery wired at all publishes an empty registry
        // as intentional config, not as "everything failed" - there was nothing to fail.
        Assert.True(DiscoveryPublicationDecision.ShouldPublish(providerCount: 0, failureCount: 0));
    }
}
