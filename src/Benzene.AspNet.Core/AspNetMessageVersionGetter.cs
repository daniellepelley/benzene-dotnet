using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Http.Routing;

namespace Benzene.AspNet.Core;

/// <summary>
/// Extracts the payload schema version from the matched route's <c>version</c> route parameter,
/// reading the one version header when the matched route declares no such parameter
/// (docs/specification/versioning.md §2.1).
/// </summary>
public class AspNetMessageVersionGetter : HttpMessageVersionGetterBase<AspNetContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AspNetMessageVersionGetter"/> class.
    /// </summary>
    /// <param name="routeFinder">The route finder used to match the request's method and path to a route.</param>
    /// <param name="headersGetter">Extracts the header dictionary from the context, for the header path.</param>
    /// <param name="headerName">The one version header; defaults to <c>benzene-version</c> when null (the transport passes <see cref="Benzene.Abstractions.IBenzeneWireNames.Version"/>).</param>
    public AspNetMessageVersionGetter(IRouteFinder routeFinder, IMessageHeadersGetter<AspNetContext> headersGetter, string? headerName = null)
        : base(routeFinder, headersGetter, headerName)
    {
    }

    /// <inheritdoc />
    protected override (string Method, string Path) GetMethodAndPath(AspNetContext context)
    {
        return (context.HttpContext.Request.Method, context.HttpContext.Request.Path);
    }
}
