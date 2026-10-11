using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Http.Routing;

namespace Benzene.Aws.Lambda.ApiGateway;

/// <summary>
/// Extracts the payload schema version from the matched route's <c>version</c> route parameter,
/// reading the one version header when the matched route declares no such parameter
/// (docs/specification/versioning.md §2.1).
/// </summary>
public class ApiGatewayMessageVersionGetter : HttpMessageVersionGetterBase<ApiGatewayContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApiGatewayMessageVersionGetter"/> class.
    /// </summary>
    /// <param name="routeFinder">The route finder used to match the request to a registered topic.</param>
    /// <param name="headersGetter">Extracts the header dictionary from the context, for the header path.</param>
    /// <param name="headerName">The one version header; defaults to <c>benzene-version</c> when null (the transport passes <see cref="Benzene.Abstractions.IBenzeneWireNames.Version"/>).</param>
    public ApiGatewayMessageVersionGetter(IRouteFinder routeFinder, IMessageHeadersGetter<ApiGatewayContext> headersGetter, string? headerName = null)
        : base(routeFinder, headersGetter, headerName)
    {
    }

    /// <inheritdoc />
    protected override (string Method, string Path) GetMethodAndPath(ApiGatewayContext context)
    {
        return (context.ApiGatewayProxyRequest.HttpMethod, context.ApiGatewayProxyRequest.Path);
    }
}
