using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Http.Routing;

namespace Benzene.Aws.Lambda.ApiGateway;

/// <summary>
/// Extracts the payload schema version from the matched route's <c>version</c> route parameter,
/// reading the one version header when the matched route declares no such parameter
/// (docs/specification/versioning.md §2.1). The v2 counterpart of
/// <see cref="ApiGatewayMessageVersionGetter"/>.
/// </summary>
public class ApiGatewayV2MessageVersionGetter : HttpMessageVersionGetterBase<ApiGatewayV2Context>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApiGatewayV2MessageVersionGetter"/> class.
    /// </summary>
    /// <param name="routeFinder">The route finder used to match the request to a registered topic.</param>
    /// <param name="headersGetter">Extracts the header dictionary from the context, for the header path.</param>
    /// <param name="headerName">The one version header; defaults to <c>benzene-version</c> when null (the transport passes <see cref="Benzene.Abstractions.IBenzeneWireNames.Version"/>).</param>
    public ApiGatewayV2MessageVersionGetter(IRouteFinder routeFinder, IMessageHeadersGetter<ApiGatewayV2Context> headersGetter, string? headerName = null)
        : base(routeFinder, headersGetter, headerName)
    {
    }

    /// <inheritdoc />
    protected override (string Method, string Path) GetMethodAndPath(ApiGatewayV2Context context)
    {
        return (context.Method, context.Path);
    }
}
