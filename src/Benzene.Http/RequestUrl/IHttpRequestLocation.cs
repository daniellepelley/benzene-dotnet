namespace Benzene.Http.RequestUrl;

/// <summary>
/// A host's read/write view of where a request was sent: its path, and the scheme where the host
/// knows it. Each HTTP host registers one. <see cref="BasePathMiddleware{TContext}"/> uses it to strip a
/// base path from the raw request, once, so every later reader (topic getter, request enricher,
/// version getter, <see cref="IHttpRequestAdapter{TContext}"/>) sees the stripped path.
/// </summary>
/// <typeparam name="TContext">The host's HTTP context type.</typeparam>
public interface IHttpRequestLocation<TContext>
{
    /// <summary>Gets the request path as the host routes it.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>The path, starting with <c>/</c>.</returns>
    string GetPath(TContext context);

    /// <summary>
    /// Rewrites the request path to <paramref name="path"/>, recording <paramref name="basePath"/> as
    /// the prefix that was stripped from it (on ASP.NET Core, <c>PathBase</c>).
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="basePath">The stripped prefix, e.g. <c>/tasks</c>.</param>
    /// <param name="path">The remaining path, starting with <c>/</c>.</param>
    void SetBasePath(TContext context, string basePath, string path);

    /// <summary>Gets the request's scheme where the host knows it (ASP.NET Core does), else <c>null</c>.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>The scheme, e.g. <c>https</c>, or <c>null</c>.</returns>
    string? GetScheme(TContext context);
}
