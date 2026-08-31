using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Contracts;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Examples.AwsMesh.Mesh;

/// <summary>
/// The discovery + aggregation pass. Discovers the benzene-tagged Lambdas, unions in any
/// admin-configured <see cref="MeshExtraServicesSeed"/> (external, non-AWS services reached over
/// plain HTTP — see that type's remarks), writes the merged registry to S3 (the "discovery creates
/// the config" seam), interrogates each discovered/seeded service via the aggregator, and writes the
/// catalog artifacts to S3. Triggered on a schedule by EventBridge (detail-type <c>mesh:aggregate</c>)
/// or on demand by <c>POST /mesh/refresh</c>.
/// </summary>
[Message("benzene:mesh:aggregate")]
[HttpEndpoint("POST", "/mesh/refresh")]
public class MeshAggregateHandler : IMessageHandler<Void, MeshAggregateSummary>
{
    private readonly MeshDiscoveryRunner _discovery;
    private readonly IMeshArtifactStore _store;
    private readonly MeshAggregator _aggregator;
    private readonly MeshExtraServicesSeed _extraServices;

    public MeshAggregateHandler(
        MeshDiscoveryRunner discovery, IMeshArtifactStore store, MeshAggregator aggregator,
        MeshExtraServicesSeed extraServices)
    {
        _discovery = discovery;
        _store = store;
        _aggregator = aggregator;
        _extraServices = extraServices;
    }

    public async Task<IBenzeneResult<MeshAggregateSummary>> HandleAsync(Void request)
    {
        // 1. Discover benzene-tagged services (default filter), unioned with the admin-configured
        //    static seed (if any) - the seed wins on a name clash, since it's a deliberate human
        //    override of whatever AWS discovery finds (see MeshDiscoveryRunner.DiscoverAsync).
        var registry = await _discovery.DiscoverAsync(new MeshDiscoveryFilter(), _extraServices.Registry);

        // 2. Persist the discovered config to S3 AND interrogate each service + publish the catalog
        //    concurrently - the registry.json write is independent of the aggregation run (which takes
        //    the registry object directly, not from S3), so there's no reason to serialise them.
        await Task.WhenAll(
            _store.PublishAsync("registry.json", MeshRegistryJson.Serialize(registry)),
            _aggregator.RunOnceAsync(registry));

        // A pass creates/refreshes the catalog artifacts (a state change), so signal 201 rather than
        // 200 on the POST /mesh/refresh surface. (On the EventBridge path the status is irrelevant.)
        return BenzeneResult.Created(new MeshAggregateSummary(registry.Services.Length));
    }
}

/// <summary>The outcome of a mesh aggregation pass.</summary>
public class MeshAggregateSummary
{
    public MeshAggregateSummary(int discovered)
    {
        Discovered = discovered;
    }

    /// <summary>The number of services discovered and catalogued.</summary>
    public int Discovered { get; }
}

/// <summary>
/// The admin-managed <c>MESH_EXTRA_SERVICES</c> seed (Option B, work/mesh-external-service-discovery-scope-2026-08.md):
/// a hand-configured <see cref="MeshServiceRegistry"/> of services outside this AWS account/Terraform
/// stack entirely, reached over plain HTTP (spec/health URLs) via the default <c>HttpMeshServiceSource</c>
/// rather than Lambda Invoke. <see cref="Registry"/> is passed straight to
/// <see cref="MeshDiscoveryRunner.DiscoverAsync"/> as its <c>staticSeed</c> - <c>null</c> when the env
/// var is unset/empty, which makes a pass byte-identical to one with no seed configured at all.
/// <para>
/// Deliberately its OWN singleton type, not another registration of the bare <see cref="MeshServiceRegistry"/>
/// type: <c>Benzene.Mesh.Aggregator.AddMeshAggregator</c> already registers one <see cref="MeshServiceRegistry"/>
/// singleton (the "discovery starts empty" registry), and <c>Startup.ConfigureServices</c> registers a
/// second, scoped one (the dispatch target, re-read from S3 per request). Microsoft.Extensions.DependencyInjection
/// resolves a single (non-<c>IEnumerable&lt;T&gt;</c>) request for a type to whichever registration was
/// added LAST, regardless of lifetime - so a third <see cref="MeshServiceRegistry"/> registration here
/// would silently become the winner for every other consumer resolving <see cref="MeshServiceRegistry"/>
/// from DI (in particular the scoped dispatch-target one, breaking <c>POST /mesh/dispatch</c>'s target
/// resolution). Wrapping the seed in its own type sidesteps that collision entirely.
/// </para>
/// </summary>
public sealed record MeshExtraServicesSeed(MeshServiceRegistry? Registry);
