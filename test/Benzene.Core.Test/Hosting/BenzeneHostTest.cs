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
using Benzene.Abstractions.Serialization;
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

// BenzeneHost.Run/RunAsync/Build reduce a Benzene service's entry point to one line by owning the
// generic host. The point is not brevity: it is that the STARTUP becomes the only place hosting is
// described, so adding a transport later never touches Program.cs. These tests hold that promise to
// the same standard as the hand-rolled host - a real socket, a real worker, the host's own root
// provider - because a shorthand that behaves differently from what it replaces is worse than no
// shorthand.
public class BenzeneHostStartUp : BenzeneStartUp
{
    // Set before the host is built; the startup is `new()`-constructed by UseBenzene, so there is no
    // constructor to pass it through (the same static-state pattern as UseAspNetWorkerStartUp).
    public static int Port;

    public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddSingleton<HostCounter>()
            .AddBenzene()
            .AddMessageHandlers()
            .AddScoped<HostCounterHandler>()
            .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                "benzenehost-count", "", typeof(CountRequest), typeof(CountResponse), typeof(HostCounterHandler)))
            .AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("POST", "/count", "benzenehost-count")));

    // Everything about hosting lives here, which is the whole claim: HTTP is declared alongside a
    // background worker, and the entry point knows about neither.
    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
        .UseWorker(worker => worker
            .UseAspNet(
                asp => asp.UseMessageHandlers(),
                options => options.Urls = $"http://127.0.0.1:{Port}")
            .Add(_ => new FakeWorker()));
}

public class HostCounter
{
    public int Value { get; set; }
}

/// <summary>A stand-in for the JSON serializer AddBenzene TryAdds, so the substitution is visible.</summary>
public class StubSerializer : ISerializer
{
    public string Serialize(Type type, object payload) => "{}";

    public string Serialize<T>(T payload) => "{}";

    public object? Deserialize(Type type, string payload) => null;

    public T? Deserialize<T>(string payload) => default;
}

public class CountRequest
{
}

public class CountResponse
{
    public int Value { get; set; }
}

public class HostCounterHandler : IMessageHandler<CountRequest, CountResponse>
{
    private readonly HostCounter _counter;

    public HostCounterHandler(HostCounter counter)
    {
        _counter = counter;
    }

    public Task<Benzene.Abstractions.Results.IBenzeneResult<CountResponse>> HandleAsync(CountRequest request)
        => Task.FromResult(Benzene.Results.BenzeneResult.Ok(new CountResponse { Value = ++_counter.Value }));
}

public class BenzeneHostTest
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
    public void Build_RegistersTheStartUpsWorkersWithoutRunningThem()
    {
        BenzeneHostStartUp.Port = GetFreePort();
        FakeWorker.Reset();

        using var host = BenzeneHost.Build<BenzeneHostStartUp>();

        // Built, not started - Build is the escape hatch for a caller who needs the IHost, and it
        // must not have side effects on the way out.
        Assert.NotEmpty(host.Services.GetServices<IHostedService>());
        Assert.False(FakeWorker.Started);

        // The startup's own registrations are resolvable from the host's root provider, so a caller
        // can reach into the service the same way a test or a migration would.
        Assert.NotNull(host.Services.GetRequiredService<HostCounter>());
    }

    [Fact]
    public async Task RunAsync_ServesTheStartUpsTransports_AndStopsOnCancellation()
    {
        BenzeneHostStartUp.Port = GetFreePort();
        FakeWorker.Reset();

        using var cts = new CancellationTokenSource();
        var run = BenzeneHost.RunAsync<BenzeneHostStartUp>(cancellationToken: cts.Token);

        using var client = new HttpClient();
        await WaitUntilServing(client, BenzeneHostStartUp.Port);

        // The HTTP leg answers over a real socket...
        var response = await client.PostAsync(
            $"http://127.0.0.1:{BenzeneHostStartUp.Port}/count",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("value").GetInt32());

        // ...and its sibling worker started too. One entry point, every transport the startup
        // declared - which is the property that makes adding a queue consumer a one-line change.
        Assert.True(FakeWorker.Started);

        await cts.CancelAsync();
        await run;

        // Shutdown is the generic host's, so the worker's own StopAsync ran and the socket is gone.
        Assert.True(FakeWorker.Stopped);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://127.0.0.1:{BenzeneHostStartUp.Port}/count"));
    }

    [Fact]
    public void ConfigureHost_RunsFirst_SoACallerCanSubstituteABenzeneDefault()
    {
        // The ordering that makes the escape hatch usable rather than decorative. Benzene's baseline
        // services are TryAdd (AddBenzene's own comment on ISerializer says "so a user registration
        // in ConfigureServices wins"), and TryAdd means FIRST registration wins - so a caller
        // substituting one has to land before UseBenzene runs the startup. If configureHost ran
        // afterwards this substitution would silently not take effect, and the failure would be a
        // subtly different serializer in production rather than an exception anywhere.
        BenzeneHostStartUp.Port = GetFreePort();
        var substitute = new StubSerializer();

        using var host = BenzeneHost.Build<BenzeneHostStartUp>(
            configureHost: builder => builder.ConfigureServices(services =>
                services.AddSingleton<ISerializer>(substitute)));

        Assert.Same(substitute, host.Services.GetRequiredService<ISerializer>());
    }

    [Fact]
    public void ConfigureHost_DoesNotOverrideWhatTheStartUpItselfRegisters()
    {
        // The other half of the same rule, asserted so nobody has to discover it. `configureHost`
        // lands first, so a plain (non-TryAdd) registration the STARTUP makes is added after it and
        // wins under Microsoft DI's last-one-wins resolution. That is the right way round - the
        // startup is the caller's own code, and the thing they cannot otherwise reach is Benzene's
        // internal defaults - but it is surprising if you assume `configureHost` overrides
        // everything.
        BenzeneHostStartUp.Port = GetFreePort();
        var ignored = new HostCounter { Value = 41 };

        using var host = BenzeneHost.Build<BenzeneHostStartUp>(
            configureHost: builder => builder.ConfigureServices(services => services.AddSingleton(ignored)));

        Assert.NotSame(ignored, host.Services.GetRequiredService<HostCounter>());
    }

    private static async Task WaitUntilServing(HttpClient client, int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await client.GetAsync($"http://127.0.0.1:{port}/count");
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"The host never began serving on port {port}.");
    }
}
