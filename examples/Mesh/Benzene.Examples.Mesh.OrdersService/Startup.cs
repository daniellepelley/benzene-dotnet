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
                new HttpEndpointDefinition("GET", "/contracts", Constants.DefaultContractsTopic)));
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();

        // Benzene Cloud Service setup (docs/specification/cloud-service-profile.md): this one call
        // replaces the old hand-wired /benzene/invoke branch + StartAnnouncing() (MeshHost.cs,
        // deleted) with the wire-envelope endpoint, health checks (reserved topic + HTTP), the
        // derived spec, message handlers via the registry, and the mesh service-side feeds
        // (registration of that spec, heartbeats, trace) - all pre-wired in the right order. Handler
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
                    // A send declaration (contract-document.md §3.2): orders-api sends payments:get@1
                    // (CheckoutOrderMessageHandler's cross-service call) to a topic payments-api defines -
                    // no handler, no destination. It is in orders-api's contract document's sends, so its
                    // registration makes orders-api a declared provider of payments:get@1 on the
                    // collector's Fleet view (mesh.md §4) - present before checkout is ever called, and
                    // dropped if a redeploy stops declaring it. orders-api is pinned to v1 while
                    // payments-api's handler has moved to v2, so the mesh's version compatibility view
                    // shows the skew (sent v1, served v2) - bridged at runtime by payments-api's upcaster,
                    // which the mesh can't see. See versioning.md. (A send, not an event: a (topic,
                    // version) declared as both fails the document, contract-document.md §3.3.)
                    .WithSends(new MeshOutboundRegistry().Register<PaymentsQueryV1>("payments:get", "1"))
                    .WithSpecPath("/spec")
                    .WithHealthPath("/healthcheck")
                )
            )
        );

        app.UseEndpoints(endpoints => { });
    }
}
