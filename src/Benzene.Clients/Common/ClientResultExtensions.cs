using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Clients.Common
{
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
        /// received document is attached to the result (via <see cref="BenzeneResult.AttachReceivedProblem{T}"/>)
        /// so <c>result.GetProblem()</c> (<c>Benzene.Results</c>) returns exactly what was received,
        /// not a synthesized document.
        /// </summary>
        /// <remarks>
        /// Success/failure classification prefers <see cref="BenzeneMessageClientResponse.IsSuccessful"/>
        /// (the wire's authoritative signal, wire-contracts.md §1.2) when the sender wrote it. A
        /// <c>null</c> value means the sender is an older .NET service or a language port that hasn't
        /// picked up the <c>isSuccessful</c> field yet, so classification falls back to
        /// <see cref="BenzeneResultHttpMapper.IsSuccessStatus"/> - which only recognizes the framework's
        /// own known statuses, so a custom status from such a sender still classifies as failure
        /// (the historical behavior, since there is no other signal to trust it with).
        /// </remarks>
        public static IBenzeneResult<T> AsBenzeneResult<T>(this BenzeneMessageClientResponse source, ISerializer serializer)
        {
            var status = BenzeneResultHttpMapper.NormalizeStatus(source.StatusCode);
            if (status == null)
            {
                return BenzeneResult.UnexpectedError<T>($"Status code {source.StatusCode} not mapped");
            }

            var isSuccessful = source.IsSuccessful ?? BenzeneResultHttpMapper.IsSuccessStatus(status);

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

            return FailedResultFromProblem<T>(status, serializer.Deserialize<ProblemDetails>(body));
        }

        private static IBenzeneResult<T> ReturnGuidResult<T>(string status, bool isSuccessful, string body, ISerializer serializer)
        {
            if (isSuccessful)
            {
                return (IBenzeneResult<T>)BenzeneResult.Set(status, ParseGuid(body, serializer), true);
            }

            return FailedResultFromProblem<T>(status, serializer.Deserialize<ProblemDetails>(body));
        }

        /// <summary>
        /// Builds the failed <paramref name="status"/> result for a deserialized failure body, per the
        /// rules in <see cref="AsBenzeneResult{T}"/>'s remarks. <paramref name="problem"/> is
        /// <c>null</c> when the body didn't deserialize to anything (e.g. an empty body) - that keeps
        /// the historical no-errors behavior instead of throwing.
        /// </summary>
        private static IBenzeneResult<T> FailedResultFromProblem<T>(string status, ProblemDetails? problem)
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
