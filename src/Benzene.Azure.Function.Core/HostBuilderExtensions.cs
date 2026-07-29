using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Benzene.Azure.Function.Core;

/// <summary>
/// Provides the <see cref="IHostBuilder"/> integration for running a platform-neutral
/// <see cref="BenzeneStartUp"/> on the Azure Functions isolated worker.
/// </summary>
public static class HostBuilderExtensions
{
    /// <summary>
    /// Registers a platform-neutral <see cref="BenzeneStartUp"/>'s services and pipeline with the
    /// isolated worker host, and registers <see cref="IAzureFunctionApp"/> so trigger functions can
    /// inject it and dispatch invocations to it.
    /// </summary>
    /// <typeparam name="TStartUp">The <see cref="BenzeneStartUp"/> to run.</typeparam>
    /// <param name="hostBuilder">The isolated worker host builder.</param>
    /// <returns><paramref name="hostBuilder"/>, for method chaining.</returns>
    public static IHostBuilder UseBenzene<TStartUp>(this IHostBuilder hostBuilder)
        where TStartUp : BenzeneStartUp, new()
    {
        var startUp = new TStartUp();
        var configuration = startUp.GetConfiguration();
        return hostBuilder.ConfigureServices((_, services) =>
        {
            services.AddLogging();
            var container = new MicrosoftBenzeneServiceContainer(services);
            var builder = new AzureFunctionAppBuilder(container);

            startUp.ConfigureServices(services, configuration);
            startUp.Configure(builder, configuration);

            services.AddScoped<IAzureFunctionApp>(serviceProvider =>
            {
                var serviceResolverFactory = new MicrosoftServiceResolverFactory(serviceProvider);
                // Azure Functions resolves IAzureFunctionApp per invocation, so unlike the Lambda host
                // there is no separate INIT hook to hang this on. Running it here still moves the
                // failure off the message path and onto the trigger's own construction.
                serviceResolverFactory.RunStartUpChecks();
                return builder.Create(serviceResolverFactory);
            });
        });
    }
}
