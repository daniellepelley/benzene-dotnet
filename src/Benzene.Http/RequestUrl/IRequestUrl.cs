namespace Benzene.Http.RequestUrl;

/// <summary>
/// Where the current HTTP request was sent, without the host's raw request types: inject it into a
/// handler to build absolute links (a <c>Location</c>, a <c>next</c> page). Filled per request by
/// <c>UseRequestUrl(...)</c> / <c>UseBasePath(...)</c>, which must be in the pipeline.
/// </summary>
public interface IRequestUrl
{
    /// <summary>The scheme the caller used, e.g. <c>https</c>.</summary>
    string Scheme { get; }

    /// <summary>The host the caller used, with a port if it had one, e.g. <c>api.example.com</c>.</summary>
    string Host { get; }

    /// <summary>The base path stripped before routing, e.g. <c>/tasks</c>; empty when there is none.</summary>
    string BasePath { get; }

    /// <summary>The request path after the base path, e.g. <c>/123</c>.</summary>
    string Path { get; }

    /// <summary><c>scheme://host</c> followed by the base path, e.g. <c>https://api.example.com/tasks</c>.</summary>
    string BaseUrl { get; }

    /// <summary>
    /// Makes <paramref name="relativePath"/> absolute under <see cref="BaseUrl"/>:
    /// <c>Absolute("/123")</c> gives <c>https://api.example.com/tasks/123</c>. An absolute URL is
    /// returned as it is.
    /// </summary>
    /// <param name="relativePath">A path relative to the base path, e.g. <c>/123</c>.</param>
    /// <returns>The absolute URL.</returns>
    Uri Absolute(string relativePath);
}
