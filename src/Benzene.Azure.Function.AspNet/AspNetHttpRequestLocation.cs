using Benzene.Http.RequestUrl;
using Microsoft.AspNetCore.Http;

namespace Benzene.Azure.Function.AspNet;

/// <summary>
/// The Azure Functions HTTP request's path. A stripped base path (for instance Azure's default
/// <c>/api</c> route prefix) moves into <c>PathBase</c>, so <c>Request.Path</c> - what every reader
/// routes on - is the remainder.
/// </summary>
public class AspNetHttpRequestLocation : IHttpRequestLocation<AspNetContext>
{
    /// <inheritdoc />
    public string GetPath(AspNetContext context)
    {
        return context.HttpRequest.Path.Value ?? "/";
    }

    /// <inheritdoc />
    public void SetBasePath(AspNetContext context, string basePath, string path)
    {
        var request = context.HttpRequest;
        request.PathBase = request.PathBase.Add(new PathString(basePath));
        request.Path = new PathString(path);
    }

    /// <inheritdoc />
    public string? GetScheme(AspNetContext context)
    {
        return context.HttpRequest.Scheme;
    }
}
