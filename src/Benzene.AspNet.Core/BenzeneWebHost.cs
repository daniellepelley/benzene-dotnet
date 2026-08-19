using Benzene.Microsoft.Dependencies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Benzene.AspNet.Core;

/// <summary>
/// Runs a <see cref="BenzeneStartUp"/> on an ASP.NET Core (Kestrel) host, so a Benzene service's
/// entry point is one line — the embedded-path counterpart of
/// <c>Benzene.HostedService.BenzeneHost</c>.
/// </summary>
/// <remarks>
/// <para>
/// The explicit form this composes is the three-call embedded triangle, and it stays fully supported
/// — nothing here can do anything you could not write yourself:
/// </para>
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.UseBenzene&lt;StartUp&gt;();   // BenzeneExtensions.UseBenzene&lt;TStartUp&gt;(WebApplicationBuilder)
/// var app = builder.Build();
/// app.UseBenzene();                  // BenzeneExtensions.UseBenzene(IApplicationBuilder)
/// app.Run();
/// </code>
/// <para>which this reduces to:</para>
/// <code>
/// // Program.cs, entire
/// await BenzeneWebHost.RunAsync&lt;StartUp&gt;(args);
/// </code>
/// <para>
/// <b>When NOT to use it — read this before reaching for it.</b> If the process has no ASP.NET
/// surface of its own (no controllers, no minimal APIs) and no second host to run the same startup
/// under, this is the <em>wrong</em> shape. Use <c>BenzeneHost.RunAsync&lt;TStartUp&gt;(args)</c> and
/// declare HTTP in the startup alongside every other transport —
/// <c>app.UseWorker(worker =&gt; worker.UseAspNet(http =&gt; …, o =&gt; o.Urls = …))</c>
/// (<see cref="AspNetSelfHostExtensions.UseAspNet"/>). That keeps hosting in one place, so adding a
/// queue consumer later is one line in <c>Configure</c> rather than a rewrite of the entry point.
/// </para>
/// <para>
/// This exists for the two cases where the embedded path is genuinely right and the ceremony is
/// still pure plumbing: a startup that must <em>also</em> run under another host (an Azure Functions
/// or Cloud Functions host, where <c>UseAspNet</c> would start a second Kestrel), and a larger
/// ASP.NET app that adds its own middleware — which goes in <c>configureApp</c>, before Benzene's
/// terminal wiring, exactly as it would between <c>Build()</c> and <c>app.UseBenzene()</c> above.
/// </para>
/// <para>
/// The start-up checks run inside <c>app.UseBenzene()</c> as always, so a mis-wired pipeline fails
/// before the first request either way.
/// </para>
/// </remarks>
public static class BenzeneWebHost
{
    /// <summary>
    /// Builds the ASP.NET Core application for <typeparamref name="TStartUp"/> without running it —
    /// the escape hatch, and the seam a test uses.
    /// </summary>
    /// <typeparam name="TStartUp">The startup describing the service's services and pipeline.</typeparam>
    /// <param name="args">Command-line arguments, passed to <see cref="WebApplication.CreateBuilder(string[])"/>.</param>
    /// <param name="configureBuilder">
    /// Applied to the <see cref="WebApplicationBuilder"/> <b>before</b> <c>UseBenzene&lt;TStartUp&gt;()</c>
    /// — the hook for listening addresses (<c>builder.WebHost.UseUrls(…)</c>), configuration sources,
    /// logging, and any service registration that must land before the startup runs (Benzene's own
    /// baseline is <c>TryAdd</c>, so a substitution has to be registered first to win).
    /// </param>
    /// <param name="configureApp">
    /// Applied to the built <see cref="WebApplication"/> <b>before</b> <c>app.UseBenzene()</c> — the
    /// hook for the app's own ASP.NET middleware (<c>UseRouting</c>, <c>UseAuthentication</c>,
    /// <c>MapControllers</c>, …), which must run in front of Benzene's terminal wiring.
    /// </param>
    /// <returns>The built application, not yet started.</returns>
    public static WebApplication Build<TStartUp>(string[]? args = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? configureApp = null)
        where TStartUp : BenzeneStartUp, new()
    {
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        configureBuilder?.Invoke(builder);
        builder.UseBenzene<TStartUp>();

        var app = builder.Build();
        configureApp?.Invoke(app);
        app.UseBenzene();

        return app;
    }

    /// <summary>Builds and runs the application, returning when it shuts down.</summary>
    /// <typeparam name="TStartUp">The startup describing the service's services and pipeline.</typeparam>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configureBuilder">Applied to the builder before <c>UseBenzene&lt;TStartUp&gt;()</c>.</param>
    /// <param name="configureApp">Applied to the built application before <c>app.UseBenzene()</c>.</param>
    /// <param name="cancellationToken">Triggers shutdown, in addition to the host's own signals.</param>
    public static Task RunAsync<TStartUp>(string[]? args = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? configureApp = null,
        CancellationToken cancellationToken = default)
        where TStartUp : BenzeneStartUp, new()
        // Cast to IHost for the cancellation-token overload: WebApplication.RunAsync(string? url)
        // shadows it, so calling it unqualified silently loses the token.
        => ((IHost)Build<TStartUp>(args, configureBuilder, configureApp)).RunAsync(cancellationToken);

    /// <summary>
    /// Builds and runs the application, blocking until it shuts down — the synchronous counterpart of
    /// <see cref="RunAsync{TStartUp}"/>.
    /// </summary>
    /// <typeparam name="TStartUp">The startup describing the service's services and pipeline.</typeparam>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configureBuilder">Applied to the builder before <c>UseBenzene&lt;TStartUp&gt;()</c>.</param>
    /// <param name="configureApp">Applied to the built application before <c>app.UseBenzene()</c>.</param>
    public static void Run<TStartUp>(string[]? args = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? configureApp = null)
        where TStartUp : BenzeneStartUp, new()
        => Build<TStartUp>(args, configureBuilder, configureApp).Run();
}
