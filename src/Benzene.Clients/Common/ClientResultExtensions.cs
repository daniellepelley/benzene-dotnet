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
        /// read as the standard error payload (<c>{ "status": ..., "detail": ... }</c>).
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

            var errorPayload = serializer.Deserialize<ErrorPayload>(body);
            return string.IsNullOrEmpty(errorPayload?.Detail)
                ? BenzeneResult.Set<T>(status, false)
                : BenzeneResult.SetFailed<T>(status, errorPayload.Detail);
        }

        private static IBenzeneResult<T> ReturnGuidResult<T>(string status, bool isSuccessful, string body, ISerializer serializer)
        {
            if (isSuccessful)
            {
                return (IBenzeneResult<T>)BenzeneResult.Set(status, ParseGuid(body, serializer), true);
            }

            var errorPayload = serializer.Deserialize<ErrorPayload>(body);
            return string.IsNullOrEmpty(errorPayload?.Detail)
                ? BenzeneResult.Set<T>(status, false)
                : BenzeneResult.SetFailed<T>(status, errorPayload.Detail);
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
