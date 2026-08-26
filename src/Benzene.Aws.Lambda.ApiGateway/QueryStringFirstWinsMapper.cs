using System.Collections.Generic;
using System.Linq;
using Amazon.Lambda.APIGatewayEvents;

namespace Benzene.Aws.Lambda.ApiGateway;

/// <summary>
/// Picks the FIRST value per key for a repeated query-string parameter, matching
/// <c>AspNetRequestEnricher</c>'s first-wins policy (<c>Query.ToDictionary(x =&gt; x.Key, x =&gt;
/// x.Value.First())</c>) - so <c>?status=active&amp;status=inactive</c> binds identically across
/// transports for the identical route/handler, rather than diverging between "first wins" (ASP.NET
/// Core) and "last wins" (the raw API Gateway payload).
/// </summary>
internal static class QueryStringFirstWinsMapper
{
    /// <summary>
    /// API Gateway REST API (v1, payload format 1.0) proxy requests carry the query string two ways:
    /// <see cref="APIGatewayProxyRequest.QueryStringParameters"/> (single value per key - the AWS SDK
    /// keeps only the LAST occurrence of a repeated key) and
    /// <see cref="APIGatewayProxyRequest.MultiValueQueryStringParameters"/> (every value, in the order
    /// they appeared on the wire). Prefer the multi-value map and take the FIRST value per key; fall
    /// back to the single-value map when the multi-value one is absent (e.g. a hand-built payload -
    /// tests included - that only sets the single-value field, or a genuinely query-less request).
    /// </summary>
    /// <param name="request">The v1 proxy request to read the query string from.</param>
    /// <returns>A single-value-per-key query-string dictionary, first-value-wins for a repeated key.</returns>
    public static IDictionary<string, string> ForV1(APIGatewayProxyRequest request)
    {
        return request.MultiValueQueryStringParameters?
                   .ToDictionary(x => x.Key, x => x.Value?.FirstOrDefault())
               ?? request.QueryStringParameters;
    }

    /// <summary>
    /// API Gateway HTTP API (v2, payload format 2.0) requests carry no multi-value map - AWS itself
    /// joins repeated values for the same key into one comma-separated string in
    /// <c>QueryStringParameters</c> before Lambda ever sees the event (AWS's documented v2 encoding).
    /// Take the FIRST comma-separated segment per key.
    /// </summary>
    /// <param name="queryStringParameters">The v2 request's already-comma-joined query-string dictionary, or null.</param>
    /// <returns>A single-value-per-key query-string dictionary, first-value-wins for a repeated key.</returns>
    public static IDictionary<string, string> ForV2(IDictionary<string, string> queryStringParameters)
    {
        return queryStringParameters?.ToDictionary(x => x.Key, x => x.Value?.Split(',')[0]);
    }
}
