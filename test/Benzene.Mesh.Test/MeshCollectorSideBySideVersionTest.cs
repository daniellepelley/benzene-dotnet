using System.Collections.Generic;
using System.Linq;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Wire;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Round-16 composition finding: mesh spec §2.4 requires "a collector's catalog key is the pair
/// (service, serviceVersion) - two releases deployed side by side are two catalog entries rather
/// than one silently overwriting the other" and explicitly rules that "two different versions
/// reporting different hashes is NOT drift". <see cref="MeshCollectorStore"/>'s in-memory model
/// (<c>_services</c>, keyed by service NAME only - see <c>EnsureService(string name)</c>) has no
/// representation of that pair at all: <c>Register</c> unconditionally overwrites
/// <c>ServiceState.Descriptor</c> wholesale, so the second of two side-by-side versions to register
/// silently evicts the first's topics/produces/schemas from the catalog, and every still-healthy
/// instance of the now-evicted version is reported as a descriptor-hash mismatch against the
/// survivor's descriptor - exactly the false "contract drift" signal §2.4 says a legitimate
/// side-by-side deployment (canary/blue-green) must NOT produce. <c>descriptorHash</c> itself
/// (<see cref="MeshDescriptorHashing"/>) and the per-instance mismatch flag
/// (<c>ServiceView.Instances[].HashMatches</c>) are each individually correct; the seam that breaks
/// is the store never having a slot for more than one live version per service name.
/// </summary>
public class MeshCollectorSideBySideVersionTest
{
    private static MeshServiceDescriptor Descriptor(string service, string version, string topic)
    {
        var descriptor = new MeshServiceDescriptor
        {
            Service = service,
            ServiceVersion = version,
            Topics = new List<MeshTopicDescriptor>(),
            Produces = new List<MeshTopicDescriptor> { new() { Id = topic } }
        };
        descriptor.DescriptorHash = MeshDescriptorHashing.ComputeHash(descriptor);
        return descriptor;
    }

    [Fact]
    public void TwoSideBySideVersions_SecondRegistrationEvictsTheFirstsContractAndFalselyFlagsItAsDrift()
    {
        var store = new MeshCollectorStore();

        // Two releases of "orders" running side by side (a canary), each providing a DIFFERENT topic
        // - a completely healthy, spec-legal state (§2.4).
        var v1 = Descriptor("orders", "1.0.0", "order:created:v1");
        var v2 = Descriptor("orders", "2.0.0", "order:created:v2");

        store.Register(v1);
        store.Heartbeat(new MeshHeartbeat { Service = "orders", InstanceId = "v1-instance", DescriptorHash = v1.DescriptorHash });

        store.Register(v2);
        store.Heartbeat(new MeshHeartbeat { Service = "orders", InstanceId = "v2-instance", DescriptorHash = v2.DescriptorHash });

        var view = store.Service("orders");
        Assert.NotNull(view);

        // RED: the catalog can only ever report ONE serviceVersion for "orders" - v1's contract
        // (its provided topic) has been silently evicted, even though v1's instance is still healthy
        // and heartbeating with its own correct descriptor hash.
        Assert.Equal("2.0.0", view!.ServiceVersion);

        var v1Instance = view.Instances.Single(i => i.InstanceId == "v1-instance");
        var v2Instance = view.Instances.Single(i => i.InstanceId == "v2-instance");

        // RED: v1's instance is reporting the EXACT hash of the descriptor it itself registered, yet
        // is flagged as a mismatch purely because v2's later registration evicted it from the shared
        // single-descriptor slot - a false positive drift signal on a legitimate side-by-side release,
        // which §2.4 explicitly says must not be reported as drift.
        Assert.False(v1Instance.HashMatches);
        Assert.True(v2Instance.HashMatches);
    }
}
