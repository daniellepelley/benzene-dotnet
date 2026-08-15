using System.Collections.Generic;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Mesh.Auth.Oidc;

namespace Benzene.Examples.AwsMesh.Mesh;

/// <summary>
/// The AWS API Gateway (v1 payload format) binding for <see cref="IOidcQueryStringReader{TContext}"/> -
/// <c>Benzene.Mesh.Auth.Oidc</c> carries no AWS SDK dependency itself (see its <c>CLAUDE.md</c>), so this
/// small adapter lives in the example that actually uses that transport, reading the query string
/// straight off the raw <c>APIGatewayProxyRequest</c> the same way <c>ApiGatewayRequestEnricher</c> does
/// for message-handler route binding.
/// </summary>
public class ApiGatewayOidcQueryStringReader : IOidcQueryStringReader<ApiGatewayContext>
{
    /// <inheritdoc />
    public IDictionary<string, string> GetQueryParameters(ApiGatewayContext context) =>
        context.ApiGatewayProxyRequest.QueryStringParameters ?? new Dictionary<string, string>();
}
