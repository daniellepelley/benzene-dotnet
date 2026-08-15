using System;
using Benzene.Http;

namespace Benzene.Mesh.Auth.Oidc;

/// <summary>Derives this host's own public base URL - needed to build the absolute
/// <c>redirect_uri</c> sent to the provider, since Benzene's transport-agnostic <see cref="HttpRequest"/>
/// carries no such concept itself.</summary>
internal static class RequestUrl
{
    /// <summary>
    /// Returns <see cref="MeshOidcOptions.PublicBaseUrl"/> if set; otherwise derives
    /// <c>{scheme}://{host}</c> from the request's <c>Host</c> header (required) and
    /// <c>X-Forwarded-Proto</c> header (optional, defaulting to <c>https</c> - correct for API Gateway
    /// and any standard reverse proxy, and the safe default regardless since a provider's
    /// <c>redirect_uri</c> should be HTTPS anyway). Assumes <paramref name="request"/> has already been
    /// through <c>HttpRequest.AsLowerCase()</c> so header lookups are case-insensitive.
    /// </summary>
    public static string BuildBaseUrl(HttpRequest request, MeshOidcOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            return options.PublicBaseUrl.TrimEnd('/');
        }

        request.Headers.TryGetValue("host", out var host);
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException(
                "Cannot determine this host's public base URL: the request has no Host header and " +
                $"{nameof(MeshOidcOptions)}.{nameof(MeshOidcOptions.PublicBaseUrl)} is not set.");
        }

        request.Headers.TryGetValue("x-forwarded-proto", out var proto);
        var scheme = string.IsNullOrWhiteSpace(proto) ? "https" : proto;

        return $"{scheme}://{host}";
    }
}
