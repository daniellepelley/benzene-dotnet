using Benzene.Abstractions.MessageHandlers.Request;

namespace Benzene.Http.Conditional;

/// <summary>
/// Binds the conditional-request headers onto the request: <c>If-Match</c> onto a property named
/// <c>IfMatch</c>, <c>If-None-Match</c> onto <c>IfNoneMatch</c>, so a handler can check preconditions
/// (<see cref="Preconditions"/>) with no header mapping to configure. Registered on every HTTP host
/// after the transport's own enricher, which therefore wins for any value it also supplies.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
public class ConditionalRequestEnricher<TContext> : IRequestEnricher<TContext> where TContext : IHttpContext
{
    private readonly IHttpRequestAdapter<TContext> _requestAdapter;

    /// <summary>Creates the enricher.</summary>
    public ConditionalRequestEnricher(IHttpRequestAdapter<TContext> requestAdapter)
    {
        _requestAdapter = requestAdapter;
    }

    /// <inheritdoc />
    public IDictionary<string, object> Enrich<TRequest>(TRequest request, TContext context)
    {
        var values = new Dictionary<string, object>();
        foreach (var (key, value) in _requestAdapter.Map(context).Headers ?? new Dictionary<string, string>())
        {
            if (string.Equals(key, "if-match", StringComparison.OrdinalIgnoreCase))
            {
                values["ifMatch"] = value;
            }
            else if (string.Equals(key, "if-none-match", StringComparison.OrdinalIgnoreCase))
            {
                values["ifNoneMatch"] = value;
            }
        }

        return values;
    }
}
