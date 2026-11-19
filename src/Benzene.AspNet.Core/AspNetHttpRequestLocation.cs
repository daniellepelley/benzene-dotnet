using Benzene.Http.RequestUrl;
using Microsoft.AspNetCore.Http;

namespace Benzene.AspNet.Core;

/// <summary>
/// The ASP.NET Core request's path. A stripped base path moves into <c>PathBase</c>, exactly as
/// <c>app.Map</c> does, so <c>Request.Path</c> - what every reader routes on - is the remainder.
/// </summary>
public class AspNetHttpRequestLocation : IHttpRequestLocation<AspNetContext>
{
    /// <inheritdoc />
    public string GetPath(AspNetContext context)
    {
        return context.HttpContext.Request.Path.Value ?? "/";
    }

    /// <inheritdoc />
    public void SetBasePath(AspNetContext context, string basePath, string path)
    {
        var request = context.HttpContext.Request;
        request.PathBase = request.PathBase.Add(new PathString(basePath));
        request.Path = new PathString(path);
    }

    /// <inheritdoc />
    public string? GetScheme(AspNetContext context)
    {
        return context.HttpContext.Request.Scheme;
    }
}
