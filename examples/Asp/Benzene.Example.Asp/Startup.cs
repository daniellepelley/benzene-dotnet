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
        // Demo-only: no real Application Insights key is checked into this example. Put a real
        // instrumentation key in config.json ("APPINSIGHTS_INSTRUMENTATIONKEY") or the
        // APPINSIGHTS_INSTRUMENTATIONKEY environment variable to see telemetry flow; with the
        // placeholder default below, ApplicationInsights sends nowhere and the console sink still
        // works.
        var appInsightsKey = Configuration["APPINSIGHTS_INSTRUMENTATIONKEY"] ?? string.Empty;

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

        app.UseAuthorization();

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
        // requires that specific scope - this is Benzene's own auth, not ASP.NET Core's
        // UseAuthorization/UseEndpoints above, so this branch has no UseRouting()/UseEndpoints() of
        // its own (ASP0001: an extra UseRouting()/UseEndpoints() pair in a branch that never maps an
        // endpoint confuses the analyzer into misjudging the real UseAuthorization/UseEndpoints pair
        // above as out of order). Try it:
        //   curl http://localhost:5000/demo-token?scope=orders:read      # mint a token
        //   curl -H "Authorization: Bearer <token>" http://localhost:5000/protected/ping
        //
        // The demo issuer's base URL (and therefore JwksUri below) defaults to http://localhost:5000/
        // - see DemoJwtIssuer.Issuer's doc comment for what breaks (an opaque 401 on every
        // /protected/* request) if the app is run on a different port without also setting
        // DEMO_AUTH_ISSUER to match.
        var demoJwtIssuer = app.ApplicationServices.GetRequiredService<DemoJwtIssuer>();
        app.Map("/protected", protectedApp =>
        {
            protectedApp.UseBenzene(benzene => benzene
                .UseHttp(asp => asp
                    .UseOAuth2Bearer(new OAuth2BearerOptions
                    {
                        JwksUri = demoJwtIssuer.JwksUri,
                        ValidIssuers = new[] { demoJwtIssuer.Issuer },
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
            slowApp.UseBenzene(benzene => benzene
                .UseHttp(asp => asp
                    .UseTimeout(TimeSpan.FromSeconds(2))
                    .UseMessageHandlers(typeof(SlowOperationMessageHandler))
                )
            );
        });

        app.UseEndpoints(endpoints => { endpoints.MapControllers(); });
    }
}