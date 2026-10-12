using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Messages;
using Benzene.Clients;
using Benzene.Clients.GoogleCloud.PubSub;
using Benzene.Examples.GoogleCloudMesh.Shared;
using Benzene.HealthChecks.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.ResponseEvents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Benzene.Mesh.Wire;

namespace Benzene.Examples.GoogleCloudMesh.Shipping;

/// <summary>shipping-api: consumes shipment:book (Pub/Sub or HTTP), publishes shipment:dispatched.</summary>
public class Startup : BenzeneStartUp
{
    private static readonly Type[] Handlers = { typeof(BookShipmentHandler) };


    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => MeshServiceWiring.ConfigureServices(services, "shipping", Handlers, x =>
        {
            x.AddResponseEventDeclarations(
                (IMessageDefinition)new ResponseEventDefinition("shipment:dispatched", typeof(OutboundShipmentDispatched)));
            x.AddPubSubPublisher();
            x.AddOutboundRouting(routing => routing
                // Forward the mesh span (mesh.md §3, M2) on every route, so a downstream TraceEvent names this
                // invocation's span as its parent - and the cloud service's M2 self-check reports it wired.
                .UseMeshTraceContext()
                .Route("shipment:dispatched", pipeline => pipeline.UsePubSub(Environment.GetEnvironmentVariable("SHIPMENT_DISPATCHED_TOPIC") ?? "shipment-dispatched")));
        });

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        IHealthCheck[] healthChecks = { new ServiceHealthCheck("shipping") };
        MeshServiceWiring.Configure(app, "shipping", Handlers, healthChecks);
    }
}
