using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Core.Helper;
using Benzene.Http;
using Benzene.Http.Routing;

namespace Benzene.AspNet.Core;

/// <summary>
/// Enriches a message handler's request with values from the HTTP query string, configured headers, and
/// route parameters matched against the request's method and path.
/// </summary>
public class AspNetRequestEnricher : IRequestEnricher<AspNetContext>
{
    private readonly IRouteFinder _routeFinder;
    private readonly AspNetHeadersToBodyGetter _headersToBodyGetter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AspNetRequestEnricher"/> class.
    /// </summary>
    /// <param name="routeFinder">The route finder used to match the request's method and path to a route.</param>
    /// <param name="httpHeaderMappings">The configured header name mappings used to extract headers into the enrichment values.</param>
    public AspNetRequestEnricher(IRouteFinder routeFinder, IHttpHeaderMappings httpHeaderMappings)
    {
        _headersToBodyGetter = new AspNetHeadersToBodyGetter(httpHeaderMappings);
        _routeFinder = routeFinder;
    }

    /// <summary>
    /// Builds a dictionary of values to enrich the request with, combining the query string, mapped
    /// headers, and matched route parameters (excluding any parameter value that looks like a JSON
    /// object). Returns an empty dictionary if no route matches.
    /// </summary>
    /// <typeparam name="TRequest">The message handler's request type.</typeparam>
    /// <param name="request">The request being enriched (unused; enrichment is derived entirely from the context).</param>
    /// <param name="context">The HTTP context to enrich from.</param>
    /// <returns>The dictionary of enrichment values.</returns>
    public IDictionary<string, object> Enrich<TRequest>(TRequest request, AspNetContext context)
    {
        var route = _routeFinder.Find(context.HttpContext.Request.Method, context.HttpContext.Request.Path);
        var dictionary = new Dictionary<string, object>();

        if (route == null)
        {
            return dictionary;
        }

        // Every value of a repeated query key (e.g. ?status=active&status=inactive): a string for one
        // value, a string[] for several. The binder gives a collection property all of them and a scalar
        // the first - the policy the API Gateway enrichers' QueryStringValuesMapper also follows, so a
        // repeated query key binds identically across transports for the identical route/handler.
        DictionaryUtils.MapOnto(dictionary, context.HttpContext.Request.Query?.ToDictionary(x => x.Key, x => QueryValue(x.Value)));
        DictionaryUtils.MapOnto(dictionary, _headersToBodyGetter.GetHeaders(context));
        DictionaryUtils.MapOnto(dictionary, CleanUp(route.Parameters));

        return dictionary;
    }

    // One value stays a string (what every enricher has always emitted); several become a string[] in
    // request order, which DictionaryUtils.Enrich binds to a collection property or, for a scalar, takes
    // the first of.
    private static object QueryValue(global::Microsoft.Extensions.Primitives.StringValues values)
    {
        return values.Count > 1 ? values.ToArray() : values.ToString();
    }

    private static IDictionary<string, object> CleanUp(IDictionary<string, object> source)
    {
        return source
            .Where(x => !x.Value.ToString()!.StartsWith("{"))
            .ToDictionary(x => x.Key, x => x.Value);
    }
}
