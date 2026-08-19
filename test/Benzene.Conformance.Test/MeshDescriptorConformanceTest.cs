using System.Text.Json;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Messages;
using Benzene.Conformance.Test.Handlers;
using Benzene.Core.MessageHandlers;
using Benzene.Mesh.Wire;
using Xunit;

namespace Benzene.Conformance.Test;

/// <summary>
/// Runs docs/specification/conformance/mesh-descriptor-cases.json: derives the ServiceDescriptor
/// (mesh.md §2) from the canonical conformance handlers and asserts the derived payload schemas
/// plus the descriptorHash's format/invariance/sensitivity properties. runtime and the hash value
/// are per-port by design and not pinned by the fixture.
/// </summary>
public class MeshDescriptorConformanceTest
{
    public class DescriptorFixture
    {
        public ServiceInfoSection ServiceInfo { get; set; } = new();
        public JsonElement ExpectedDescriptor { get; set; }
        public HashSection Hash { get; set; } = new();
    }

    public class ServiceInfoSection
    {
        public string Service { get; set; } = string.Empty;
        public string? ServiceVersion { get; set; }
        public PlacementSection Placement { get; set; } = new();
    }

    public class PlacementSection
    {
        public string Cloud { get; set; } = string.Empty;
        public string? Region { get; set; }
    }

    public class HashSection
    {
        public string Prefix { get; set; } = string.Empty;
        public int HexLength { get; set; }
        public bool InvariantToInstanceId { get; set; }
        public bool SensitiveToServiceVersion { get; set; }
        public bool SensitiveToTopics { get; set; }
        public bool SensitiveToProduces { get; set; }
    }

    /// <summary>
    /// The canonical outbound registration (conformance/README.md "Canonical outbound registration"):
    /// <c>conformance:log</c>, request <c>{ "message": string }</c>, no declared response type - no
    /// handler, since nothing here receives.
    /// </summary>
    public class LogRequest
    {
        public string Message { get; set; } = string.Empty;
    }

    private static readonly Lazy<DescriptorFixture> Fixture = new(() =>
        ConformanceFixtures.Load<DescriptorFixture>("mesh-descriptor-cases.json"));

    private static MeshServiceInfo Info(string? instanceId = null, string? serviceVersion = null)
    {
        var fixture = Fixture.Value;
        return new MeshServiceInfo(
            fixture.ServiceInfo.Service,
            serviceVersion ?? fixture.ServiceInfo.ServiceVersion,
            instanceId,
            placement: new MeshPlacement
            {
                Cloud = fixture.ServiceInfo.Placement.Cloud,
                Region = fixture.ServiceInfo.Placement.Region
            });
    }

    private static IMessageHandlerDefinitionLookUp CanonicalLookUp(params Type[] extraHandlerTypes)
    {
        var types = new[] { typeof(GreetConformanceHandler), typeof(StatusConformanceHandler) }
            .Concat(extraHandlerTypes)
            .ToArray();
        return new DefinitionsLookUp(new ReflectionMessageHandlersFinder(types).FindDefinitions());
    }

    /// <summary>The canonical outbound registration, explicit registration (spec §2.3's MUST-support
    /// path) rather than attribute scanning - no handler is registered for <c>conformance:log</c>
    /// anywhere, and none should be.</summary>
    private static MeshOutboundRegistry CanonicalOutboundLookUp(bool withExtraTopic = false)
    {
        var registry = new MeshOutboundRegistry().Register<LogRequest>("conformance:log");
        if (withExtraTopic)
        {
            registry.Register<LogRequest>("conformance:log-extra");
        }
        return registry;
    }

    [Fact]
    public void DerivedDescriptor_MatchesTheExpectedDescriptor()
    {
        var descriptor = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(), CanonicalOutboundLookUp());

        using var actual = JsonDocument.Parse(MeshJson.Serialize(descriptor));
        var mismatch = ConformanceFixtures.FindSubsetMismatch(Fixture.Value.ExpectedDescriptor, actual.RootElement);
        Assert.True(mismatch == null, $"descriptor mismatch at {mismatch}");
    }

    [Fact]
    public void DescriptorHash_HasTheWireFormat()
    {
        var hash = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(), CanonicalOutboundLookUp()).DescriptorHash;

        Assert.NotNull(hash);
        Assert.StartsWith(Fixture.Value.Hash.Prefix, hash);
        Assert.Equal(Fixture.Value.Hash.Prefix.Length + Fixture.Value.Hash.HexLength, hash!.Length);
        Assert.Matches("^[0-9a-f]+$", hash.Substring(Fixture.Value.Hash.Prefix.Length));
    }

    [Fact]
    public void DescriptorHash_IsInvariantToInstanceId()
    {
        if (!Fixture.Value.Hash.InvariantToInstanceId) return; // not asserted by the fixture

        var first = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(instanceId: "instance-1"), CanonicalOutboundLookUp());
        var second = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(instanceId: "instance-2"), CanonicalOutboundLookUp());

        Assert.Equal(first.DescriptorHash, second.DescriptorHash);
    }

    [Fact]
    public void DescriptorHash_IsSensitiveToServiceVersion()
    {
        if (!Fixture.Value.Hash.SensitiveToServiceVersion) return; // not asserted by the fixture

        var baseline = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(), CanonicalOutboundLookUp());
        var bumped = MeshDescriptorFactory.Create(CanonicalLookUp(),
            Info(serviceVersion: Fixture.Value.ServiceInfo.ServiceVersion + "-changed"), CanonicalOutboundLookUp());

        Assert.NotEqual(baseline.DescriptorHash, bumped.DescriptorHash);
    }

    [Fact]
    public void DescriptorHash_IsSensitiveToTheTopicSet()
    {
        if (!Fixture.Value.Hash.SensitiveToTopics) return; // not asserted by the fixture

        var baseline = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(), CanonicalOutboundLookUp());
        var grown = MeshDescriptorFactory.Create(CanonicalLookUp(typeof(PanicConformanceHandler)), Info(), CanonicalOutboundLookUp());

        Assert.NotEqual(baseline.DescriptorHash, grown.DescriptorHash);
    }

    [Fact]
    public void DescriptorHash_IsSensitiveToTheProducedTopicSet()
    {
        if (!Fixture.Value.Hash.SensitiveToProduces) return; // not asserted by the fixture

        var baseline = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(), CanonicalOutboundLookUp());
        var grown = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(), CanonicalOutboundLookUp(withExtraTopic: true));

        Assert.NotEqual(baseline.DescriptorHash, grown.DescriptorHash);
    }

    [Fact]
    public void MissingRegistry_DegradesTheFeedNotTheDescriptor()
    {
        var descriptor = MeshDescriptorFactory.Create(null, Info(), CanonicalOutboundLookUp());

        Assert.Empty(descriptor.Topics);
        Assert.Equal(new List<string> { MeshDescriptorFactory.RegistryFeed }, descriptor.Degraded);
        Assert.Equal(Fixture.Value.ServiceInfo.Service, descriptor.Service);
        Assert.NotNull(descriptor.DescriptorHash);
    }

    [Fact]
    public void MissingOutboundRegistry_DegradesTheFeedNotTheDescriptor()
    {
        // spec §2/§2.3: a port that hasn't wired up outbound registration MUST mark `produces`
        // degraded rather than emit an empty array - an empty array asserts "produces nothing", which
        // a port that cannot yet know that has no right to assert.
        var descriptor = MeshDescriptorFactory.Create(CanonicalLookUp(), Info(), outboundLookUp: null);

        Assert.Empty(descriptor.Produces);
        Assert.Equal(new List<string> { MeshDescriptorFactory.OutboundRegistryFeed }, descriptor.Degraded);
        Assert.NotNull(descriptor.DescriptorHash);
    }

    [Fact]
    public void MissingBothRegistries_DegradesBothFeeds()
    {
        var descriptor = MeshDescriptorFactory.Create(null, Info());

        Assert.Empty(descriptor.Topics);
        Assert.Empty(descriptor.Produces);
        Assert.Equal(
            new List<string> { MeshDescriptorFactory.RegistryFeed, MeshDescriptorFactory.OutboundRegistryFeed },
            descriptor.Degraded);
    }

    private class DefinitionsLookUp : IMessageHandlerDefinitionLookUp
    {
        private readonly IMessageHandlerDefinition[] _definitions;

        public DefinitionsLookUp(IMessageHandlerDefinition[] definitions)
        {
            _definitions = definitions;
        }

        public IMessageHandlerDefinition? FindHandler(ITopic topic)
        {
            return _definitions.FirstOrDefault(x => x.Topic.Id == topic.Id && x.Topic.Version == topic.Version);
        }

        public IMessageHandlerDefinition[] GetAllHandlers()
        {
            return _definitions;
        }
    }
}
