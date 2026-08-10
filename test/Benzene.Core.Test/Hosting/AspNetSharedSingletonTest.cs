using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http.Routing;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Hosting;

// Regression coverage for a real bug: AspApplicationBuilder resolves message handlers through a
// SEPARATE IServiceProvider from the ASP.NET host's own root one (see AspApplicationBuilder.cs and
// MicrosoftBenzeneServiceContainer.Reopen()'s remarks). Before the fix, a singleton registered in
// StartUp.ConfigureServices - like this counter - would exist as two different instances: one the
// host's root provider (app.Services, and anything resolved outside Benzene's routing, e.g. a plain
// IHostedService) hands out, and a second one Benzene message handlers see. This test asserts they
// are the same instance by having a message handler observe a mutation made directly against
// app.Services before the request runs.
public class SharedCounter
{
    public int Value;
}

public class IncrementRequest
{
}

public class IncrementResponse
{
    public int Value { get; set; }
}

// Deliberately not annotated with [Message]/[HttpEndpoint] - see AspNetUnifiedStartUpTest.cs's
// PingHandler comment for why (avoids polluting AppDomain-wide reflection scans in other tests).
public class IncrementHandler : IMessageHandler<IncrementRequest, IncrementResponse>
{
    private readonly SharedCounter _counter;

    public IncrementHandler(SharedCounter counter) => _counter = counter;

    public Task<IBenzeneResult<IncrementResponse>> HandleAsync(IncrementRequest request)
    {
        _counter.Value++;
        return Task.FromResult(BenzeneResult.Ok(new IncrementResponse { Value = _counter.Value }));
    }
}

public class AspNetSharedSingletonStartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration() => new ConfigurationBuilder().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddSingleton<SharedCounter>()
            .AddBenzene()
            .AddMessageHandlers()
            .AddScoped<IncrementHandler>()
            .AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                "aspnet-shared-singleton-increment", "", typeof(IncrementRequest), typeof(IncrementResponse), typeof(IncrementHandler)))
            .AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("POST", "/increment", "aspnet-shared-singleton-increment")));

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration) => app
        .UseHttp(http => http.UseMessageHandlers());
}

public class AspNetSharedSingletonTest
{
    [Fact]
    public async Task MessageHandler_ObservesSingletonMutatedThroughRootProvider()
    {
        var builder = WebApplication.CreateBuilder();
        builder.UseBenzene<AspNetSharedSingletonStartUp>();

        var app = builder.Build();
        app.UseBenzene();

        var requestDelegate = ((IApplicationBuilder)app).Build();

        // Simulates a plain IHostedService (or anything else resolved outside Benzene's own message
        // routing) mutating the singleton via the host's own root provider.
        var rootCounter = app.Services.GetRequiredService<SharedCounter>();
        rootCounter.Value = 41;

        var httpContext = new DefaultHttpContext
        {
            Request =
            {
                Method = "POST",
                Path = "/increment",
                ContentType = "application/json",
                Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"))
            },
            Response = { Body = new MemoryStream() }
        };

        await requestDelegate(httpContext);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEndAsync();

        // If the message handler resolved a separate SharedCounter instance (the bug this guards
        // against), Value would be 1 (0 + 1, never having seen the root provider's write of 41)
        // instead of 42 (41 + 1).
        using var document = JsonDocument.Parse(body);
        Assert.Equal(42, document.RootElement.GetProperty("value").GetInt32());
        Assert.Equal(42, rootCounter.Value);
    }
}
