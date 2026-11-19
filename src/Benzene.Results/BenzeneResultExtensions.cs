using System.Linq;
using System.Net;
using Benzene.Abstractions.Results;

namespace Benzene.Results;

public static class BenzeneResultExtensions
{
    /// <summary>
    /// Projects a result's structured <see cref="IBenzeneResult.Errors"/> down to their message text -
    /// for callers that want the pre-<see cref="BenzeneError"/> <c>string[]</c> shape back (e.g. an
    /// older log statement or comparison that only cares about the text).
    /// </summary>
    public static string[] ErrorMessages(this IBenzeneResult result)
    {
        return result.Errors.Select(e => e.Message).ToArray();
    }

    /// <summary>
    /// Returns <paramref name="result"/>'s problem document - a total function, always non-<c>null</c>.
    /// </summary>
    /// <remarks>
    /// Two cases, and the caller can't (and shouldn't need to) tell them apart from the return value
    /// alone:
    /// <list type="bullet">
    /// <item><b>Received</b> - <paramref name="result"/>'s concrete type implements
    /// <see cref="IHasProblemDetails"/> and has a non-<c>null</c> <see cref="IHasProblemDetails.Problem"/>
    /// (a handler-authored problem built via <see cref="BenzeneResult.Problem{T}"/>, or a document a
    /// Benzene client actually deserialized off a failure response,
    /// <c>Benzene.Clients.Common.ClientResultExtensions</c>): that document is returned verbatim.</item>
    /// <item><b>Synthesized</b> - every other result (the overwhelming majority - any ordinary failure
    /// built with <see cref="BenzeneResult.NotFound{T}(string[])"/>/<c>.ValidationError(...)</c>/etc.):
    /// a document is built on the spot via <see cref="ProblemTypes.From"/> from the result's status and
    /// errors. Calling this twice on the same synthesized-path result returns two distinct
    /// <see cref="ProblemDetails"/> instances with equal content, not the same reference.</item>
    /// </list>
    /// There is no <c>TryGetProblem</c> twin - <c>null</c> never escapes this method, by design, so
    /// every caller can treat "does this result have a problem" as "call <see cref="GetProblem"/>",
    /// full stop.
    /// </remarks>
    public static ProblemDetails GetProblem(this IBenzeneResult result)
    {
        if (result is IHasProblemDetails { Problem: { } problem })
        {
            return problem;
        }

        return ProblemTypes.From(result);
    }

    /// <summary>
    /// Returns the response headers attached to <paramref name="result"/> with
    /// <see cref="WithHeader{T}"/>; empty (never <c>null</c>) when there are none.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetResponseHeaders(this IBenzeneResult result)
    {
        return result is IHasResponseHeaders { ResponseHeaders: { } headers } ? headers : NoResponseHeaders;
    }

    /// <summary>
    /// Returns a copy of <paramref name="result"/> that also carries the response header
    /// <paramref name="name"/>: <c>BenzeneResult.Created(task).WithHeader("Location", $"/tasks/{task.Id}")</c>.
    /// The result itself is unchanged. A second call for the same name (ignoring case) replaces the
    /// first. An HTTP transport writes the header after rendering the response, unless the pipeline
    /// already set it (CORS, <c>content-type</c>); a transport with no response headers ignores it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not an HTTP token, or
    /// <paramref name="name"/> or <paramref name="value"/> contains CR, LF or NUL (which would split the
    /// header on the wire).</exception>
    public static IBenzeneResult<T> WithHeader<T>(this IBenzeneResult<T> result, string name, string value)
    {
        return WithHeaders(result, new[] { new KeyValuePair<string, string>(name, value) });
    }

    /// <summary>The non-generic counterpart of <see cref="WithHeader{T}"/>.</summary>
    public static IBenzeneResult WithHeader(this IBenzeneResult result, string name, string value)
    {
        return WithHeaders(result, new[] { new KeyValuePair<string, string>(name, value) });
    }

    /// <summary>
    /// Returns a copy of <paramref name="result"/> that also carries every header in
    /// <paramref name="headers"/>, applied in order; see <see cref="WithHeader{T}"/>.
    /// </summary>
    public static IBenzeneResult<T> WithHeaders<T>(this IBenzeneResult<T> result, IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(result);
        return BenzeneResult.Copy(result, AttachedProblem(result), MergeHeaders(result, headers));
    }

    /// <summary>The non-generic counterpart of <see cref="WithHeaders{T}"/>.</summary>
    public static IBenzeneResult WithHeaders(this IBenzeneResult result, IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(result);
        return BenzeneResult.Copy(result, AttachedProblem(result), MergeHeaders(result, headers));
    }

    private static readonly IReadOnlyDictionary<string, string> NoResponseHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static ProblemDetails? AttachedProblem(IBenzeneResult result)
    {
        return result is IHasProblemDetails hasProblem ? hasProblem.Problem : null;
    }

    private static IReadOnlyDictionary<string, string> MergeHeaders(IBenzeneResult result, IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var merged = new Dictionary<string, string>(result.GetResponseHeaders(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            ValidateHeader(name, value);
            merged[name] = value;
        }

        return merged;
    }

    // RFC 9110 §5.6.2: a field name is a token. A CR, LF or NUL in the value would let a handler (or
    // whatever fed it the value) split the header on the wire, whatever the host does with it.
    private static void ValidateHeader(string name, string value)
    {
        if (string.IsNullOrEmpty(name) || !name.All(IsTokenChar))
        {
            throw new ArgumentException($"'{name}' is not a valid HTTP header name.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException($"The value of header '{name}' contains CR, LF or NUL.", nameof(value));
        }
    }

    private static bool IsTokenChar(char c)
    {
        return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }

    public static bool IsAccepted(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Accepted;
    }
    /// <summary>
    /// True when the result's status is in the success class (<c>Ok</c>, <c>Created</c>,
    /// <c>Accepted</c>, <c>Updated</c>, <c>Deleted</c>, <c>Ignored</c>) — the same classification
    /// as <see cref="IBenzeneResult.IsSuccessful"/> and <see cref="BenzeneResultStatus.IsSuccess"/>.
    /// Use <see cref="IsOk"/> for the narrower "status is exactly <c>Ok</c>" check.
    /// </summary>
    public static bool IsSuccess(this IBenzeneResult serviceBenzeneResult)
    {
        return BenzeneResultStatus.IsSuccess(serviceBenzeneResult.Status);
    }
    public static bool IsOk(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Ok;
    }

    public static bool IsCreated(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Created;
    }
    public static bool IsUpdated(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Updated;
    }
    public static bool IsDeleted(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Deleted;
    }
    public static bool IsIgnored(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Ignored;
    }
    public static bool IsNotFound(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.NotFound;
    }
    public static bool IsBadRequest(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.BadRequest;
    }
    public static bool IsValidationError(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.ValidationError;
    }
    public static bool IsServiceUnavailable(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.ServiceUnavailable;
    }
    public static bool IsNotImplemented(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.NotImplemented;
    }
    public static bool IsUnexpectedError(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.UnexpectedError;
    }
    public static bool IsConflict(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Conflict;
    }
    public static bool IsForbidden(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Forbidden;
    }
    public static bool IsUnauthorized(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Unauthorized;
    }
    public static bool IsTooManyRequests(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.TooManyRequests;
    }
    public static bool IsTimeout(this IBenzeneResult serviceBenzeneResult)
    {
        return serviceBenzeneResult.Status == BenzeneResultStatus.Timeout;
    }

    /// <summary>
    /// True when the result's status describes a transient condition — one where a later retry
    /// may succeed. See <see cref="BenzeneResultStatus.IsTransient"/> for the retry-safety caveat
    /// on <see cref="BenzeneResultStatus.Timeout"/>.
    /// </summary>
    public static bool IsTransient(this IBenzeneResult serviceBenzeneResult)
    {
        return BenzeneResultStatus.IsTransient(serviceBenzeneResult.Status);
    }

    public static IBenzeneResult<TOutput> As<T, TOutput>(this IBenzeneResult<T> serviceBenzeneResult)
    {
        return As<TOutput>(serviceBenzeneResult);
    }

    public static IBenzeneResult<TOutput> As<TOutput>(this IBenzeneResult serviceBenzeneResult)
    {
        return CarryOver(serviceBenzeneResult, serviceBenzeneResult.IsSuccessful
           // Set<TOutput>(status, true) → the (string, bool) overload. A single-string
           // Set<TOutput>(status) has no matching overload and silently bound to
           // Set<TOutput>(string, params string[]) with zero errors — the FAILURE constructor — so a
           // projected success came back IsSuccessful=false (a 200 carrying an error body downstream,
           // and misread as a failure by saga/idempotency/SQS batch escalation).
           ? BenzeneResult.Set<TOutput>(serviceBenzeneResult.Status, true)
           // The structured (IReadOnlyList<BenzeneError>) overload, not SetFailed, so a re-wrap keeps
           // each error's Field/Code intact instead of collapsing to message-only text.
           : BenzeneResult.Set<TOutput>(serviceBenzeneResult.Status, serviceBenzeneResult.Errors));
    }

    public static IBenzeneResult<TOutput> As<T, TOutput>(this IBenzeneResult<T> serviceBenzeneResult,
        Func<T?, TOutput> map)
    {
        return CarryOver(serviceBenzeneResult, serviceBenzeneResult.IsSuccessful
           ? BenzeneResult.Set(serviceBenzeneResult.Status, map(serviceBenzeneResult.Payload), true)
           : BenzeneResult.Set<TOutput>(serviceBenzeneResult.Status, serviceBenzeneResult.Errors));
    }

    // A re-wrap keeps the attached problem document and response headers, which Set alone would drop.
    private static IBenzeneResult<TOutput> CarryOver<TOutput>(IBenzeneResult source, IBenzeneResult<TOutput> target)
    {
        var problem = AttachedProblem(source);
        var headers = source.GetResponseHeaders();
        return problem == null && headers.Count == 0 ? target : BenzeneResult.Copy(target, problem, headers);
    }

    /// <summary>
    /// Re-wraps <paramref name="value"/> under the original result's status, preserving its success
    /// class exactly (<see cref="IBenzeneResult.IsSuccessful"/>) rather than re-deriving it from the
    /// status text — required for an application-defined status, which the derive-from-status overload
    /// can't classify at all.
    /// </summary>
    public static IBenzeneResult<TOutput> As<TOutput>(this IBenzeneResult serviceBenzeneResult, TOutput value)
    {
        return CarryOver(serviceBenzeneResult, BenzeneResult.Set(serviceBenzeneResult.Status, value, serviceBenzeneResult.IsSuccessful));
    }

    public static async Task<IBenzeneResult<TOutput>> As<T, TOutput>(this Task<IBenzeneResult<T>> source,
        Func<T?, TOutput> map)
    {
        await source;
        return source.Result.As(map);
    }

    public static async Task<IBenzeneResult<TOutput>> As<TOutput>(this Task<IBenzeneResult> source,
        TOutput value)
    {
        await source;
        return source.Result.As(value);
    }

    public static async Task<IBenzeneResult<TOutput>> As<TOutput>(this Task<IBenzeneResult> source)
    {
        await source;
        return source.Result.As<TOutput>();
    }

    public static async Task<IBenzeneResult<TOutput>> As<T, TOutput>(this Task<IBenzeneResult<T>> source)
    {
        await source;
        return source.Result.As<T, TOutput>();
    }

    public static Task<IBenzeneResult<T>> AsTask<T>(this IBenzeneResult<T> serviceBenzeneResult)
    {
        return Task.FromResult(serviceBenzeneResult);
    }

    public static IBenzeneResult Convert(this HttpStatusCode httpStatusCode)
    {
        return httpStatusCode switch
        {
            HttpStatusCode.Accepted => BenzeneResult.Accepted(),
            HttpStatusCode.OK => BenzeneResult.Ok(),
            HttpStatusCode.Created => BenzeneResult.Created(),
            HttpStatusCode.NoContent => BenzeneResult.Deleted(),
            HttpStatusCode.NotFound => BenzeneResult.NotFound(),
            HttpStatusCode.BadRequest => BenzeneResult.BadRequest(),
            HttpStatusCode.Conflict => BenzeneResult.Conflict(),
            HttpStatusCode.Forbidden => BenzeneResult.Forbidden(),
            HttpStatusCode.Unauthorized => BenzeneResult.Unauthorized(),
            HttpStatusCode.UnprocessableEntity => BenzeneResult.ValidationError(),
            HttpStatusCode.TooManyRequests => BenzeneResult.TooManyRequests(),
            HttpStatusCode.RequestTimeout => BenzeneResult.Timeout(),
            HttpStatusCode.GatewayTimeout => BenzeneResult.Timeout(),
            HttpStatusCode.NotImplemented => BenzeneResult.NotImplemented(),
            HttpStatusCode.BadGateway => BenzeneResult.ServiceUnavailable(),
            HttpStatusCode.ServiceUnavailable => BenzeneResult.ServiceUnavailable(),
            _ => BenzeneResult.UnexpectedError()
        };
    }

    public static IBenzeneResult<T> Convert<T>(this HttpStatusCode httpStatusCode, T payload)
    {
        return httpStatusCode switch
        {
            HttpStatusCode.Accepted => BenzeneResult.Accepted(payload),
            HttpStatusCode.OK => BenzeneResult.Ok(payload),
            HttpStatusCode.Created => BenzeneResult.Created(payload),
            HttpStatusCode.NoContent => BenzeneResult.Deleted(payload),
            HttpStatusCode.NotFound => BenzeneResult.NotFound<T>(),
            HttpStatusCode.BadRequest => BenzeneResult.BadRequest<T>(),
            HttpStatusCode.Conflict => BenzeneResult.Conflict<T>(),
            HttpStatusCode.Forbidden => BenzeneResult.Forbidden<T>(),
            HttpStatusCode.Unauthorized => BenzeneResult.Unauthorized<T>(),
            HttpStatusCode.UnprocessableEntity => BenzeneResult.ValidationError<T>(),
            HttpStatusCode.TooManyRequests => BenzeneResult.TooManyRequests<T>(),
            HttpStatusCode.RequestTimeout => BenzeneResult.Timeout<T>(),
            HttpStatusCode.GatewayTimeout => BenzeneResult.Timeout<T>(),
            HttpStatusCode.NotImplemented => BenzeneResult.NotImplemented<T>(),
            HttpStatusCode.BadGateway => BenzeneResult.ServiceUnavailable<T>(),
            HttpStatusCode.ServiceUnavailable => BenzeneResult.ServiceUnavailable<T>(),
            _ => BenzeneResult.UnexpectedError<T>()
        };
    }

    public static IBenzeneResult<T> Convert<T>(this HttpStatusCode httpStatusCode)
    {
        return httpStatusCode switch
        {
            HttpStatusCode.Accepted => BenzeneResult.Accepted<T>(),
            HttpStatusCode.OK => BenzeneResult.Ok<T>(),
            HttpStatusCode.Created => BenzeneResult.Created<T>(),
            HttpStatusCode.NoContent => BenzeneResult.Deleted<T>(),
            HttpStatusCode.NotFound => BenzeneResult.NotFound<T>(),
            HttpStatusCode.BadRequest => BenzeneResult.BadRequest<T>(),
            HttpStatusCode.Conflict => BenzeneResult.Conflict<T>(),
            HttpStatusCode.Forbidden => BenzeneResult.Forbidden<T>(),
            HttpStatusCode.Unauthorized => BenzeneResult.Unauthorized<T>(),
            HttpStatusCode.UnprocessableEntity => BenzeneResult.ValidationError<T>(),
            HttpStatusCode.TooManyRequests => BenzeneResult.TooManyRequests<T>(),
            HttpStatusCode.RequestTimeout => BenzeneResult.Timeout<T>(),
            HttpStatusCode.GatewayTimeout => BenzeneResult.Timeout<T>(),
            HttpStatusCode.NotImplemented => BenzeneResult.NotImplemented<T>(),
            HttpStatusCode.BadGateway => BenzeneResult.ServiceUnavailable<T>(),
            HttpStatusCode.ServiceUnavailable => BenzeneResult.ServiceUnavailable<T>(),
            _ => BenzeneResult.UnexpectedError<T>()
        };
    }
}
