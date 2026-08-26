using System;
using System.Collections.Generic;
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
    /// <remarks>
    /// #105: the resulting <see cref="HttpRequest.Headers"/> is built with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> - matching the case-insensitive, non-null contract
    /// <see cref="HttpRequest.Headers"/> documents - instead of the plain-ordinal dictionary
    /// <c>Dictionary&lt;,&gt;.ToDictionary</c> would otherwise produce; two header names that collide
    /// once lower-cased (a malformed/duplicate wire payload) resolve first-wins rather than throwing.
    /// The underlying <c>APIGatewayProxyRequest</c>'s <c>Headers</c>, <c>HttpMethod</c> and <c>Path</c>
    /// are all nullable-oblivious on the wire type and can be <c>null</c> for a hand-built payload or a
    /// request with no headers (health pings, authorizer test invokes) - each is defaulted so this
    /// method never hands back a <c>null</c> where <see cref="HttpRequest"/> promises a non-null
    /// string/dictionary.
    /// </remarks>
    /// <param name="context">The API Gateway context to map.</param>
    /// <returns>The mapped HTTP request.</returns>
    public HttpRequest Map(ApiGatewayContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.ApiGatewayProxyRequest.Headers != null)
        {
            // First-wins on a case-collision (matches the pattern used throughout
            // Benzene.Core.Helper.DictionaryUtils, e.g. Replace/FilterAndReplace) rather than the
            // ToDictionary this replaced, which throws ArgumentException on a duplicate lower-cased key.
            foreach (var header in context.ApiGatewayProxyRequest.Headers)
            {
                headers.TryAdd(header.Key.ToLowerInvariant(), header.Value);
            }
        }

        return new HttpRequest
        {
            Path = context.ApiGatewayProxyRequest.Path ?? string.Empty,
            Method = context.ApiGatewayProxyRequest.HttpMethod ?? string.Empty,
            Headers = headers
        };
    }
}
