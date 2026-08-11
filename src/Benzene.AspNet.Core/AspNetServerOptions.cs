using Microsoft.AspNetCore.Builder;

namespace Benzene.AspNet.Core;

/// <summary>
/// Configures the Kestrel host <see cref="AspNetServerWorker"/> runs when ASP.NET Core is hosted as
/// a Benzene worker via <see cref="AspNetSelfHostExtensions.UseAspNet"/>.
/// </summary>
public class AspNetServerOptions
{
    /// <summary>
    /// The URL(s) to listen on, semicolon-separated (the <c>ASPNETCORE_URLS</c> convention).
    /// Defaults to <c>http://0.0.0.0:8080</c>.
    /// </summary>
    public string Urls { get; set; } = "http://0.0.0.0:8080";

    /// <summary>
    /// Optional escape hatch run against the inner <see cref="WebApplicationBuilder"/> before it is
    /// built - Kestrel limits, TLS, logging, and anything else <see cref="Urls"/> doesn't cover.
    /// The inner host exists only to run Kestrel; Benzene resolves nothing from its service
    /// container (see <see cref="AspNetServerWorker"/>), so registrations made here are invisible
    /// to message handlers - register those in the startup's <c>ConfigureServices</c> instead.
    /// </summary>
    public Action<WebApplicationBuilder>? ConfigureBuilder { get; set; }
}
