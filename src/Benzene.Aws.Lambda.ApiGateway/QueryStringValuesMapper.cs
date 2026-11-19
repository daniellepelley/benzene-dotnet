using System.Collections.Generic;
using System.Linq;
using Amazon.Lambda.APIGatewayEvents;

namespace Benzene.Aws.Lambda.ApiGateway;

/// <summary>
/// Reads every value of each query-string key, the way <c>AspNetRequestEnricher</c> does, so
/// <c>?status=active&amp;status=inactive</c> binds identically across transports for the identical
/// route/handler. A key with one value maps to a <c>string</c>; a repeated key maps to a
/// <c>string[]</c> in request order. The binder (<c>DictionaryUtils.Enrich</c>) gives a collection
/// property every value and a scalar property the first, so scalars keep first-value-wins.
/// </summary>
internal static class QueryStringValuesMapper
{
    /// <summary>
    /// API Gateway REST API (v1, payload format 1.0) proxy requests carry the query string two ways:
    /// <see cref="APIGatewayProxyRequest.QueryStringParameters"/> (single value per key - the AWS SDK
    /// keeps only the LAST occurrence of a repeated key) and
    /// <see cref="APIGatewayProxyRequest.MultiValueQueryStringParameters"/> (every value, in the order
    /// they appeared on the wire). Prefer the multi-value map; fall back to the single-value map when
    /// the multi-value one is absent (e.g. a hand-built payload - tests included - that only sets the
    /// single-value field, or a genuinely query-less request).
    /// </summary>
    /// <param name="request">The v1 proxy request to read the query string from.</param>
    /// <returns>The query-string values per key: a string, or a string[] for a repeated key.</returns>
    public static IDictionary<string, object> ForV1(APIGatewayProxyRequest request)
    {
        if (request.MultiValueQueryStringParameters != null)
        {
            return request.MultiValueQueryStringParameters.ToDictionary(x => x.Key, x => FromValues(x.Value));
        }

        return request.QueryStringParameters?.ToDictionary(x => x.Key, x => (object)x.Value);
    }

    /// <summary>
    /// API Gateway HTTP API (v2, payload format 2.0) requests carry no multi-value map - AWS itself
    /// joins repeated values for the same key into one comma-separated string in
    /// <c>QueryStringParameters</c> before Lambda ever sees the event (AWS's documented v2 encoding).
    /// Split it back into its values. A single value that itself contains a comma can't be told apart
    /// from two values; a scalar property still binds its first segment, as it always has.
    /// </summary>
    /// <param name="queryStringParameters">The v2 request's already-comma-joined query-string dictionary, or null.</param>
    /// <returns>The query-string values per key: a string, or a string[] for a repeated key.</returns>
    public static IDictionary<string, object> ForV2(IDictionary<string, string> queryStringParameters)
    {
        return queryStringParameters?.ToDictionary(x => x.Key, x => FromValues(x.Value?.Split(',')));
    }

    private static object FromValues(IList<string> values)
    {
        if (values == null || values.Count == 0)
        {
            return null;
        }

        return values.Count == 1 ? values[0] : values.ToArray();
    }
}
