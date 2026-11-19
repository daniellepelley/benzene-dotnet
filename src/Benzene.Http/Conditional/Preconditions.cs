using Benzene.Abstractions.Results;
using Benzene.Results;

namespace Benzene.Http.Conditional;

/// <summary>
/// Optimistic concurrency for a handler: checks the request's <c>If-Match</c> against the resource's
/// current entity tag before it changes anything.
/// </summary>
/// <example>
/// <code>
/// var failed = Preconditions.CheckIfMatch&lt;TaskDto&gt;(request.IfMatch, EntityTags.Strong(task.Version));
/// if (failed != null) return failed;   // 428 with no If-Match, 412 with a stale one
/// </code>
/// A request property named <c>IfMatch</c> receives the <c>If-Match</c> header on every HTTP host
/// (<see cref="ConditionalRequestEnricher{TContext}"/>).
/// </example>
public static class Preconditions
{
    /// <summary>
    /// Returns <c>null</c> when <paramref name="ifMatch"/> matches <paramref name="currentETag"/>
    /// (strong comparison, <c>*</c> matching any). Otherwise returns the failure to answer with:
    /// <c>precondition-required</c> (428) when there is no <c>If-Match</c> and <paramref name="required"/>
    /// is true, else <c>precondition-failed</c> (412). With <paramref name="required"/> false, a request
    /// without <c>If-Match</c> passes.
    /// </summary>
    public static IBenzeneResult<T>? CheckIfMatch<T>(string? ifMatch, string currentETag, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return required
                ? BenzeneResult.PreconditionRequired<T>("This operation requires an If-Match header with the resource's current ETag.")
                : null;
        }

        return EntityTags.IfMatch(ifMatch, currentETag)
            ? null
            : BenzeneResult.PreconditionFailed<T>("The resource has changed since the ETag in If-Match was issued.");
    }
}
