using Benzene.Http.RequestUrl;

namespace Benzene.Aws.Lambda.ApiGateway;

/// <summary>
/// The API Gateway REST API (v1) request's path: <c>APIGatewayProxyRequest.Path</c>, which every v1
/// reader routes on, rewritten in place when a base path is stripped. The scheme comes from headers.
/// </summary>
public class ApiGatewayHttpRequestLocation : IHttpRequestLocation<ApiGatewayContext>
{
    /// <inheritdoc />
    public string GetPath(ApiGatewayContext context)
    {
        return context.ApiGatewayProxyRequest.Path;
    }

    /// <inheritdoc />
    public void SetBasePath(ApiGatewayContext context, string basePath, string path)
    {
        context.ApiGatewayProxyRequest.Path = path;
    }

    /// <inheritdoc />
    public string? GetScheme(ApiGatewayContext context)
    {
        return null;
    }
}
