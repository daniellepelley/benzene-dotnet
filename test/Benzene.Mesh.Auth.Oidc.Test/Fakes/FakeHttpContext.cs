using System.Collections.Generic;
using Benzene.Http;

namespace Benzene.Mesh.Auth.Oidc.Test.Fakes;

/// <summary>A minimal, transport-agnostic <see cref="IHttpContext"/> test double: a request (method,
/// path, headers, query parameters) and the response fields the middleware under test writes to.
/// Deliberately not tied to any real transport SDK - exercising this package's middleware directly
/// against its own abstractions is the point (see this package's <c>CLAUDE.md</c> on
/// <c>IOidcQueryStringReader</c>).</summary>
public sealed class FakeHttpContext : IHttpContext
{
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    public IDictionary<string, string> QueryParameters { get; set; } = new Dictionary<string, string>();

    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? Body { get; set; }
    public List<(string Key, string Value)> ResponseHeaders { get; } = new();
    public bool Finalized { get; set; }

    public string? Location => Find("Location");
    public IEnumerable<string> SetCookies => FindAll("Set-Cookie");

    private string? Find(string key)
    {
        for (var i = ResponseHeaders.Count - 1; i >= 0; i--)
        {
            if (string.Equals(ResponseHeaders[i].Key, key, System.StringComparison.OrdinalIgnoreCase))
            {
                return ResponseHeaders[i].Value;
            }
        }

        return null;
    }

    private IEnumerable<string> FindAll(string key)
    {
        foreach (var header in ResponseHeaders)
        {
            if (string.Equals(header.Key, key, System.StringComparison.OrdinalIgnoreCase))
            {
                yield return header.Value;
            }
        }
    }
}
