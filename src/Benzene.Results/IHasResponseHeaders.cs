namespace Benzene.Results;

/// <summary>
/// A capability interface a concrete <see cref="Abstractions.Results.IBenzeneResult"/> implementation
/// can carry response headers on - the headers a handler attached with
/// <see cref="BenzeneResultExtensions.WithHeader{T}"/> (a <c>Location</c> on a 201, an <c>ETag</c>, a
/// <c>Cache-Control</c>). Like <see cref="IHasProblemDetails"/>, deliberately NOT a member of
/// <c>IBenzeneResult</c> itself, which is frozen.
/// </summary>
/// <remarks>
/// A transport that has response headers writes them after the response is rendered, and never
/// replaces a header the pipeline already set (CORS, the rendered <c>content-type</c>): the pipeline's
/// headers win. A transport with no response headers (a queue) ignores them. Read them through
/// <see cref="BenzeneResultExtensions.GetResponseHeaders"/> rather than by casting.
/// </remarks>
public interface IHasResponseHeaders
{
    /// <summary>
    /// The response headers attached to this result; empty when none were. Names compare ignoring
    /// case, one value per name.
    /// </summary>
    IReadOnlyDictionary<string, string> ResponseHeaders { get; }
}
