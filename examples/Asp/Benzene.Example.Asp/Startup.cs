using System;
using Benzene.AspNet.Core;
using Benzene.Auth.OAuth2;
using Benzene.Example.Asp.Cancellation;
using Benzene.Example.Asp.DemoAuth;
using Benzene.Examples.App.Data;
using Benzene.Examples.App.Logging;
using Benzene.Examples.App.Services;
using Benzene.Examples.App.Validators;
using Benzene.Microsoft.Dependencies;
using FluentValidation;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages;
using Benzene.Core.Middleware;
using Benzene.Diagnostics.Correlation;
using Benzene.FluentValidation;
using Benzene.Http.Routing;
using Benzene.Resilience;
using Benzene.Schema.OpenApi;
using Benzene.Spec.Ui;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Benzene.Example.Asp;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        // #218: instrumentation key comes from config (config.json / env), never hardcoded - the
        // shipped config.json value is an obvious placeholder GUID, not a real key. Replace it (or
        // set the APPINSIGHTS_INSTRUMENTATIONKEY environment variable) with your own before this
        // sink actually delivers telemetry anywhere.
        var appInsightsKey = Configuration["APPINSIGHTS_INSTRUMENTATIONKEY"] ?? "00000000-0000-0000-0000-000000000000";
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(new CustomJsonFormatter())
            .WriteTo.ApplicationInsights(new TelemetryConfiguration(appInsightsKey),
                TelemetryConverter.Traces)
            .CreateLogger();

        services.AddLogging();
        services.AddScoped<ILogger, Logger<string>>();
        services.AddControllers();

        services.AddSingleton(Configuration);

        services.AddScoped<IOrderDbClient, InMemoryOrderDbClient>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddSingleton<IMessageHandlerDefinition>(_ =>
            MessageHandlerDefinition.CreateInstance("benzene:spec", "", typeof(SpecRequest), typeof(RawStringMessage),
                typeof(SpecMessageHandler)));
        services.AddScoped<SpecMessageHandler>();
        services.AddSingleton<IHttpEndpointDefinition>(_ => new HttpEndpointDefinition("get", "/spec", "benzene:spec"));

        // Demo-only fake identity provider (docs/cookbooks/auth-patterns.md) - see DemoAuth/.
        // A real service points OAuth2BearerOptions at a real identity provider instead.
        services.AddSingleton<DemoJwtIssuer>();

        services.UsingBenzene(x => x.SetApplicationInfo(
            "Benzene ASP.NET Example",
            "1.0.0",
            "Example ASP.NET Core host demonstrating Benzene message handlers, validation, and the derived OpenAPI spec."));

        services.AddValidatorsFromAssemblyContaining<GetOrderMessageValidator>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseHttpsRedirection();

        app.UseRouting();

        app.UseBenzene(benzene => benzene
            .UseHttp(asp => asp
                .UseSpec()
                .UseSpecUi()          // browse the spec at GET /spec-ui (fetches /spec?type=benzene)
                .UseMessageHandlers(x => x.UseFluentValidation())
            )
        );

        // A protected route (docs/cookbooks/auth-patterns.md), isolated with app.Map so it never
        // reaches the public pipeline above: Benzene's message router is unconditionally terminal
        // for any request it sees (it always answers, even NotFound, and never falls through to a
        // sibling UseHttp pipeline) - so branching by URL prefix BEFORE Benzene's own pipeline runs,
        // via plain ASP.NET Core Map, is what actually isolates a protected route from a public one
        // in the same app. UseOAuth2Bearer validates the caller's bearer token against the demo
        // identity provider's JWKS (DemoAuthController), and RequireScope("orders:read") then
        // requires that specific scope. Try it:
        //   curl http://localhost:5000/demo-token?scope=orders:read      # mint a token
        //   curl -H "Authorization: Bearer <token>" http://localhost:5000/protected/ping
        app.Map("/protected", protectedApp =>
        {
            protectedApp.UseRouting();
            protectedApp.UseBenzene(benzene => benzene
                .UseHttp(asp => asp
                    .UseOAuth2Bearer(new OAuth2BearerOptions
                    {
                        JwksUri = $"{DemoJwtIssuer.Issuer}.well-known/jwks.json",
                        ValidIssuers = new[] { DemoJwtIssuer.Issuer },
                        ValidAudiences = new[] { DemoJwtIssuer.Audience },
                        ValidAlgorithms = new[] { "RS256" },
                        // The demo identity provider above is this same app, over plain HTTP - never
                        // do this against a real identity provider (see OAuth2BearerOptions.RequireHttpsMetadata).
                        RequireHttpsMetadata = false
                    })
                    .RequireScope("orders:read")
                    .UseMessageHandlers(typeof(ProtectedPingMessageHandler))
                )
            );
            protectedApp.UseEndpoints(endpoints => { });
        });

        // Cancellation demo (docs/message-handlers.md#cancellation): isolated the same way the
        // /protected branch is above, so .UseTimeout(...) only applies a deadline to this one
        // route rather than every request the service handles. SlowOperationMessageHandler
        // injects ICancellationTokenAccessor and reads its token at the point of use; the 2-second
        // timeout here fires before the handler's simulated 5-second call completes, so the
        // response comes back as a "timeout" failure result instead of an aborted connection. Try
        // it: curl http://localhost:5000/slow/slow-operation
        app.Map("/slow", slowApp =>
        {
            slowApp.UseRouting();
            slowApp.UseBenzene(benzene => benzene
                .UseHttp(asp => asp
                    .UseTimeout(TimeSpan.FromSeconds(2))
                    .UseMessageHandlers(typeof(SlowOperationMessageHandler))
                )
            );
            slowApp.UseEndpoints(endpoints => { });
        });

        // ASP0001: UseAuthorization() belongs immediately before endpoint mapping, not earlier in the
        // chain - the app.Map("/protected", ...) / app.Map("/slow", ...) branches above sit between
        // routing and this app's own endpoints, and each guards itself with Benzene's own auth
        // (UseOAuth2Bearer) rather than ASP.NET Core's UseAuthorization(), which only applies to
        // MapControllers() below.
        app.UseAuthorization();
        app.UseEndpoints(endpoints => { endpoints.MapControllers(); });
    }
}