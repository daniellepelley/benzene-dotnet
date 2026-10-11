using System.Collections.Generic;
using System.Linq;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Wire;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// Round-16 composition finding (#251), fixed, and mesh.md §5 as spec-0.4.0 words it: a collector
/// implementing Service Versions keys its catalog by <c>(service, serviceVersion)</c>, so two releases
/// deployed side by side are two entries rather than one overwriting the other, and a heartbeat's
/// <c>registrationHash</c> is compared with the registration for the SAME <c>(service,
/// serviceVersion)</c> - the heartbeat carries its <c>serviceVersion</c> for exactly that - so two
/// versions reporting different hashes is NOT drift, while one version reporting another's hash is.
/// </summary>
/// <remarks>
/// <see cref="MeshCollectorStore.Service(string, MeshTimeRange?)"/> still returns exactly ONE
/// <see cref="ServiceView"/> per service NAME (the most-recently-registered version's scalar fields);
/// both versions' registrations remain live underneath it - the topic catalog reports both versions'
/// declared edges, and <see cref="ServiceView.Versions"/> lists them.
/// </remarks>
public class MeshCollectorSideBySideVersionTest
{
    private static MeshRegistration Registration(string service, string version, string hash, params string[] sends)
    {
        return new MeshRegistration
        {
            Service = service,
            ServiceVersion = version,
            RegistrationHash = hash,
            Requests = new List<MeshRegisteredEntry>(),
            Events = new List<MeshRegisteredEntry>(),
            Sends = sends.Select(topic => new MeshRegisteredEntry { Topic = topic }).ToList()
        };
    }

    [Fact]
    public void TwoSideBySideVersions_BothCatalogEntriesLive_EachInstanceMatchesItsOwnVersion()
    {
        var store = new MeshCollectorStore();

        // Two releases of "orders" running side by side (a canary), each providing a DIFFERENT topic
        // - a completely healthy, spec-legal state.
        store.Register(Registration("orders", "1.0.0", "sha256:v1", "order:created:v1"));
        store.Heartbeat(new MeshHeartbeat { Service = "orders", ServiceVersion = "1.0.0", InstanceId = "v1-instance", RegistrationHash = "sha256:v1" });

        store.Register(Registration("orders", "2.0.0", "sha256:v2", "order:created:v2"));
        store.Heartbeat(new MeshHeartbeat { Service = "orders", ServiceVersion = "2.0.0", InstanceId = "v2-instance", RegistrationHash = "sha256:v2" });

        // v1's contract is NOT evicted - its provided topic still shows "orders" as the provider,
        // even though v2 registered afterwards under the same service name.
        Assert.Contains("orders", store.Topic("order:created:v1", null)!.Providers);
        Assert.Contains("orders", store.Topic("order:created:v2", null)!.Providers);

        var view = store.Service("orders")!;
        // Each instance's hash is compared with ITS OWN version's registration - two live versions
        // reporting two different, individually-correct hashes is not drift.
        Assert.True(view.Instances.Single(i => i.InstanceId == "v1-instance").HashMatches);
        Assert.True(view.Instances.Single(i => i.InstanceId == "v2-instance").HashMatches);
    }

    [Fact]
    public void SameVersionReregisteredWithADifferentHash_IsFlaggedAsDrift()
    {
        var store = new MeshCollectorStore();

        // The SAME (service, version) pair re-registers with a different hash - a silent contract
        // change without a version bump (service-versions.md §1, immutability).
        store.Register(Registration("orders", "1.0.0", "sha256:original", "order:created:v1"));
        store.Heartbeat(new MeshHeartbeat { Service = "orders", ServiceVersion = "1.0.0", InstanceId = "stale-instance", RegistrationHash = "sha256:original" });

        store.Register(Registration("orders", "1.0.0", "sha256:updated", "order:created:v1", "order:created:v1-extra"));

        var staleInstance = store.Service("orders")!.Instances.Single(i => i.InstanceId == "stale-instance");
        Assert.False(staleInstance.HashMatches);
    }

    [Fact]
    public void AnInstanceReportingASiblingVersionsHash_IsDrift_NotAMatch()
    {
        // Before spec-0.4.0 a heartbeat had no serviceVersion, so the collector matched its hash against
        // ANY registered version: a v1 instance running v2's contract read as a match.
        var store = new MeshCollectorStore();
        store.Register(Registration("orders", "1.0.0", "sha256:v1", "a"));
        store.Register(Registration("orders", "2.0.0", "sha256:v2", "b"));

        store.Heartbeat(new MeshHeartbeat { Service = "orders", ServiceVersion = "1.0.0", InstanceId = "i", RegistrationHash = "sha256:v2" });

        Assert.False(Assert.Single(store.Service("orders")!.Instances).HashMatches);
    }
}
