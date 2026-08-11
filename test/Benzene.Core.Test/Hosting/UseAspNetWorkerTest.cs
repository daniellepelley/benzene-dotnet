using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.HostedService;
using Benzene.Http.Routing;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Test.Hosting;

// UseAspNet hosts Kestrel as an IBenzeneWorker inside a Worker-platform startup, so one startup can
// wire HTTP alongside the other self-hosted transports (the K8sTransports shape). This test drives
// it end to end over a REAL socket on the generic host: the same startup registers the HTTP server
// AND a second worker, both start/stop through the one composite hosted service, and the handler
// observes a singleton mutated through the host's own root provider - proving the worker-hosted
// pipeline resolves from the outer container, never a second provider inside the inner
// WebApplication (the singleton-split hazard AspNetSharedSingletonTest guards the embedded path
// against cannot arise here by construction; this asserts it anyway).
public class UseAspNetWorkerStartUp : BenzeneStartUp
{
    // Set by the test before the host is built (the startup is `new()`-constructed by UseBenzene,
    // so there's no constructor to pass it through) - same static-state pattern as FakeWorker.
    public static int Port;

    public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddSingleton<SharedCounter>()
            .AddBenzene()
            .AddMessageHandlers()
            .AddScoped<IncrementHandler>()
            .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                "useaspnet-worker-increment", "", typeof(IncrementRequest), typeof(IncrementResponse), typeof(IncrementHandler)))
            .AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("POST", "/increment", "useaspnet-worker-increment")));

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
        .UseWorker(worker => worker
            .UseAspNet(
                asp => asp.UseMessageHandlers(),
                options => options.Urls = $"http://127.0.0.1:{Port}")
            .Add(_ => new FakeWorker()));
}

public class UseAspNetWorkerTest
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task GenericHost_ServesHttpThroughUseAspNet_AlongsideAnotherWorker()
    {
        FakeWorker.Reset();
        UseAspNetWorkerStartUp.Port = GetFreePort();

        var host = new HostBuilder()
            .UseBenzene<UseAspNetWorkerStartUp>()
            .Build();

        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        Assert.NotEmpty(hostedServices);

        foreach (var service in hostedServices)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            // Both legs of the one composite worker started - HTTP didn't crowd out its sibling.
            Assert.True(FakeWorker.Started);

            // Mutate the singleton through the host's own root provider, then observe the mutation
            // through the handler served over the real socket.
            host.Services.GetRequiredService<SharedCounter>().Value = 41;

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{UseAspNetWorkerStartUp.Port}/increment",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(42, document.RootElement.GetProperty("value").GetInt32());

            // No controllers to fall through to in this mode - an unrouted request is a plain 404.
            var unrouted = await client.GetAsync(
                $"http://127.0.0.1:{UseAspNetWorkerStartUp.Port}/no-such-route");
            Assert.Equal(HttpStatusCode.NotFound, unrouted.StatusCode);
        }
        finally
        {
            foreach (var service in hostedServices)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        Assert.True(FakeWorker.Stopped);

        // StopAsync actually released the socket, not just stopped answering.
        using var probe = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            probe.GetAsync($"http://127.0.0.1:{UseAspNetWorkerStartUp.Port}/increment"));
    }
}
