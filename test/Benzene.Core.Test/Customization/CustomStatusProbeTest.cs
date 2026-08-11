using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.HostedService;
using Benzene.Http;
using Benzene.Http.Routing;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Test.Customization;

// Customization-robustness probes over a REAL socket (via UseAspNet): a handler returning an
// APPLICATION-DEFINED status string ("quarantined"), with and without user-supplied overrides
// registered in ConfigureServices - the officially advertised override point.
public class QuarantineDto
{
    public string Name { get; set; } = "";
}

public class QuarantineRequest
{
}

public class QuarantineHandler : IMessageHandler<QuarantineRequest, QuarantineDto>
{
    public Task<IBenzeneResult<QuarantineDto>> HandleAsync(QuarantineRequest request)
        => Task.FromResult(BenzeneResult.Set("quarantined", new QuarantineDto { Name = "acme" }));
}

public class CustomStatusStartUp : BenzeneStartUp
{
    public static int Port;

    public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddBenzene()
            .AddMessageHandlers()
            .AddScoped<QuarantineHandler>()
            .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                "probe-quarantine", "", typeof(QuarantineRequest), typeof(QuarantineDto), typeof(QuarantineHandler)))
            .AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("POST", "/quarantine", "probe-quarantine")));

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
        .UseWorker(worker => worker.UseAspNet(
            asp => asp.UseMessageHandlers(),
            options => options.Urls = $"http://127.0.0.1:{Port}"));
}

// Same startup, but the user ALSO registers their own IHttpStatusCodeMapper in ConfigureServices -
// the documented way to give a custom status a real HTTP code (the seam is TryAdd'd, so the user's
// earlier registration should win).
public class MapperOverrideStartUp : CustomStatusStartUp
{
    public class QuarantineAwareMapper : IHttpStatusCodeMapper
    {
        private readonly DefaultHttpStatusCodeMapper _inner = new();
        public string Map(string benzeneResultStatus)
            => benzeneResultStatus == "quarantined" ? "409" : _inner.Map(benzeneResultStatus);
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.UsingBenzene(x => x.AddScoped<IHttpStatusCodeMapper, QuarantineAwareMapper>());
        base.ConfigureServices(services, configuration);
    }
}

// Regression probe for the per-context transport seams: the user registers a custom ISerializer in
// ConfigureServices. AddAspNetMessageHandlers registers its JsonSerializer default with TryAdd, so
// the user's earlier registration now wins the ISerializer resolve. NOTE the HTTP response body still
// does NOT carry the marker: the render/request-mapping path serializes via the media-format
// negotiator, whose default JsonMediaFormat wraps the concrete JsonSerializer and never resolves the
// ISerializer seam - a separate (pre-existing) fact from the Add-vs-TryAdd ordering this fixes.
public class SerializerOverrideStartUp : CustomStatusStartUp
{
    public class MarkerSerializer : ISerializer
    {
        private readonly Benzene.Core.MessageHandlers.Serialization.JsonSerializer _inner = new();
        public string Serialize<T>(T value) => "MARKER:" + _inner.Serialize(value);
        public T Deserialize<T>(string value) => _inner.Deserialize<T>(value);
        public string Serialize(Type type, object value) => "MARKER:" + _inner.Serialize(type, value);
        public object Deserialize(Type type, string value) => _inner.Deserialize(type, value);
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.UsingBenzene(x => x.AddScoped<ISerializer, MarkerSerializer>());
        base.ConfigureServices(services, configuration);
    }
}

public class CustomStatusProbeTest
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int StatusCode, string Body)> PostAsync<TStartUp>(int port, string path = "/quarantine")
        where TStartUp : BenzeneStartUp, new()
    {
        var host = new HostBuilder().UseBenzene<TStartUp>().Build();
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        foreach (var service in hostedServices)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}{path}",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, body);
        }
        finally
        {
            foreach (var service in hostedServices)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task CustomStatus_NoMapperOverride_Probe()
    {
        CustomStatusStartUp.Port = GetFreePort();
        var (status, body) = await PostAsync<CustomStatusStartUp>(CustomStatusStartUp.Port);
        // A custom status maps to the generic-error row (500, spec-pinned) while IsSuccessful=true
        // renders the SUCCESS payload as the body - the split-brain combination, confirmed here.
        Assert.True(500 == status, $"status={status} body={body}");
        Assert.Contains("acme", body);
    }

    [Fact]
    public async Task CustomStatus_WithUserMapperInConfigureServices_Probe()
    {
        CustomStatusStartUp.Port = GetFreePort();
        var (status, body) = await PostAsync<MapperOverrideStartUp>(CustomStatusStartUp.Port);
        // IHttpStatusCodeMapper is TryAdd'd by the framework, so the user's earlier registration wins.
        Assert.True(409 == status, $"status={status} body={body}");
        Assert.Contains("acme", body);
    }

    [Fact]
    public async Task CustomSerializer_InConfigureServices_Probe()
    {
        CustomStatusStartUp.Port = GetFreePort();
        var (status, body) = await PostAsync<SerializerOverrideStartUp>(CustomStatusStartUp.Port);
        // The body stays plain JSON: the response is rendered through the media-format negotiator's
        // default JsonMediaFormat (which wraps the concrete JsonSerializer), not the ISerializer seam,
        // so the marker cannot appear here regardless of registration order.
        Assert.Contains("acme", body);
        Assert.True(!body.Contains("MARKER:"), $"body unexpectedly flowed through ISerializer: status={status} body={body}");

        // The seam itself IS now honored: the transport TryAdds its JsonSerializer default, so the
        // user's earlier ConfigureServices registration wins the single resolve (before the TryAdd
        // conversion the framework's later plain AddScoped silently shadowed it).
        using var host = new HostBuilder().UseBenzene<SerializerOverrideStartUp>().Build();
        using var scope = host.Services.CreateScope();
        Assert.IsType<SerializerOverrideStartUp.MarkerSerializer>(
            scope.ServiceProvider.GetRequiredService<ISerializer>());
    }
}
