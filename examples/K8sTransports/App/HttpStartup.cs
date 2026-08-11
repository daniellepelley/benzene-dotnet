using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Examples.K8sTransports.Domain;
using Benzene.Http;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.K8sTransports.App;

/// <summary>
/// The HTTP leg: maps <c>POST /orders</c> straight onto <see cref="PlaceOrderMessageHandler"/> on
/// Kestrel. Wired via <c>WebApplicationBuilder.UseBenzene&lt;HttpStartup&gt;()</c> in
/// <c>Program.cs</c> - a plain <see cref="IBenzeneApplicationBuilder"/> is handed to
/// <see cref="Configure"/>, and calling <c>app.UseWorker(...)</c> here would silently no-op (it only
/// runs when the app is a <c>WorkerApplicationBuilder</c>), which is exactly why the SQS/Kafka legs
/// live in <see cref="WorkerStartup"/> instead - two <c>BenzeneStartUp</c>s, sharing the same
/// <c>builder.Services</c>, is what lets one process own all three transports.
/// </summary>
public class HttpStartup : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.UsingBenzene(x => x
            .AddMessageHandlers(new[] { typeof(PlaceOrderMessageHandler) })
            .AddHttpMessageHandlers());
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        app.UseHttp(asp => asp.UseMessageHandlers());
    }
}
