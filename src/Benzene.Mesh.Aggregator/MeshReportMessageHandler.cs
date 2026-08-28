using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Mesh.Contracts;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Mesh.Aggregator;

/// <summary>
/// The push/self-report ingestion endpoint - accepts a <see cref="MeshServiceReport"/> and hands it
/// to whichever <see cref="IMeshReportPublisher"/> is registered (<see cref="ArtifactStoreMeshReportPublisher"/>
/// by default, via <c>Extensions.AddMeshAggregator</c>). Reachable on whatever transport the host
/// already runs, the same dogfooded shape as <see cref="MeshAggregateMessageHandler"/>.
/// </summary>
/// <remarks>
/// Only actually reachable if the consuming host's own <c>.AddMessageHandlers()</c>/<c>.UseMessageHandlers()</c>
/// call discovers it (the same opt-in every Benzene message handler already requires) - an
/// aggregator deployment that never wires this up simply has no write surface, preserving
/// "aggregator polls, UI reads static files" as the default, not a fait accompli.
///
/// <para>
/// #242: <see cref="MeshServiceReport.Name"/> is untrusted wire input that
/// <see cref="ArtifactStoreMeshReportPublisher"/> keys straight into an artifact path
/// (<c>services/{Name}.json</c>). <see cref="HandleAsync"/> rejects a null/empty/whitespace
/// <c>Name</c>, one containing a path separator (<c>/</c> or <c>\</c>), or a bare <c>"."</c>/
/// <c>".."</c> segment before it ever reaches a publisher/store - so a request like
/// <c>{"name":"../manifest"}</c> never gets built into a traversal key at all, the same
/// boundary-validation posture <see cref="MeshAnnotationsMessageHandler"/> already applies to its
/// own inputs. Rejection is a <see cref="Benzene.Results.BenzeneResultStatus.BadRequest"/> result,
/// not a throw. <see cref="FileSystemMeshArtifactStore"/>'s own storage-layer segment check is a
/// second, independent line of defense (defense in depth) - this one exists so an invalid report
/// is rejected with a clear result instead of only failing (or, pre-#242, silently succeeding)
/// deep inside whichever publisher happens to be registered.
/// </para>
/// </remarks>
[HttpEndpoint("POST", "/mesh/report")]
[Message(MeshAggregatorTopics.Report)]
public class MeshReportMessageHandler : IMessageHandler<MeshServiceReport, Void>
{
    private static readonly char[] PathSeparators = { '/', '\\' };

    private readonly IMeshReportPublisher _publisher;

    /// <summary>Initializes a new instance of the <see cref="MeshReportMessageHandler"/> class.</summary>
    /// <param name="publisher">Publishes the incoming report into the mesh catalog.</param>
    public MeshReportMessageHandler(IMeshReportPublisher publisher)
    {
        _publisher = publisher;
    }

    /// <inheritdoc />
    public async Task<IBenzeneResult<Void>> HandleAsync(MeshServiceReport request)
    {
        var name = request.Name;

        if (string.IsNullOrWhiteSpace(name))
        {
            return BenzeneResult.BadRequest<Void>("name is required");
        }

        if (name.IndexOfAny(PathSeparators) >= 0)
        {
            return BenzeneResult.BadRequest<Void>("name must not contain a path separator");
        }

        if (name is "." or "..")
        {
            return BenzeneResult.BadRequest<Void>("name must not be a '.' or '..' path segment");
        }

        await _publisher.PublishAsync(request);
        return BenzeneResult.Accepted<Void>();
    }
}
