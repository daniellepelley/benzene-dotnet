using Benzene.Mesh.Contracts;
using Benzene.Mesh.Host;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// <see cref="MeshServiceRegistryEntry"/> has an <c>OwningTeam</c> property the Mesh UI renders, but
/// <see cref="MeshHostServiceConfig"/> had no matching property and <c>ToEntry()</c> never passed one -
/// so the "who do I talk to before I change this" field could never be set from <c>mesh.json</c>. Guards
/// that it now carries through, alongside the existing <c>Source</c> default this config type relies on.
/// </summary>
public class MeshHostServiceConfigTest
{
    [Fact]
    public void ToEntry_OwningTeamSet_CarriesThroughToTheRegistryEntry()
    {
        var config = new MeshHostServiceConfig
        {
            Name = "orders-api",
            OwningTeam = "team-orders",
        };

        var entry = config.ToEntry();

        Assert.Equal("team-orders", entry.OwningTeam);
    }

    [Fact]
    public void ToEntry_SourceOmitted_DefaultsToHttp()
    {
        var config = new MeshHostServiceConfig { Name = "orders-api" };

        var entry = config.ToEntry();

        Assert.Equal(MeshServiceSource.Http, entry.Source);
    }
}
