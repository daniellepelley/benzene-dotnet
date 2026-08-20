using Benzene.AspNet.Core;
using Benzene.CloudService;
using Benzene.Clients.HealthChecks;
using Benzene.Examples.Mesh.OrdersService.Clients;
using Benzene.Examples.Mesh.OrdersService.HealthChecks;
using Benzene.Examples.Mesh.OrdersService.Model;
using Benzene.HealthChecks;
using Benzene.Http.Routing;
using Benzene.Mesh.Wire;
using Benzene.Microsoft.Dependencies;
using Benzene.ResponseEvents;
using Microsoft.Extensions.DependencyInjection;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();
        services.UsingBenzene(x => x
            // The generated-style downstream client whose contract orders-api checks (see below), and
            // the HTTP route that lets a raw GET /contracts resolve to the "contracts" topic - the
            // ASP.NET wiring pattern from docs/kubernetes-health-checks.md.
            .AddSingleton<PaymentsContractClient>(_ => new PaymentsContractClient())
            .AddSingleton<IHttpEndpointDefinition>(_ =>
                new HttpEndpointDefinition("GET", "/contracts", Constants.DefaultContractsTopic))
            // orders-api is pinned to payments:get v1 while payments-api's handler has moved to v2.
            // Declaring the produced event (spec `events`) puts a payments:get@1 producer in the fleet, so
            // the mesh's version compatibility view shows the skew (produced v1, consumed v2) - bridged at
            // runtime by payments-api's upcaster, which the mesh can't see. See versioning.md.
            .AddResponseEventDeclarations(
                new ResponseEventDefinition(new Benzene.Core.Messages.Topic("payments:get", "1"), typeof(PaymentsQueryV1))));
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();

        // Benzene Cloud Service setup (docs/specification/cloud-service-profile.md): this one call
        // replaces the old hand-wired /benzene/invoke branch + StartAnnouncing() (MeshHost.cs,
        // deleted) with the wire-envelope endpoint, health checks (reserved topic + HTTP), the
        // derived spec, message handlers via the registry, and the mesh service-side feeds
        // (descriptor, registration, heartbeats, trace) - all pre-wired in the right order. Handler
        // discovery stays attribute-based ([Message]/[HttpEndpoint] on GetOrdersMessageHandler /
        // CheckoutOrderMessageHandler), same idiom as before. Spec/health stay at this demo's
        // pre-existing /spec and /healthcheck paths (relocated from the /benzene/ defaults) so the
        // aggregator's polling and run.sh keep working unchanged - the profile report honestly
        // flags R7 for that deliberate relocation.
        app.UseBenzene(benzene => benzene
            .UseHttp(asp => asp
                // A SEPARATE diagnostic surface for orders-api's consumer-side contract-drift check
                // against payments-api, on the "contracts" topic (GET /contracts) - deliberately NOT
                // on /healthcheck. A contract check calls a downstream service, so keeping it out of
                // the health/readiness surface is what stops a drifted or slow payments-api from
                // de-routing orders-api. Added before the (terminal) cloud-service call, per its
                // "app middleware goes first" contract. See docs/kubernetes-health-checks.md and
                // work/archive/client-health-checks-design-2026-08.md.
                .UseContractsCheck(x => x.AddContractCheck<PaymentsContractClient>("payments-api"))
                .UseBenzeneCloudService("orders-api", cloud => cloud
                    .WithServiceVersion("1.0.0")
                    .WithInstanceId("orders-api-1")
                    .WithHealthChecks(new OrdersDatabaseHealthCheck(), new OrdersCacheHealthCheck(), new OrdersQueueHealthCheck())
                    .WithCollector(Environment.GetEnvironmentVariable("MESH_COLLECTOR_ENVELOPE_URL")
                        ?? "http://localhost:5300/benzene/invoke")
                    // mesh.md §2.3 outbound registration: orders-api explicitly declares that it CONSUMES
                    // payments:get@1 (CheckoutOrderMessageHandler's cross-service call) - no handler, no
                    // destination, the same PaymentsQueryV1 request shape the pre-existing response-event
                    // declaration above already models. This is what puts a declared consumer edge on the
                    // collector's Fleet view (spec §4, 2026-08 revision) for payments:get - present even
                    // before checkout is ever called, and dropped automatically if a future redeploy stops
                    // declaring it (re-registration replaces `consumes` wholesale, spec §4).
                    .WithConsumes(new MeshOutboundRegistry().Register<PaymentsQueryV1>("payments:get", "1"))
                    .WithSpecPath("/spec")
                    .WithHealthPath("/healthcheck")
                )
            )
        );

        app.UseEndpoints(endpoints => { });
    }
}
