using System.Collections.Generic;
using System.Linq;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Wire;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Round-16 composition finding (#251), fixed: mesh spec §2.4 requires "a collector's catalog key is
/// the pair (service, serviceVersion) - two releases deployed side by side are two catalog entries
/// rather than one silently overwriting the other" and explicitly rules that "two different versions
/// reporting different hashes is NOT drift". <see cref="MeshCollectorStore"/> now keys its internal
/// per-service state by <c>ServiceVersion ?? ""</c> (<c>ServiceState.Descriptors</c>): <c>Register</c>
/// for a new version no longer evicts a still-live sibling version's descriptor, and
/// <c>ServiceView.Instances[].HashMatches</c> is computed against EVERY currently registered version's
/// hash for the service, not just the one "current"/latest row - so each instance matches against its
/// own version's descriptor rather than whichever version registered last.
/// </summary>
/// <remarks>
/// [RESOLVED] view-shape choice: <see cref="MeshCollectorStore.Service(string, MeshTimeRange?)"/>
/// still returns exactly ONE <see cref="ServiceView"/> per service NAME (the most-recently-registered
/// version's scalar Runtime/Binding/Placement/Topics/ServiceVersion/Descriptor fields) - preserving
/// today's one-row-per-name shape so every existing caller/test keying purely on name is unaffected
/// (in particular <c>MeshCollectorStoreTest.Reregistration_ReplacesServiceVersion_WithTheLatestDescriptors</c>,
/// which asserts exactly one "orders" row via <c>Fleet().Services.Single(...)</c> after two versions
/// register - that test's two registrations share identical topics/produces, so it exercises the
/// "latest wins the headline row" path without needing a second live row to be visible by name).
/// Both versions' full descriptors (topics/produces/hash) remain live underneath that one row: the
/// topic catalog (<c>mesh:query:topic</c>) reports BOTH versions' declared provider/consumer edges
/// (queried by topic id, so a second row was never needed there), and per-instance
/// <see cref="InstanceView.HashMatches"/> is resolved against the full live set, not the headline
/// row alone. A dedicated per-version breakdown on <see cref="ServiceView"/> itself (e.g. a
/// <c>Versions</c> collection) was NOT added this round - not required by any of the three behaviors
/// spec §2.4 actually mandates (no silent eviction; correct per-instance hash comparison; drift only
/// on a genuine same-version hash change), and speculative until a caller needs to render "which
/// versions are live" as its own list.
/// </remarks>
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
    public void TwoSideBySideVersions_BothCatalogEntriesLive_EachInstanceMatchesItsOwnVersion()
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

        // GREEN: v1's contract is NOT evicted - its provided topic still shows "orders" as the
        // provider, even though v2 registered afterwards under the same service name.
        var v1Topic = store.Topic("order:created:v1", null);
        var v2Topic = store.Topic("order:created:v2", null);
        Assert.NotNull(v1Topic);
        Assert.Contains("orders", v1Topic!.Providers);
        Assert.NotNull(v2Topic);
        Assert.Contains("orders", v2Topic!.Providers);

        var view = store.Service("orders");
        Assert.NotNull(view);

        var v1Instance = view!.Instances.Single(i => i.InstanceId == "v1-instance");
        var v2Instance = view.Instances.Single(i => i.InstanceId == "v2-instance");

        // GREEN: each instance's own, correctly-computed hash is compared against its OWN version's
        // descriptor (not just whichever version happens to be "current") - two live versions
        // reporting two different, individually-correct hashes is the expected side-by-side
        // deployment state, not drift (§2.4).
        Assert.True(v1Instance.HashMatches);
        Assert.True(v2Instance.HashMatches);
    }

    [Fact]
    public void SameVersionReregisteredWithADifferentHash_IsFlaggedAsDrift()
    {
        var store = new MeshCollectorStore();

        // A real drift scenario (§2.4's OTHER case): the SAME (service, version) pair re-registers
        // with a different descriptor hash - a silent contract change without a version bump.
        var v1Original = Descriptor("orders", "1.0.0", "order:created:v1");
        store.Register(v1Original);
        store.Heartbeat(new MeshHeartbeat { Service = "orders", InstanceId = "stale-instance", DescriptorHash = v1Original.DescriptorHash });

        // "orders" 1.0.0 redeploys with a changed contract (a second produced topic) but the SAME
        // version string - a genuinely different hash under the same catalog key.
        var v1Updated = Descriptor("orders", "1.0.0", "order:created:v1");
        v1Updated.Produces.Add(new MeshTopicDescriptor { Id = "order:created:v1-extra" });
        v1Updated.DescriptorHash = MeshDescriptorHashing.ComputeHash(v1Updated);
        Assert.NotEqual(v1Original.DescriptorHash, v1Updated.DescriptorHash);
        store.Register(v1Updated);

        var view = store.Service("orders");
        Assert.NotNull(view);
        var staleInstance = view!.Instances.Single(i => i.InstanceId == "stale-instance");

        // GREEN: the instance still reporting the OLD hash for the SAME version no longer matches
        // any live descriptor for "orders" - this IS drift, and must still be flagged as such.
        Assert.False(staleInstance.HashMatches);
    }
}
