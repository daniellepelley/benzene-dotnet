using Benzene.Abstractions.Results;
using Benzene.Results;

namespace Benzene.Abstractions.MessageHandlers.Response;

/// <summary>
/// Writes a failed result's body in a format of the application's choosing instead of the RFC 9457
/// problem document - for a service that must keep an error format its callers already depend on.
/// It receives the problem document Benzene built, so it only reshapes it; success payloads are
/// untouched. Register one per context type with <c>AddProblemFormatter&lt;TContext&gt;(...)</c>, so it
/// applies to that transport only.
/// </summary>
/// <remarks>
/// <para>
/// <b>A replaced format doesn't conform.</b> The spec says a failure body is an RFC 9457 problem
/// document (wire-contracts §1.3), and Benzene clients and the mesh read its <c>errors</c>. Register a
/// formatter for the HTTP-native contexts only (<c>ApiGatewayContext</c>, <c>ApiGatewayV2Context</c>,
/// <c>AspNetContext</c>), never for <c>BenzeneMessageContext</c>, so the envelope endpoint keeps
/// conforming.
/// </para>
/// <para>
/// The HTTP status line, the <c>benzene-status</c> header and the result's response headers are
/// unchanged. On HTTP, <see cref="ProblemDetails.Status"/> is already filled in when
/// <see cref="Format"/> runs.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The transport context type this formatter applies to.</typeparam>
public interface IProblemFormatter<TContext>
{
    /// <summary>
    /// The content type of the body <see cref="Format"/> returns, e.g. <c>application/json</c>. It
    /// replaces <c>application/problem+json</c>, which would tell a client to read the body as a problem
    /// document.
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// Whether this formatter writes the body for <paramref name="result"/>. When it returns
    /// <c>false</c>, the problem document is written as usual. The default applies to every failure.
    /// </summary>
    /// <param name="context">The transport context for the current message.</param>
    /// <param name="result">The failed result.</param>
    bool Applies(TContext context, IBenzeneResult result) => true;

    /// <summary>
    /// The body to write. A <see cref="string"/> is written as it is; any other object is serialized
    /// with the message's negotiated serializer, by its runtime type.
    /// </summary>
    /// <param name="context">The transport context for the current message.</param>
    /// <param name="problem">The problem document Benzene would otherwise have written.</param>
    /// <param name="result">The failed result.</param>
    object Format(TContext context, ProblemDetails problem, IBenzeneResult result);
}
