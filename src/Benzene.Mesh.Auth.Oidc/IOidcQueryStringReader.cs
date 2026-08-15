using System.Collections.Generic;
using Benzene.Http;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// Reads the raw query string parameters off a transport-specific HTTP context. Complements
/// <see cref="IHttpRequestAdapter{TContext}"/>, whose <c>HttpRequest.Path</c> deliberately excludes the
/// query string - no existing Benzene abstraction exposes it generically, and the login/callback routes
/// need <c>code</c>, <c>state</c>, and <c>returnTo</c> off it. A transport binding registers its own
/// implementation (e.g. reading <c>APIGatewayProxyRequest.QueryStringParameters</c> for AWS API
/// Gateway) - this package stays free of any transport SDK dependency.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
public interface IOidcQueryStringReader<in TContext> where TContext : IHttpContext
{
    /// <summary>Gets the query string parameters for the given request context. Never null - an empty
    /// dictionary for a request with no query string.</summary>
    /// <param name="context">The transport-specific HTTP context to read from.</param>
    IDictionary<string, string> GetQueryParameters(TContext context);
}
