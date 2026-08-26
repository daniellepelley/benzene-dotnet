using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Middleware;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Hosting;

// Regression coverage for #104: both ASP.NET call sites (AspNetServerWorker.StartAsync's app.Run
// handler, and AspApplicationBuilder.Add's middleware) must forward HttpContext.RequestAborted into
// the token-taking SendAsync(event, cancellationToken) overload, not the one-arg overload. This
// entry point explicitly implements both overloads (rather than relying on the interface's default
// forwarding member) so a call to the one-arg overload and a call to the two-arg overload are
// observably different, isolating exactly what each call site does.
public class CapturingEntryPoint : IEntryPointMiddlewareApplication<HttpContext>
{
    public CancellationToken? Captured { get; private set; }

    public Task SendAsync(HttpContext @event)
    {
        Captured = CancellationToken.None;
        return Task.CompletedTask;
    }

    public Task SendAsync(HttpContext @event, CancellationToken cancellationToken)
    {
        Captured = cancellationToken;
        return Task.CompletedTask;
    }
}

public class AspApplicationBuilderCaptureStartUp : BenzeneStartUp
{
    // Set by the test before the host is built - the startup is `new()`-constructed by UseBenzene,
    // so there's no constructor to pass it through (same static-state pattern as UseAspNetWorkerTest).
    public static CapturingEntryPoint EntryPoint = new();

    public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.UsingBenzene(x => x.AddBenzene());

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        // Registering the entry point directly through IAspApplicationBuilder.Add, bypassing
        // UseHttp/BuildHttpPipeline entirely - so this test observes AspApplicationBuilder.Add's own
        // forwarding, not the AspNetContext pipeline's independent "SeedCancellationToken" middleware.
        if (app is IAspApplicationBuilder aspApp)
        {
            aspApp.Add(_ => EntryPoint);
        }
    }
}

public class AspNetCancellationForwardingTest
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
    public async Task AspApplicationBuilder_Add_ForwardsRequestAbortedToEntryPoint()
    {
        AspApplicationBuilderCaptureStartUp.EntryPoint = new CapturingEntryPoint();

        var builder = WebApplication.CreateBuilder();
        builder.UseBenzene<AspApplicationBuilderCaptureStartUp>();
        var app = builder.Build();
        app.UseBenzene();
        var requestDelegate = ((IApplicationBuilder)app).Build();

        using var cts = new CancellationTokenSource();
        var httpContext = new DefaultHttpContext
        {
            Request = { Method = "GET", Path = "/" },
            Response = { Body = new MemoryStream() },
            RequestAborted = cts.Token,
        };

        await requestDelegate(httpContext);

        var captured = AspApplicationBuilderCaptureStartUp.EntryPoint.Captured;
        Assert.NotNull(captured);
        Assert.True(captured!.Value.CanBeCanceled);
        Assert.Equal(cts.Token, captured.Value);
    }

    [Fact]
    public async Task AspNetServerWorker_StartAsync_ForwardsRequestAbortedToEntryPoint()
    {
        var entryPoint = new CapturingEntryPoint();
        var port = GetFreePort();
        var worker = new AspNetServerWorker(entryPoint, new AspNetServerOptions { Urls = $"http://127.0.0.1:{port}" });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient();
            await client.GetAsync($"http://127.0.0.1:{port}/anything");

            var captured = entryPoint.Captured;
            Assert.NotNull(captured);
            // A real Kestrel request's RequestAborted token is always cancellable (it fires on
            // client disconnect); CancellationToken.None never is - so this distinguishes the
            // token-forwarding overload from the one-arg overload without depending on timing.
            Assert.True(captured!.Value.CanBeCanceled);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
