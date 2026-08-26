using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Diagnostics;
using Benzene.Http;
using Benzene.Http.BenzeneMessage;
using Benzene.Http.Cors;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Discovery.Kubernetes;
using Benzene.Mesh.Ui;
using Benzene.Microsoft.Dependencies;
using Benzene.OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Benzene.Examples.K8sMesh.Mesh;

/// <summary>
/// The mesh service, hosted as an ASP.NET Core container in the cluster. It discovers the
/// benzene-labelled Kubernetes Services (via the Kubernetes API), writes the discovered registry, and
/// interrogates each over plain in-cluster HTTP — then serves the Mesh UI and catalog artifacts. A
/// background service re-runs the pass on an interval; <c>POST /mesh/refresh</c> triggers one on demand.
/// The catalog is stored on the pod's own volume (single writer + reader), so no blob store is needed.
/// </summary>
public class Startup : BenzeneStartUp
{

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var artifactDir = Environment.GetEnvironmentVariable("MESH_ARTIFACT_DIR") ?? "/artifacts";

        // The OTLP exporter is only attached when OTEL_EXPORTER_OTLP_ENDPOINT is set — the
        // instrumentation is armed either way, so there are no connection-refused errors without one.
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("benzene-mesh"))
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new AlwaysOnSampler()).AddBenzeneInstrumentation();
                if (!string.IsNullOrEmpty(otlpEndpoint)) tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddBenzeneInstrumentation();
                if (!string.IsNullOrEmpty(otlpEndpoint)) metrics.AddOtlpExporter();
            });

        services.UsingBenzene(benzene => benzene
            .AddDiagnostics()
            .AddMessageHandlers(typeof(Startup).Assembly)
            .AddHttpMessageHandlers()
            // Discovery starts with an empty registry — discovery replaces it at runtime.
            .AddMeshAggregator(new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>()), artifactDir)
            .AddMeshKubernetesDiscovery());

        // The one thing that differs between mesh hosts: where the registry for a pass comes from.
        // This host used to hand-roll the pass WITHOUT the single-writer gate its three siblings
        // had - the drift that made this seam worth extracting.
        services.AddSingleton(provider => new MeshAggregationPass(
            provider.GetRequiredService<IMeshArtifactStore>(),
            provider.GetRequiredService<MeshAggregator>(),
            cancellationToken =>
            {
                var ns = Environment.GetEnvironmentVariable("MESH_NAMESPACE");
                var filter = new MeshDiscoveryFilter(@namespace: string.IsNullOrWhiteSpace(ns) ? null : ns);
                return provider.GetRequiredService<MeshDiscoveryRunner>()
                    .DiscoverAsync(filter, cancellationToken: cancellationToken);
            }));
        services.AddHostedService<MeshAggregationBackgroundService>();

        // The live spec collector's in-memory state (Benzene.Mesh.Collector). A single always-on mesh
        // pod is exactly the right host for it - one process holds the accumulated registrations,
        // heartbeats, and traces the services push, and the Fleet view below reads it back. (This is
        // why the Fleet view fits K8sMesh but not the scale-to-zero Consumption Functions mesh.)
        services.AddSingleton<MeshCollectorStore>();
        services.AddSingleton<IMeshFleetReadModel>(sp => sp.GetRequiredService<MeshCollectorStore>());
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        // Scope handler discovery to this assembly so Benzene.Mesh.Aggregator's own
        // MeshAggregateMessageHandler (also [Message("benzene:mesh:aggregate")]) isn't discovered too.
        app.UseHttp(asp => asp
            .UseW3CTraceContext()
            .UseBenzeneEnrichment()
            .UseBenzeneMetrics()
            // #41 (WP-E): this endpoint had NO guard at all - an anonymous POST could trigger
            // unauthenticated Kubernetes-API discovery + a catalog rewrite. Same package, same wiring
            // as AzureMesh/AwsMesh: a required X-Benzene-Refresh header (CSRF) plus a manifest-age
            // throttle. See README's "Security posture" for what this does and does not cover.
            .UseMeshRefreshGuard(BuildRefreshGuardOptions())
            // The Mesh UI: the service catalog (what services declare, from the aggregator's pulled +
            // published manifest.json) enriched in-page with the live fleet — what the collector derives
            // from the services' own push feeds (what's actually running) — polled from /benzene/invoke.
            .UseMeshUi("/mesh-ui", "manifest.json", "/benzene/invoke")
            // The mesh-hosted per-service Spec UI (mesh-ui's "benzene:spec" link). Renders each service's spec
            // from the same-origin services/{name}.json snapshot, so a service only serves JSON.
            .UseMeshSpecUi("/mesh-spec-ui.html", "manifest.json")
            // Allow the AsyncAPI Studio deep-link to fetch asyncapi.json cross-origin. Uses
            // Benzene's own CORS support (Benzene.Http.Cors.CorsSettings); "*" would open it to
            // any origin, but scoping to Studio's origin keeps the example tight.
            .UseMeshArtifacts(new CorsSettings { AllowedDomains = new[] { "https://studio.asyncapi.com" } })
            // The live spec collector behind the wire-envelope endpoint the services report to
            // (register/heartbeat/trace) and the Mesh UI's Fleet plane queries (mesh:query:fleet). Its
            // own inner pipeline routes only the collector topics, over the singleton MeshCollectorStore.
            .UseBenzeneMessage(new BenzeneMessageHttpOptions { Path = "/benzene/invoke" },
                collector => collector.UseMessageHandlers(MeshCollectorHandlers.All))
            .UseMessageHandlers(typeof(Startup).Assembly));
    }

    /// <summary>
    /// Builds the refresh endpoint's guard config. Only the throttle window is configurable (via
    /// <c>MESH_REFRESH_MIN_INTERVAL_SECONDS</c>); the path and the <c>X-Benzene-Refresh</c> header name
    /// are fixed contracts shared with the mesh UI, so they stay as the guard's own defaults. Mirrors
    /// <c>examples/AzureMesh/Mesh/Startup.cs</c>'s <c>BuildRefreshGuardOptions</c>.
    /// </summary>
    private static MeshRefreshGuardOptions BuildRefreshGuardOptions()
    {
        // MeshRefreshGuardOptions.Topic defaults to MeshAggregatorTopics.Aggregate
        // ("benzene:mesh:aggregate") - AwsMesh's MeshAggregateHandler's topic, but NOT this example's:
        // MeshRefreshHandler here is "mesh:refresh" (matching the GoogleCloudMesh/AzureMesh/
        // AzureFunctionsMesh siblings). The Path match alone already guards the endpoint, but a wrong
        // Topic would leave the guard's second, route-alias-proof check inertly matching a topic
        // nothing here ever uses - so it's corrected explicitly.
        var options = new MeshRefreshGuardOptions { Topic = "mesh:refresh" };

        // Parse leniently but reject nonsense: a negative value would disable the throttle by accident,
        // so only a non-negative parse wins. 0 is honoured as an explicit "throttle off" escape hatch.
        if (double.TryParse(Environment.GetEnvironmentVariable("MESH_REFRESH_MIN_INTERVAL_SECONDS"),
                out var seconds) && seconds >= 0)
        {
            options.MinimumInterval = TimeSpan.FromSeconds(seconds);
        }

        return options;
    }
}
