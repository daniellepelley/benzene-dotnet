using Benzene.Http.RequestUrl;

namespace Benzene.Aws.Lambda.ApiGateway;

/// <summary>
/// The API Gateway HTTP API (v2) request's path: <c>RequestContext.Http.Path</c>, which every v2 reader
/// routes on (<see cref="ApiGatewayV2Context.Path"/>), rewritten in place - with <c>RawPath</c> - when a
/// base path is stripped. The scheme comes from headers.
/// </summary>
public class ApiGatewayV2HttpRequestLocation : IHttpRequestLocation<ApiGatewayV2Context>
{
    /// <inheritdoc />
    public string GetPath(ApiGatewayV2Context context)
    {
        return context.Path;
    }

    /// <inheritdoc />
    public void SetBasePath(ApiGatewayV2Context context, string basePath, string path)
    {
        var request = context.ApiGatewayProxyRequest;
        if (request.RequestContext?.Http != null)
        {
            request.RequestContext.Http.Path = path;
        }

        request.RawPath = path;
    }

    /// <inheritdoc />
    public string? GetScheme(ApiGatewayV2Context context)
    {
        return null;
    }
}
