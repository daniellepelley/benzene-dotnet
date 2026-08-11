using Xunit;

namespace Benzene.Mesh.Discovery.Host.Test;

/// <summary>
/// Guards the fail-fast-on-an-unknown-name rule every config surface in this work follows - a typo'd
/// provider name must name the valid values, not silently discover nothing. Does not exercise a
/// *known* name: constructing an <c>AwsLambdaDiscoveryProvider</c>/<c>AzureAppServiceDiscoveryProvider</c>/
/// <c>KubernetesServiceDiscoveryProvider</c> builds a real cloud SDK client (region/ADC/in-cluster
/// config), which can throw immediately in an environment without one - see
/// <c>Benzene.Mesh.Host.Test.AwsMeshParityTest</c>'s remarks for the same caution applied there.
/// </summary>
public class DiscoveryProviderFactoryTest
{
    [Fact]
    public void UnknownProviderName_ThrowsNamingTheValidValues()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DiscoveryProviderFactory.Build(new[] { "not-a-real-provider" }));

        Assert.Contains("not-a-real-provider", exception.Message);
        foreach (var valid in DiscoveryProviderFactory.ValidProviderNames)
        {
            Assert.Contains(valid, exception.Message);
        }
    }

    [Fact]
    public void EmptyProviderList_BuildsNoProviders()
    {
        var providers = DiscoveryProviderFactory.Build(Array.Empty<string>());

        Assert.Empty(providers);
    }
}
