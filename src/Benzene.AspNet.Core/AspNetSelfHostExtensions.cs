using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.SelfHost;

namespace Benzene.AspNet.Core;

/// <summary>
/// Provides the extension method for hosting ASP.NET Core (Kestrel) as a Benzene worker, so HTTP is
/// wired inside a Worker-platform startup exactly like every other transport
/// (<c>UseSqs</c>, <c>UseKafka</c>, ...) instead of owning the program shape.
/// </summary>
public static class AspNetSelfHostExtensions
{
    /// <summary>
    /// Adds a Kestrel HTTP server to the worker, running every request through a Benzene HTTP
    /// pipeline configured by <paramref name="action"/> - the same pipeline shape
    /// <see cref="BenzeneExtensions.UseHttp(IAspApplicationBuilder, Action{IMiddlewarePipelineBuilder{AspNetContext}})"/>
    /// builds, so <c>asp =&gt; asp.UseMessageHandlers()</c> behaves identically in both.
    /// </summary>
    /// <param name="app">The worker startup to add the HTTP server to.</param>
    /// <param name="action">The action that configures the HTTP middleware pipeline.</param>
    /// <param name="configure">
    /// Optionally configures <see cref="AspNetServerOptions"/> - e.g. set
    /// <see cref="AspNetServerOptions.Urls"/> to choose the listening address (defaults to
    /// <c>http://0.0.0.0:8080</c>), or <see cref="AspNetServerOptions.ConfigureBuilder"/> for
    /// anything else (TLS, Kestrel limits, logging).
    /// </param>
    /// <returns>The worker startup, for method chaining.</returns>
    /// <remarks>
    /// This is the shape for a service where ASP.NET Core is purely the HTTP host for Benzene - no
    /// controllers, no other ASP.NET middleware. Requests the pipeline doesn't respond to are 404s
    /// (there is nothing to fall through to). To embed Benzene inside a larger ASP.NET Core app
    /// instead, use <see cref="BenzeneExtensions.UseBenzene{TStartUp}(WebApplicationBuilder)"/> with
    /// <c>app.UseHttp(...)</c> - see <see cref="AspNetServerWorker"/>'s remarks for the trade-offs.
    /// </remarks>
    public static IBenzeneWorkerStartup UseAspNet(
        this IBenzeneWorkerStartup app,
        Action<IMiddlewarePipelineBuilder<AspNetContext>> action,
        Action<AspNetServerOptions>? configure = null)
    {
        // AddBenzene() is what WebApplicationBuilder.UseBenzene<T> registers up front on the
        // embedded path; the generic-host path (Benzene.HostedService) registers no baseline, so add
        // it here (idempotent - it TryAdds throughout, so co-existing with other transports'
        // registrations is safe).
        app.Register(x => x
            .AddBenzene()
            .AddSingleton<IMiddlewarePipelineBuilder<AspNetContext>, MiddlewarePipelineBuilder<AspNetContext>>()
            .AddAspNetMessageHandlers());

        var pipeline = app.Create<AspNetContext>();
        var builtPipeline = BenzeneExtensions.BuildHttpPipeline(pipeline, action);

        var options = new AspNetServerOptions();
        configure?.Invoke(options);

        app.Add(serviceResolverFactory =>
            new AspNetServerWorker(new AspNetApplication(builtPipeline, serviceResolverFactory), options));
        return app;
    }
}
