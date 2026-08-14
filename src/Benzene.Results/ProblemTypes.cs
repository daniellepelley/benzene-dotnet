using System.Collections.Generic;
using System.Linq;
using Benzene.Abstractions.Results;

namespace Benzene.Results;

/// <summary>
/// The problem-type registry: <c>type</c> URI / <c>title</c> / HTTP status keyed by the existing
/// <see cref="BenzeneResultStatus"/> failure vocabulary - no new taxonomy (see
/// <c>docs/specification/wire-contracts.md</c> §3.1 in the cross-language Benzene spec repo). A
/// pure, static, spec-pinned-table type, matching <see cref="BenzeneResultStatus"/>'s own pattern.
/// </summary>
public static class ProblemTypes
{
    private const string BaseUri = "https://benzene.app/problems/";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.BadRequest"/>.</summary>
    public const string BadRequest = BaseUri + "bad-request";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.Unauthorized"/>.</summary>
    public const string Unauthorized = BaseUri + "unauthorized";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.Forbidden"/>.</summary>
    public const string Forbidden = BaseUri + "forbidden";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.NotFound"/>.</summary>
    public const string NotFound = BaseUri + "not-found";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.Conflict"/>.</summary>
    public const string Conflict = BaseUri + "conflict";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.ValidationError"/>.</summary>
    public const string ValidationError = BaseUri + "validation-error";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.TooManyRequests"/>.</summary>
    public const string TooManyRequests = BaseUri + "too-many-requests";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.UnexpectedError"/>.</summary>
    public const string UnexpectedError = BaseUri + "unexpected-error";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.NotImplemented"/>.</summary>
    public const string NotImplemented = BaseUri + "not-implemented";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.ServiceUnavailable"/>.</summary>
    public const string ServiceUnavailable = BaseUri + "service-unavailable";

    /// <summary>The <see cref="ProblemDetails.Type"/> URI for <see cref="BenzeneResultStatus.Timeout"/>.</summary>
    public const string Timeout = BaseUri + "timeout";

    private const int UnknownHttpStatus = 500;

    private sealed record RegistryRow(string Type, string Title, int HttpStatus);

    // One row per failure status (docs/specification/wire-contracts.md §3.1). Success statuses have
    // no problem type - problems exist only on failure - so they're deliberately absent here.
    private static readonly IReadOnlyDictionary<string, RegistryRow> Registry = new Dictionary<string, RegistryRow>
    {
        [BenzeneResultStatus.BadRequest] = new(BadRequest, "Bad request", 400),
        [BenzeneResultStatus.Unauthorized] = new(Unauthorized, "Unauthorized", 401),
        [BenzeneResultStatus.Forbidden] = new(Forbidden, "Forbidden", 403),
        [BenzeneResultStatus.NotFound] = new(NotFound, "Not found", 404),
        [BenzeneResultStatus.Conflict] = new(Conflict, "Conflict", 409),
        [BenzeneResultStatus.ValidationError] = new(ValidationError, "Validation failed", 422),
        [BenzeneResultStatus.TooManyRequests] = new(TooManyRequests, "Too many requests", 429),
        [BenzeneResultStatus.UnexpectedError] = new(UnexpectedError, "Unexpected error", 500),
        [BenzeneResultStatus.NotImplemented] = new(NotImplemented, "Not implemented", 501),
        [BenzeneResultStatus.ServiceUnavailable] = new(ServiceUnavailable, "Service unavailable", 503),
        [BenzeneResultStatus.Timeout] = new(Timeout, "Timeout", 504),
    };

    /// <summary>
    /// The registry <see cref="ProblemDetails.Type"/> URI for <paramref name="status"/>, or
    /// <c>null</c> for an application-defined status the registry doesn't know (the application owns
    /// its own <c>type</c>, or omits one).
    /// </summary>
    public static string? TypeFor(string? status)
    {
        return status != null && Registry.TryGetValue(status, out var row) ? row.Type : null;
    }

    /// <summary>
    /// The registry <see cref="ProblemDetails.Title"/> for <paramref name="status"/>, or <c>null</c>
    /// for an application-defined status the registry doesn't know.
    /// </summary>
    public static string? TitleFor(string? status)
    {
        return status != null && Registry.TryGetValue(status, out var row) ? row.Title : null;
    }

    /// <summary>
    /// The HTTP status code the registry maps <paramref name="status"/> to, or <c>500</c> for an
    /// application-defined status - the same "unknown status falls to the generic-error row" rule
    /// <see cref="BenzeneResultStatus"/>-based HTTP mappings use elsewhere (e.g.
    /// <c>DefaultHttpStatusCodeMapper</c>).
    /// </summary>
    public static int HttpStatusFor(string? status)
    {
        return status != null && Registry.TryGetValue(status, out var row) ? row.HttpStatus : UnknownHttpStatus;
    }

    /// <summary>
    /// Builds the problem document for a failed <paramref name="result"/>: <see cref="ProblemDetails.Type"/>/
    /// <see cref="ProblemDetails.Title"/> from the registry (both <c>null</c> for an application-defined
    /// status), <see cref="ProblemDetails.Detail"/> as the result's error messages joined with
    /// <c>", "</c> (unchanged from the retired <c>ErrorPayload</c>'s construction),
    /// <see cref="ProblemDetails.BenzeneStatus"/> as the result's status, and
    /// <see cref="ProblemDetails.Errors"/> as the result's structured errors when non-empty (else
    /// <c>null</c>, so the member is omitted from the wire). Deliberately never sets
    /// <see cref="ProblemDetails.Status"/> - the numeric HTTP status is an HTTP-binding concern, filled
    /// in by the HTTP-aware response mapper, not this transport-neutral factory.
    /// </summary>
    /// <param name="result">The unsuccessful result to build a problem document for.</param>
    public static ProblemDetails From(IBenzeneResult result)
    {
        var detail = string.Join(", ", result.ErrorMessages());

        return new ProblemDetails
        {
            Type = TypeFor(result.Status),
            Title = TitleFor(result.Status),
            Detail = string.IsNullOrEmpty(detail) ? null : detail,
            BenzeneStatus = result.Status,
            Errors = result.Errors.Count > 0 ? result.Errors.ToArray() : null,
        };
    }
}
