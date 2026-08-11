using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Examples.K8sTransports.Domain;
using Benzene.Http;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.K8sTransports.Api;

/// <summary>
/// The HTTP leg: an ordinary ASP.NET Core container exposing <c>POST /orders</c>. There is nothing
/// Kubernetes-specific here - it's hosted exactly like
/// <a href="../../../docs/getting-started-aspnet.md">Getting Started: ASP.NET Core</a>, just pointed
/// at the shared <see cref="PlaceOrderMessageHandler"/> instead of one it defines itself.
/// </summary>
public class Startup : BenzeneStartUp
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
