namespace Benzene.Http.RequestUrl;

/// <summary>
/// The scoped <see cref="IRequestUrl"/> for one request, filled by
/// <see cref="BasePathMiddleware{TContext}"/>. Reading it before the middleware ran throws, naming the
/// missing call, rather than returning empty values a link would silently be built from.
/// </summary>
public class RequestUrlHolder : IRequestUrl
{
    private string? _scheme;
    private string? _host;
    private string _basePath = string.Empty;
    private string? _path;

    /// <inheritdoc />
    public string Scheme => Get(_scheme);

    /// <inheritdoc />
    public string Host => Get(_host);

    /// <inheritdoc />
    public string BasePath
    {
        get
        {
            Get(_scheme);
            return _basePath;
        }
    }

    /// <inheritdoc />
    public string Path => Get(_path);

    /// <inheritdoc />
    public string BaseUrl => $"{Scheme}://{Host}{BasePath}";

    /// <inheritdoc />
    public Uri Absolute(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (Uri.TryCreate(relativePath, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        if (Host.Length == 0)
        {
            throw new InvalidOperationException(
                "Can't build an absolute URL: the request has no Host header and RequestUrlOptions.PublicBaseUrl is not set.");
        }

        var path = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        return new Uri(BaseUrl + path);
    }

    /// <summary>Fills the holder for the current request.</summary>
    /// <param name="scheme">The scheme.</param>
    /// <param name="host">The host, with a port if it had one.</param>
    /// <param name="basePath">The stripped base path, or empty.</param>
    /// <param name="path">The path after the base path.</param>
    public void Set(string scheme, string host, string basePath, string path)
    {
        _scheme = scheme;
        _host = host;
        _basePath = basePath;
        _path = path;
    }

    private static string Get(string? value)
    {
        return value ?? throw new InvalidOperationException(
            "IRequestUrl was read before it was filled for this request. Add .UseRequestUrl(...) or " +
            ".UseBasePath(...) to the start of the HTTP pipeline (before CORS and UseMessageHandlers).");
    }
}
