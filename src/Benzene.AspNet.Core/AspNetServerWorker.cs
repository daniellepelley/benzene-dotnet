using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Benzene.AspNet.Core;

/// <summary>
/// Hosts Kestrel as an <see cref="IBenzeneWorker"/>: a deliberately empty ASP.NET Core application
/// whose only middleware forwards every request into a Benzene HTTP pipeline. Registered by
/// <see cref="AspNetSelfHostExtensions.UseAspNet"/> so an HTTP listener can sit alongside the other
/// self-hosted workers (SQS, Kafka, ...) in a single Worker-platform startup.
/// </summary>
/// <remarks>
/// The inner <see cref="WebApplication"/> resolves nothing from Benzene: the pipeline and the
/// <see cref="Benzene.Abstractions.DI.IServiceResolverFactory"/> inside <paramref name="entryPoint"/>
/// both come from the outer host's container (the one the startup's <c>ConfigureServices</c>
/// populated), so there is no second service provider for anything Benzene resolves - the
/// singleton-split hazard <see cref="AspApplicationBuilder"/>'s two-phase lifecycle guards against
/// cannot occur here. <see cref="StartAsync"/> returns once the socket is bound (it does not run the
/// server's lifetime on the returned task), composing with <c>CompositeBenzeneWorker</c>'s parallel
/// start/rollback and <c>Benzene.HostedService</c>'s adapter; a bind failure throws, failing host
/// start-up loudly. There is no fall-through to other ASP.NET middleware in this mode - a request
/// the pipeline doesn't respond to is a 404. If the process genuinely is an ASP.NET app
/// (controllers, minimal APIs, other middleware), host Benzene inside it with
/// <see cref="BenzeneExtensions.UseBenzene{TStartUp}(WebApplicationBuilder)"/> instead.
/// </remarks>
public class AspNetServerWorker : IBenzeneWorker
{
    private readonly IEntryPointMiddlewareApplication<HttpContext> _entryPoint;
    private readonly AspNetServerOptions _options;
    private WebApplication? _app;

    /// <summary>
    /// Initializes a new instance of the <see cref="AspNetServerWorker"/> class.
    /// </summary>
    /// <param name="entryPoint">The entry point application every request is forwarded into.</param>
    /// <param name="options">The URL(s) to listen on and the optional builder escape hatch.</param>
    public AspNetServerWorker(IEntryPointMiddlewareApplication<HttpContext> entryPoint, AspNetServerOptions options)
    {
        _entryPoint = entryPoint;
        _options = options;
    }

    /// <summary>
    /// Builds the inner Kestrel host and starts it listening on <see cref="AspNetServerOptions.Urls"/>.
    /// Returns once the socket is bound; a bind failure throws.
    /// </summary>
    /// <param name="cancellationToken">Aborts server start-up.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        _options.ConfigureBuilder?.Invoke(builder);
        var app = builder.Build();

        app.Urls.Clear();
        foreach (var url in _options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            app.Urls.Add(url);
        }

        app.Run(async context =>
        {
            await _entryPoint.SendAsync(context, context.RequestAborted);
            if (!context.Response.HasStarted)
            {
                // No "next" in this mode - there are no controllers or other middleware to fall
                // through to, so an unhandled request is a plain 404.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        await app.StartAsync(cancellationToken);
        _app = app;
    }

    /// <summary>
    /// Stops the inner Kestrel host with its normal graceful drain, then disposes it.
    /// </summary>
    /// <param name="cancellationToken">Bounds how long the graceful drain may take.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is { } app)
        {
            _app = null;
            await app.StopAsync(cancellationToken);
            await app.DisposeAsync();
        }
    }
}
