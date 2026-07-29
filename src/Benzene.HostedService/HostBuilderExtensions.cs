using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Benzene.HostedService;

public static class HostBuilderExtensions
{
    /// <summary>Runs a platform-neutral BenzeneStartUp as a hosted worker on the .NET generic host.</summary>
    public static IHostBuilder UseBenzene<TStartUp>(this IHostBuilder hostBuilder)
        where TStartUp : BenzeneStartUp, new()
    {
        var startUp = new TStartUp();
        var configuration = startUp.GetConfiguration();
        return hostBuilder.ConfigureServices((_, services) =>
        {
            services.AddLogging();
            var container = new MicrosoftBenzeneServiceContainer(services);
            var builder = new WorkerApplicationBuilder(container);

            startUp.ConfigureServices(services, configuration);
            startUp.Configure(builder, configuration);

            services.AddSingleton<IHostedService>(provider =>
            {
                var serviceResolverFactory = new MicrosoftServiceResolverFactory(provider);
                // Check the wiring before the worker starts consuming, so a registration mistake is a
                // start-up failure rather than every message failing to route once the loop is live.
                serviceResolverFactory.RunStartUpChecks();
                return new BenzeneHostedServiceAdapter(builder.CreateWorker(serviceResolverFactory));
            });
        });
    }
}
