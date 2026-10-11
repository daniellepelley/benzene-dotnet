using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Azure.Function.Core;
using Benzene.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Benzene.Core.MessageHandlers.Info;

namespace Benzene.Azure.Function.BlobStorage;

/// <summary>
/// Provides extension methods for adding blob trigger handling to an <see cref="IAzureFunctionAppBuilder"/>.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers the services blob-trigger handling depends on beyond the entry point application
    /// itself - currently just the <see cref="ITransportInfo"/> advertising <c>"blob-storage"</c>
    /// as a wired transport. Called automatically by <see cref="UseBlobStorage"/>.
    /// </summary>
    /// <param name="services">The service container to register services with.</param>
    /// <returns>The service container for method chaining.</returns>
    public static IBenzeneServiceContainer AddAzureBlobStorage(this IBenzeneServiceContainer services)
    {
        services.AddSingleton<ITransportInfo>(_ => new TransportInfo(TransportNames.BlobStorage));
        return services;
    }

    /// <summary>
    /// Adds a blob entry point application to the Azure Function app, configuring its inner
    /// middleware pipeline. There is no <c>UseMessageHandlers()</c>-style routing on this transport
    /// - a blob is a file, not a message envelope, and one blob-trigger function watches one
    /// container path - so the pipeline is consumed with <c>UseBlob(...)</c> (or any ordinary
    /// middleware), composing with correlation/metrics/exception middleware as usual.
    /// </summary>
    /// <param name="app">The Azure Function app builder to add blob handling to.</param>
    /// <param name="action">The action that configures the blob middleware pipeline.</param>
    /// <returns>The Azure Function app builder, for method chaining.</returns>
    public static IAzureFunctionAppBuilder UseBlobStorage(this IAzureFunctionAppBuilder app, Action<IMiddlewarePipelineBuilder<BlobStorageContext>> action)
    {
        return app.UseBlobStorage(action, _ => { });
    }

    /// <summary>
    /// Adds a blob entry point application to the Azure Function app, with settlement options - e.g.
    /// <see cref="BlobStorageOptions.AtMostOnce"/>, the at-most-once override.
    /// </summary>
    /// <param name="app">The Azure Function app builder to add blob handling to.</param>
    /// <param name="action">The action that configures the blob middleware pipeline.</param>
    /// <param name="configure">Configures how a delivery settles.</param>
    /// <returns>The Azure Function app builder, for method chaining.</returns>
    public static IAzureFunctionAppBuilder UseBlobStorage(this IAzureFunctionAppBuilder app, Action<IMiddlewarePipelineBuilder<BlobStorageContext>> action, Action<BlobStorageOptions> configure)
    {
        var options = new BlobStorageOptions();
        configure(options);
        app.Register(x => x.AddAzureBlobStorage());
        var pipeline = app.Create<BlobStorageContext>();
        action(pipeline);
        app.Add(serviceResolverFactory =>
        {
            var built = pipeline.Build();
            return new BlobStorageApplication(options.AtMostOnce ? new AtMostOncePipeline(built) : built, serviceResolverFactory);
        });
        return app;
    }

    /// <summary>
    /// Applies blob-trigger configuration to a platform-neutral <see cref="IBenzeneApplicationBuilder"/>.
    /// No-op on any platform other than Azure Functions.
    /// </summary>
    /// <param name="app">The application builder passed to <c>BenzeneStartUp.Configure</c>.</param>
    /// <param name="action">The action that configures the blob middleware pipeline.</param>
    /// <returns><paramref name="app"/>, for method chaining.</returns>
    public static IBenzeneApplicationBuilder UseBlobStorage(this IBenzeneApplicationBuilder app, Action<IMiddlewarePipelineBuilder<BlobStorageContext>> action)
    {
        return app.UseBlobStorage(action, _ => { });
    }

    /// <summary>
    /// Applies blob-trigger configuration, with settlement options, to a platform-neutral
    /// <see cref="IBenzeneApplicationBuilder"/>. No-op on any platform other than Azure Functions.
    /// </summary>
    /// <param name="app">The application builder passed to <c>BenzeneStartUp.Configure</c>.</param>
    /// <param name="action">The action that configures the blob middleware pipeline.</param>
    /// <param name="configure">Configures how a delivery settles.</param>
    /// <returns><paramref name="app"/>, for method chaining.</returns>
    public static IBenzeneApplicationBuilder UseBlobStorage(this IBenzeneApplicationBuilder app, Action<IMiddlewarePipelineBuilder<BlobStorageContext>> action, Action<BlobStorageOptions> configure)
    {
        if (app is IAzureFunctionAppBuilder azureApp)
        {
            azureApp.UseBlobStorage(action, configure);
        }
        return app;
    }

    // The at-most-once override (transport-bindings §1, obligation 8): a failed delivery is logged and
    // the invocation returns normally, so the host does not retry the blob. An infrastructure
    // (mis-wiring) failure is not the blob's outcome and still propagates.
    private sealed class AtMostOncePipeline(IMiddlewarePipeline<BlobStorageContext> inner) : IMiddlewarePipeline<BlobStorageContext>
    {
        public async Task HandleAsync(BlobStorageContext context, IServiceResolver serviceResolver)
        {
            try
            {
                await inner.HandleAsync(context, serviceResolver);
            }
            catch (Exception ex) when (!BenzeneFailure.IsInfrastructure(ex))
            {
                (serviceResolver.TryGetService<ILoggerFactory>()?.CreateLogger(typeof(DependencyInjectionExtensions).FullName!)
                        ?? NullLogger.Instance)
                    .LogError(ex, "Processing blob {name} failed; acknowledged without a retry (AtMostOnce)", context.Blob.Name);
            }
        }
    }
}
