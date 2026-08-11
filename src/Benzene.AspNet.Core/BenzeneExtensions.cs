using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Benzene.Core;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.Http.RequestBody;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Microsoft.Dependencies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.AspNet.Core;

/// <summary>
/// Provides the top-level extension methods for wiring Benzene into an ASP.NET Core application.
/// </summary>
public static class BenzeneExtensions
{
    /// <summary>
    /// Adds an HTTP entry point application to the ASP.NET Core app, configuring its inner middleware
    /// pipeline. This is the ASP.NET Core-specific building block <see cref="UseBenzene(WebApplicationBuilder)"/>
    /// and <see cref="UseBenzene(IApplicationBuilder)"/> use to run a platform-neutral <see cref="BenzeneStartUp"/>;
    /// call it directly only if you're wiring ASP.NET Core up by hand instead.
    /// </summary>
    /// <param name="app">The Benzene ASP.NET application builder to add HTTP handling to.</param>
    /// <param name="action">The action that configures the HTTP middleware pipeline.</param>
    /// <returns>The Benzene ASP.NET application builder, for method chaining.</returns>
    public static IAspApplicationBuilder UseHttp(this IAspApplicationBuilder app, Action<IMiddlewarePipelineBuilder<AspNetContext>> action)
    {
        var pipeline = app.Create<AspNetContext>();
        app.Register(x => x
            .AddSingleton<IMiddlewarePipelineBuilder<AspNetContext>, MiddlewarePipelineBuilder<AspNetContext>>()
            .AddAspNetMessageHandlers());
        // Seed the scope's ambient cancellation token from the request's aborted token, so any
        // component resolving ICancellationTokenAccessor (e.g. a health check) observes a client
        // disconnect / request abort. Runs first, in the same per-request scope as the rest.
        pipeline.Use(resolver => new FuncWrapperMiddleware<AspNetContext>("SeedCancellationToken", async (context, next) =>
        {
            var accessor = resolver.TryGetService<CancellationTokenAccessor>();
            if (accessor != null)
            {
                accessor.CancellationToken = context.HttpContext.RequestAborted;
            }
            await next();
        }));

        // Read the request body asynchronously, once, up front - so the synchronous
        // AspNetMessageBodyGetter serves it from memory instead of blocking a thread-pool thread.
        pipeline.UseBufferedRequestBody();
        action(pipeline);

        // Built eagerly, here, rather than inside the Add() callback below: Build() registers this
        // pipeline's PipelineDescriptor into the service container as a side effect (see
        // MiddlewarePipelineBuilder.Build()), and Add()'s callback only runs once the ASP.NET Core
        // host is built - by which point the underlying IServiceCollection is sealed and any further
        // registration throws. Matches the same eager-Build() pattern Benzene.Grpc.AspNet's UseGrpc
        // already uses.
        var builtPipeline = pipeline.Build();
        app.Add(serviceResolverFactory => new AspNetApplication(builtPipeline, serviceResolverFactory));
        return app;
    }

    /// <summary>
    /// Applies HTTP-specific configuration to a platform-neutral <see cref="IBenzeneApplicationBuilder"/>.
    /// No-op on any platform other than ASP.NET Core.
    /// </summary>
    /// <param name="app">The application builder passed to <see cref="BenzeneStartUp.Configure"/>.</param>
    /// <param name="action">The action that configures the HTTP middleware pipeline.</param>
    /// <returns><paramref name="app"/>, for method chaining.</returns>
    public static IBenzeneApplicationBuilder UseHttp(this IBenzeneApplicationBuilder app, Action<IMiddlewarePipelineBuilder<AspNetContext>> action)
    {
        if (app is IAspApplicationBuilder aspApp)
        {
            aspApp.UseHttp(action);
        }
        return app;
    }

    /// <summary>
    /// Wires Benzene into the ASP.NET Core application pipeline, wrapping <paramref name="app"/> in a
    /// <see cref="Benzene.AspNet.Core.AspApplicationBuilder"/> and running the given configuration action
    /// against it.
    /// </summary>
    /// <param name="app">The ASP.NET Core application builder.</param>
    /// <param name="builder">The action that configures Benzene (typically via <see cref="UseHttp(IAspApplicationBuilder, Action{IMiddlewarePipelineBuilder{AspNetContext}})"/>).</param>
    /// <returns><paramref name="app"/>, for method chaining.</returns>
    public static IApplicationBuilder UseBenzene(this IApplicationBuilder app, Action<IAspApplicationBuilder> builder)
    {
        var aspApplicationBuilder = new AspApplicationBuilder(app);
        aspApplicationBuilder.Register(x => x.AddBenzene());
        builder(aspApplicationBuilder);
        return app;
    }

    /// <summary>
    /// Registers a platform-neutral <see cref="BenzeneStartUp"/>'s services with the ASP.NET Core host,
    /// running <see cref="IStartUp{TContainer,TConfiguration,TAppBuilder}.GetConfiguration"/>,
    /// <see cref="IStartUp{TContainer,TConfiguration,TAppBuilder}.ConfigureServices"/>, and
    /// <see cref="IStartUp{TContainer,TConfiguration,TAppBuilder}.Configure"/>. Pair with
    /// <see cref="UseBenzene(IApplicationBuilder)"/> after <c>Build()</c> to finish wiring the
    /// middleware pipeline into the ASP.NET Core request pipeline.
    /// </summary>
    /// <typeparam name="TStartUp">The <see cref="BenzeneStartUp"/> to run.</typeparam>
    /// <param name="builder">The web application builder.</param>
    /// <returns><paramref name="builder"/>, for method chaining.</returns>
    /// <remarks>
    /// <c>Configure</c> runs here, before <c>Build()</c>, not in the paired post-Build call - so every
    /// registration it makes (the message pipeline, <c>AddBenzene()</c>, ...) lands in this same
    /// <see cref="WebApplicationBuilder.Services"/> the host's eventual root provider is built from,
    /// instead of a second, separate provider built later from a clone. Without that, a singleton
    /// registered here that a Benzene message handler also depends on would silently be a different
    /// instance from the one anything else resolved outside Benzene's own routing (a plain
    /// <c>IHostedService</c>, a controller, ...) sees - see <see cref="AspApplicationBuilder"/>'s
    /// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> constructor and
    /// <see cref="AspApplicationBuilder.Finish"/> for the mechanics.
    /// </remarks>
    public static WebApplicationBuilder UseBenzene<TStartUp>(this WebApplicationBuilder builder)
        where TStartUp : BenzeneStartUp, new()
    {
        var startUp = new TStartUp();
        var configuration = startUp.GetConfiguration();
        startUp.ConfigureServices(builder.Services, configuration);

        var aspApplicationBuilder = new AspApplicationBuilder(builder.Services);
        aspApplicationBuilder.Register(x => x.AddBenzene());
        startUp.Configure(aspApplicationBuilder, configuration);

        builder.Services.AddSingleton(new BenzeneStartUpHolder(aspApplicationBuilder));
        return builder;
    }

    /// <summary>
    /// Completes the middleware wiring <see cref="UseBenzene{TStartUp}(WebApplicationBuilder)"/> deferred,
    /// against the now-built application.
    /// </summary>
    /// <param name="app">The built ASP.NET Core application.</param>
    /// <returns><paramref name="app"/>, for method chaining.</returns>
    public static IApplicationBuilder UseBenzene(this IApplicationBuilder app)
    {
        var holder = app.ApplicationServices.GetRequiredService<BenzeneStartUpHolder>();
        holder.AspApplicationBuilder.Finish(app);

        // Check the wiring while the app is still being built, so a registration mistake fails
        // start-up instead of turning into a 404 or a 500 on the first request that reaches it.
        new MicrosoftServiceResolverFactory(app.ApplicationServices).RunStartUpChecks();

        return app;
    }

    private sealed class BenzeneStartUpHolder
    {
        public BenzeneStartUpHolder(AspApplicationBuilder aspApplicationBuilder)
        {
            AspApplicationBuilder = aspApplicationBuilder;
        }

        public AspApplicationBuilder AspApplicationBuilder { get; }
    }
}

/// <summary>
/// Builds the set of entry point applications wired into an ASP.NET Core application's request pipeline.
/// </summary>
public interface IAspApplicationBuilder: IRegisterDependency
{
    /// <summary>
    /// Registers a factory for an entry point application, adding ASP.NET Core middleware that runs it
    /// against each incoming request.
    /// </summary>
    /// <param name="func">A factory that creates the entry point application given the current invocation's service resolver factory.</param>
    void Add(Func<IServiceResolverFactory, IEntryPointMiddlewareApplication<HttpContext>> func);

    /// <summary>
    /// Creates a new middleware pipeline builder for a given context type, sharing this builder's
    /// underlying service container.
    /// </summary>
    /// <typeparam name="TNewContext">The context type the pipeline operates on.</typeparam>
    /// <returns>The created pipeline builder.</returns>
    IMiddlewarePipelineBuilder<TNewContext> Create<TNewContext>();
}
