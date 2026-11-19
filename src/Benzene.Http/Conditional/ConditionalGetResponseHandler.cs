using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Results;

namespace Benzene.Http.Conditional;

/// <summary>
/// The HTTP binding's conditional-<c>GET</c> rule (spec 1.3, wire-contracts §4.1): a successful result
/// to a <c>GET</c> or <c>HEAD</c> that carries an <c>ETag</c> response header
/// (<c>WithHeader("ETag", …)</c>) answers <b>304 Not Modified</b>, with no body, when the request's
/// <c>If-None-Match</c> matches it (weak comparison, a list, or <c>*</c>). 304 is not a Benzene status:
/// the result stays <c>ok</c>, and only the HTTP response changes. Registered last on every HTTP host,
/// after the result's headers are written, so the 304 keeps them, the <c>ETag</c> included.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
public class ConditionalGetResponseHandler<TContext> : IResponseHandler<TContext> where TContext : IHttpContext
{
    private readonly IHttpRequestAdapter<TContext> _requestAdapter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;

    /// <summary>Creates the handler.</summary>
    public ConditionalGetResponseHandler(IHttpRequestAdapter<TContext> requestAdapter, IBenzeneResponseAdapter<TContext> responseAdapter)
    {
        _requestAdapter = requestAdapter;
        _responseAdapter = responseAdapter;
    }

    /// <inheritdoc />
    public ValueTask HandleAsync(TContext context, IMessageHandlerResult messageHandlerResult)
    {
        var result = messageHandlerResult.BenzeneResult;
        if (!result.IsSuccessful || !result.GetResponseHeaders().TryGetValue("ETag", out var etag))
        {
            return ValueTask.CompletedTask;
        }

        var request = _requestAdapter.Map(context);
        var method = request.Method;
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.CompletedTask;
        }

        string? ifNoneMatch = null;
        foreach (var (key, value) in request.Headers ?? new Dictionary<string, string>())
        {
            if (string.Equals(key, "if-none-match", StringComparison.OrdinalIgnoreCase))
            {
                ifNoneMatch = value;
                break;
            }
        }

        if (EntityTags.IfNoneMatch(ifNoneMatch, etag))
        {
            _responseAdapter.SetStatusCode(context, "304");
            _responseAdapter.SetBody(context, string.Empty);
        }

        return ValueTask.CompletedTask;
    }
}
