using Benzene.Http;

namespace Benzene.AspNet.Core;

/// <summary>
/// Adapts an <see cref="AspNetContext"/>'s ASP.NET Core HTTP request into Benzene's transport-agnostic
/// <see cref="HttpRequest"/> shape.
/// </summary>
/// <remarks>
/// Serves every host built on ASP.NET Core's request pipeline, not just <c>UseAspNet</c> itself - the
/// Google Cloud Functions HTTP trigger, Cloud Run, and <c>Benzene.Azure.Function.AspNet</c> all run
/// through this same adapter.
/// </remarks>
public class AspNetHttpRequestAdapter : IHttpRequestAdapter<AspNetContext>
{
    /// <summary>
    /// Maps the context's ASP.NET Core request into a Benzene <see cref="HttpRequest"/>, lower-casing
    /// header names.
    /// </summary>
    /// <remarks>
    /// #164 (mirroring #105's fix for <c>ApiGatewayHttpRequestAdapter</c>): the resulting
    /// <see cref="HttpRequest.Headers"/> is built with <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// and first-wins <c>TryAdd</c> - matching the case-insensitive, non-null contract
    /// <see cref="HttpRequest.Headers"/> documents - instead of the plain-ordinal dictionary
    /// <c>IEnumerable&lt;,&gt;.ToDictionary</c> would otherwise produce, which also throws
    /// <see cref="ArgumentException"/> on two header names that collide once lower-cased. <c>Path</c>/
    /// <c>Method</c> get <c>?? string.Empty</c>: <see cref="Microsoft.AspNetCore.Http.PathString"/>'s
    /// implicit conversion to <see langword="string"/> can be <c>null</c> for a default/unset
    /// <c>PathString</c> (e.g. a hand-built <c>HttpContext</c> in a test or a minimal synthetic
    /// request), and <see cref="HttpRequest"/> promises non-null strings.
    /// </remarks>
    /// <param name="context">The context to adapt.</param>
    /// <returns>The adapted HTTP request.</returns>
    public HttpRequest Map(AspNetContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in context.HttpContext.Request.Headers)
        {
            headers.TryAdd(header.Key.ToLowerInvariant(), header.Value.ToString());
        }

        return new HttpRequest
        {
            Path = context.HttpContext.Request.Path.Value ?? string.Empty,
            Method = context.HttpContext.Request.Method ?? string.Empty,
            Headers = headers
        };
    }
}
