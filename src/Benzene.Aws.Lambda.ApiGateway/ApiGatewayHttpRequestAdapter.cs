using System.Collections.Generic;
using System.Linq;
using Benzene.Http;

namespace Benzene.Aws.Lambda.ApiGateway;

/// <summary>
/// Adapts an <see cref="ApiGatewayContext"/> into Benzene's transport-agnostic <see cref="HttpRequest"/> shape.
/// </summary>
public class ApiGatewayHttpRequestAdapter : IHttpRequestAdapter<ApiGatewayContext>
{
    /// <summary>
    /// Maps the API Gateway request onto a Benzene <see cref="HttpRequest"/>, lower-casing header
    /// names - matching <c>AspNetHttpRequestAdapter</c> and <c>ApiGatewayV2Context.CombinedHeaders()</c>,
    /// so <c>authorization</c>/<c>origin</c>/<c>cookie</c> lookups by auth/CORS middleware (which read
    /// by lowercase literal key) work on a v1-triggered request without every caller having to remember
    /// <c>HttpRequest.AsLowerCase()</c> first. API Gateway's raw dict preserves original casing and is
    /// case-sensitive, unlike every sibling transport adapter.
    /// </summary>
    /// <param name="context">The API Gateway context to map.</param>
    /// <returns>The mapped HTTP request.</returns>
    public HttpRequest Map(ApiGatewayContext context)
    {
        return new HttpRequest
        {
            Path = context.ApiGatewayProxyRequest.Path,
            Method = context.ApiGatewayProxyRequest.HttpMethod,
            Headers = context.ApiGatewayProxyRequest.Headers?.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value)
                      ?? new Dictionary<string, string>()
        };
    }
}
