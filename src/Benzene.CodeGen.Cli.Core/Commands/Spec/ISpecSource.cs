using Benzene.Schema.OpenApi;

namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

/// <summary>
/// Resolves a Benzene spec document as raw JSON, wherever it lives - a live AWS Lambda invocation,
/// a local file (Phase 1's <c>{Service}.spec.json</c> build artifact), a running Cloud Service's
/// HTTP endpoint, or a mesh manifest's cached snapshot. Every implementation returns the document
/// verbatim: deserializable via <see cref="Benzene.Schema.OpenApi.EventService.EventServiceDocumentDeserializer"/>,
/// the same shape the mesh itself stores without re-serializing
/// (<c>Benzene.Mesh.Contracts.MeshServiceSnapshot.SpecJson</c>). This is what decouples `benzene
/// build`/`benzene spec` from needing deployed-AWS-Lambda + credentials.
/// </summary>
public interface ISpecSource
{
    /// <summary>
    /// Fetches/reads the spec document JSON.
    /// </summary>
    /// <param name="request">
    /// The document type/format to request, honored by sources that can ask for a specific
    /// representation (AWS Lambda, HTTP). A file or mesh source returns whatever was stored,
    /// regardless of this request - there is only one document at that location.
    /// </param>
    Task<string> GetSpecJsonAsync(SpecRequest request);
}
