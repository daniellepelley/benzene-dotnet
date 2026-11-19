namespace Benzene.Http.RequestUrl;

/// <summary>Options for <c>UseRequestUrl(...)</c>.</summary>
public class RequestUrlOptions
{
    /// <summary>
    /// A prefix every request path must start with, stripped before routing, e.g. <c>/tasks</c> for a
    /// service behind a custom-domain mapping. A request outside it answers <c>not-found</c>. Empty for
    /// none.
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// The public scheme and host, e.g. <c>https://api.example.com</c>, used instead of the request's
    /// headers. Set it when the service sits behind a proxy whose headers don't carry the public host.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Whether to take the host from an <c>X-Forwarded-Host</c> header. Off by default: a caller can
    /// send any value, and generated links would then point wherever it said. Turn it on only behind a
    /// proxy that sets the header itself.
    /// </summary>
    public bool TrustForwardedHost { get; set; }
}
