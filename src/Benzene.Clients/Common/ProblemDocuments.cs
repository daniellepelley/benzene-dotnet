using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Clients.Common
{
    /// <summary>
    /// How a client reads a received problem document (wire-contracts §1.3) back into a failed result -
    /// core-concepts §12, <b>error fidelity</b>: the result's <see cref="IBenzeneResult.Errors"/> are rebuilt
    /// from the document's <c>errors</c> with <c>field</c> and <c>code</c> intact (in order), and the document
    /// itself is attached verbatim so <c>result.GetProblem()</c> (<c>Benzene.Results</c>) returns exactly
    /// what was received, an application's own problem <c>type</c> and extension members included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every client that receives a problem document goes through here: the envelope clients (the failure
    /// envelope's body, via <see cref="BenzeneResultExtensions.AsBenzeneResult{T}"/>) and the plain HTTP
    /// client (an <c>application/problem+json</c> response, <c>Benzene.Clients.Http.HttpContextConverter</c>).
    /// </para>
    /// <para>
    /// The result's status: inside an envelope it is the envelope's <c>statusCode</c> (classification is
    /// envelope-first, wire-contracts §1.2/§1.3 - the document's <c>benzeneStatus</c> mirrors it), so
    /// <see cref="ToFailedResult{T}(string, string, ISerializer)"/> keeps the status it is given. Where no
    /// envelope carries one - a plain HTTP response, or a non-envelope failure an envelope client falls
    /// back on - the status would come from the reverse HTTP table, and there the document's
    /// <c>benzeneStatus</c> wins instead (<see cref="ToFailedResultFromDocumentStatus{T}"/>; wire-contracts
    /// §4.1, spec 1.1), so an application-defined status survives the hop. The document's HTTP
    /// <c>status</c> member is never read for classification.
    /// </para>
    /// </remarks>
    public static class ProblemDocuments
    {
        /// <summary>The RFC 9457 JSON problem document media type, <c>application/problem+json</c>.</summary>
        public const string JsonMediaType = "application/problem+json";

        /// <summary>
        /// Whether <paramref name="mediaType"/> (a content type's media type, without parameters) is
        /// <see cref="JsonMediaType"/>, ignoring case.
        /// </summary>
        /// <param name="mediaType">The media type to test; <c>null</c> is not a problem document.</param>
        public static bool IsJsonProblem(string? mediaType)
        {
            return string.Equals(mediaType?.Trim(), JsonMediaType, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds the failed <paramref name="status"/> result for a received problem document: its
        /// <c>errors</c> become the result's errors (<c>message</c>, <c>field</c>, <c>code</c>, in order),
        /// and the document is attached for <c>GetProblem()</c>. A document without <c>errors</c> yields one
        /// message-only error from <c>detail</c> - never split, since messages contain commas - and none
        /// when <c>detail</c> is missing or empty (wire-contracts §1.3, "Without <c>errors</c>").
        /// </summary>
        /// <typeparam name="T">The result's payload type.</typeparam>
        /// <param name="status">The result status, already classified by the caller.</param>
        /// <param name="body">The received problem document.</param>
        /// <param name="serializer">The serializer that reads the document.</param>
        /// <returns>
        /// The failed result. A body that is empty or does not read as a problem document yields the failed
        /// <paramref name="status"/> with no errors, rather than letting a parse failure replace the status
        /// the caller already established.
        /// </returns>
        public static IBenzeneResult<T> ToFailedResult<T>(string status, string? body, ISerializer serializer)
        {
            return ToFailedResult<T>(status, TryRead(body, serializer));
        }

        /// <summary>
        /// Builds the failed <paramref name="status"/> result for an already-deserialized problem document,
        /// per <see cref="ToFailedResult{T}(string, string, ISerializer)"/>.
        /// </summary>
        /// <typeparam name="T">The result's payload type.</typeparam>
        /// <param name="status">The result status, already classified by the caller.</param>
        /// <param name="problem">The received document; <c>null</c> yields a failed result with no errors.</param>
        public static IBenzeneResult<T> ToFailedResult<T>(string status, ProblemDetails? problem)
        {
            if (problem == null)
            {
                return BenzeneResult.Set<T>(status, false);
            }

            IReadOnlyList<BenzeneError> errors = problem.Errors is { Length: > 0 }
                ? problem.Errors
                : string.IsNullOrEmpty(problem.Detail)
                    ? Array.Empty<BenzeneError>()
                    : new[] { new BenzeneError(problem.Detail) };

            return BenzeneResult.AttachReceivedProblem<T>(status, isSuccessful: false, errors, problem);
        }

        /// <summary>
        /// Builds the failed result for a problem document received <b>outside</b> an envelope - where the
        /// status would otherwise come from the reverse HTTP table: the document's <c>benzeneStatus</c>, when
        /// present and non-empty, is the result's status (wire-contracts §4.1, spec 1.1 - so an
        /// application-defined status survives the hop); otherwise <paramref name="reverseTableStatus"/>.
        /// Errors and the attached document as <see cref="ToFailedResult{T}(string, string, ISerializer)"/>.
        /// </summary>
        /// <typeparam name="T">The result's payload type.</typeparam>
        /// <param name="reverseTableStatus">The status the reverse HTTP table gives the response.</param>
        /// <param name="body">The received problem document.</param>
        /// <param name="serializer">The serializer that reads the document.</param>
        public static IBenzeneResult<T> ToFailedResultFromDocumentStatus<T>(string reverseTableStatus, string? body, ISerializer serializer)
        {
            var problem = TryRead(body, serializer);
            return ToFailedResult<T>(StatusOf(problem, reverseTableStatus), problem);
        }

        /// <summary>
        /// The status a received problem document gives a result where no envelope carries one: its
        /// <c>benzeneStatus</c> when present and non-empty, else <paramref name="reverseTableStatus"/>
        /// (wire-contracts §4.1, spec 1.1).
        /// </summary>
        /// <param name="problem">The received document; <c>null</c> yields <paramref name="reverseTableStatus"/>.</param>
        /// <param name="reverseTableStatus">The status the reverse HTTP table gives the response.</param>
        public static string StatusOf(ProblemDetails? problem, string reverseTableStatus)
        {
            return string.IsNullOrWhiteSpace(problem?.BenzeneStatus) ? reverseTableStatus : problem!.BenzeneStatus!;
        }

        /// <summary>
        /// Reads <paramref name="body"/> as a problem document, or <c>null</c> when it is empty or not one.
        /// </summary>
        /// <param name="body">The received body.</param>
        /// <param name="serializer">The serializer that reads the document.</param>
        public static ProblemDetails? TryRead(string? body, ISerializer serializer)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                return serializer.Deserialize<ProblemDetails>(body);
            }
            catch (Exception)
            {
                // Not a problem document (an HTML page, plain text, a JSON array...): the failure stands
                // on its status alone. The serializer is pluggable, so its exception type is not known here.
                return null;
            }
        }
    }
}
