using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Clients.Common
{
    /// <summary>Extension methods mapping a <see cref="BenzeneMessageClientResponse"/> onto an <see cref="IBenzeneResult{T}"/>.</summary>
    public static class BenzeneResultExtensions
    {
        /// <summary>
        /// Maps a <see cref="BenzeneMessageClientResponse"/> to an <see cref="IBenzeneResult{T}"/>.
        /// The response's status code may be a raw Benzene result status (the standard envelope
        /// contract, preserved verbatim - including an application-defined status, which now
        /// round-trips instead of being coerced to <c>unexpected-error</c>) or a numeric HTTP status
        /// code (older or HTTP-shaped services, mapped to its Benzene equivalent). Failure bodies are
        /// read as an RFC 9457 problem document (<see cref="ProblemDetails"/>): when its
        /// <see cref="ProblemDetails.Errors"/> member is present and non-empty, the result's
        /// structured errors are populated from it directly - field/code and all, and in order -
        /// closing the defect where a multi-error failure used to round-trip as a single joined
        /// <see cref="ProblemDetails.Detail"/> string (work/benzene-result-errors-ruling.md §5.2,
        /// Phase 5 of work/archive/problem-details-plan-2026-08.md). When <c>Errors</c> is absent (an older producer
        /// still emitting only <c>{ status, detail }</c>), this falls back to a single message-only
        /// error built from <see cref="ProblemDetails.Detail"/> - unchanged behavior. Either way the
        /// received document is attached to the result (via <see cref="ProblemDocuments.ToFailedResult{T}(string, string, ISerializer)"/>)
        /// so <c>result.GetProblem()</c> (<c>Benzene.Results</c>) returns exactly what was received,
        /// not a synthesized document.
        /// </summary>
        /// <remarks>
        /// Success/failure is <see cref="BenzeneMessageClientResponse.IsSuccessful"/> - the wire's one
        /// success signal (wire-contracts.md §1.2), which a receiver takes as authoritative and never
        /// re-derives from the status text. An envelope that omits it is malformed and is treated as
        /// <b>unsuccessful</b> (spec-0.2.0): classifying it from <c>statusCode</c> text instead would
        /// misread any application-defined status. A failure body that does not read as a problem document
        /// (not JSON, or not an object) leaves the envelope's failed status with no errors rather than
        /// throwing - the envelope, not the body, is the outcome.
        /// </remarks>
        public static IBenzeneResult<T> AsBenzeneResult<T>(this BenzeneMessageClientResponse source, ISerializer serializer)
        {
            var status = BenzeneResultHttpMapper.NormalizeStatus(source.StatusCode);
            if (status == null)
            {
                return BenzeneResult.UnexpectedError<T>($"Status code {source.StatusCode} not mapped");
            }

            // wire-contracts §1.2: no isSuccessful = a malformed envelope = unsuccessful.
            var isSuccessful = source.IsSuccessful ?? false;

            if (source.Body == null)
            {
                return BenzeneResult.Set<T>(status, isSuccessful);
            }

            return typeof(T) == typeof(Guid)
                ? ReturnGuidResult<T>(status, isSuccessful, source.Body, serializer)
                : ReturnObjectResult<T>(status, isSuccessful, source.Body, serializer);
        }

        private static IBenzeneResult<T> ReturnObjectResult<T>(string status, bool isSuccessful, string body, ISerializer serializer)
        {
            if (isSuccessful)
            {
                return BenzeneResult.Set(status, serializer.Deserialize<T>(body), true);
            }

            return ProblemDocuments.ToFailedResult<T>(status, body, serializer);
        }

        private static IBenzeneResult<T> ReturnGuidResult<T>(string status, bool isSuccessful, string body, ISerializer serializer)
        {
            if (isSuccessful)
            {
                return (IBenzeneResult<T>)BenzeneResult.Set(status, ParseGuid(body, serializer), true);
            }

            return ProblemDocuments.ToFailedResult<T>(status, body, serializer);
        }

        private static Guid ParseGuid(string body, ISerializer serializer)
        {
            var successfullyParsed = Guid.TryParse(serializer.Deserialize<string>(body), out var parsedGuid);

            if (!successfullyParsed)
            {
                parsedGuid = Guid.Empty;
            }

            return parsedGuid;
        }
    }
}
