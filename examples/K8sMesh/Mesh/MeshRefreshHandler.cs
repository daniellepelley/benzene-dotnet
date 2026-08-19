using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

using Benzene.Mesh.Aggregator;

namespace Benzene.Examples.K8sMesh.Mesh;

/// <summary>
/// On-demand discovery + aggregation trigger: <c>POST /mesh/refresh</c> runs a pass and returns 201
/// with the number of services discovered (a pass creates/refreshes the catalog artifacts).
/// </summary>
[Message("mesh:refresh")]
[HttpEndpoint("POST", "/mesh/refresh")]
public class MeshRefreshHandler : IMessageHandler<Void, MeshRefreshResult>
{
    private readonly MeshAggregationPass _pass;

    public MeshRefreshHandler(MeshAggregationPass pass)
    {
        _pass = pass;
    }

    public async Task<IBenzeneResult<MeshRefreshResult>> HandleAsync(Void request)
    {
        var discovered = await _pass.RunAsync();
        return BenzeneResult.Created(new MeshRefreshResult(discovered));
    }
}

/// <summary>The outcome of a mesh refresh.</summary>
public record MeshRefreshResult(int Discovered);
