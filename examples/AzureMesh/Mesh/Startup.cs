using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Diagnostics;
using Benzene.Http;
using Benzene.Http.Cors;
using Azure.Monitor.OpenTelemetry.Exporter;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Azure.Blob;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Discovery.Azure;
using Benzene.Mesh.Ui;
using Benzene.Mesh.Usage.ApplicationInsights;
using Benzene.Microsoft.Dependencies;
using Benzene.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Benzene.Mesh.Aggregator;

namespace Benzene.Examples.AzureMesh.Mesh;

/// <summary>
/// The mesh service, hosted as an ASP.NET Core container on Azure (App Service / Container App). It
/// discovers the benzene-tagged Azure App Services (via Azure Resource Manager, using the app's
/// managed identity), writes the discovered registry + catalog to Blob Storage, and interrogates each
/// service over HTTPS — then serves the Mesh UI and catalog artifacts. A background service re-runs the
/// pass on an interval; <c>POST /mesh/refresh</c> triggers one on demand.
/// </summary>
public class Startup : BenzeneStartUp
{

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var blobServiceUri = Environment.GetEnvironmentVariable("MESH_BLOB_URI")
                             ?? throw new InvalidOperationException("MESH_BLOB_URI is required (e.g. https://acct.blob.core.windows.net).");
        var container = Environment.GetEnvironmentVariable("MESH_BLOB_CONTAINER") ?? "benzene:mesh";
        var prefix = Environment.GetEnvironmentVariable("MESH_BLOB_PREFIX") ?? "";

        // The OTLP exporter is only attached when OTEL_EXPORTER_OTLP_ENDPOINT is set — the
        // instrumentation is armed either way, so there are no connection-refused errors without one.
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        // With an Application Insights connection string, also export metrics to Azure Monitor (delta
        // temporality by default) — this is what the CloudWatch usage feed's Azure sibling reads back.
        var appInsights = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
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
                if (!string.IsNullOrEmpty(appInsights)) metrics.AddAzureMonitorMetricExporter();
            });

        services.UsingBenzene(benzene =>
        {
            benzene
                .AddDiagnostics()
                .AddMessageHandlers(typeof(Startup).Assembly)
                .AddHttpMessageHandlers()
                // Discovery starts with an empty registry — discovery replaces it at runtime; artifacts live in Blob Storage.
                .AddMeshAggregatorWithBlob(new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>()),
                    new Uri(blobServiceUri), container, prefix)
                // Scope discovery to this deployment's subscription + resource group so a subscription-scoped
                // Reader identity doesn't discover every benzene-tagged site in the subscription. Both are
                // optional — unset falls back to the credential's default subscription, whole-subscription sweep.
                .AddMeshAzureDiscovery(
                    subscriptionId: Environment.GetEnvironmentVariable("MESH_SUBSCRIPTION_ID"),
                    resourceGroup: Environment.GetEnvironmentVariable("MESH_RESOURCE_GROUP"));

            // Usage feed: read the benzene.messages.processed counter (exported to Application Insights by
            // each service's Azure Monitor exporter) back from the Log Analytics workspace as per-topic
            // request counts over a window, merged into usage.json each run. Only wired when the workspace
            // id is configured (so the example still runs without App Insights). Window: MESH_USAGE_WINDOW_HOURS.
            var workspaceId = Environment.GetEnvironmentVariable("MESH_LOG_ANALYTICS_WORKSPACE_ID");
            if (!string.IsNullOrEmpty(workspaceId))
            {
                var usageWindowHours = double.TryParse(
                    Environment.GetEnvironmentVariable("MESH_USAGE_WINDOW_HOURS"), out var hours) ? hours : 24;
                benzene.AddApplicationInsightsUsage(
                    new ApplicationInsightsUsageOptions(workspaceId, TimeSpan.FromHours(usageWindowHours)));
            }
        });

        // The one thing that differs between mesh hosts: where the registry for a pass comes from.
        // Everything else about a pass - publish the driving registry, interrogate, write the
        // catalog, and do it one at a time - is MeshAggregationPass's.
        services.AddSingleton(provider => new MeshAggregationPass(
            provider.GetRequiredService<IMeshArtifactStore>(),
            provider.GetRequiredService<MeshAggregator>(),
            cancellationToken =>
            {
                var region = Environment.GetEnvironmentVariable("MESH_REGION");
                var filter = new MeshDiscoveryFilter(
                    regions: string.IsNullOrWhiteSpace(region) ? null : new[] { region });
                return provider.GetRequiredService<MeshDiscoveryRunner>()
                    .DiscoverAsync(filter, cancellationToken: cancellationToken);
            }));
        services.AddHostedService<MeshAggregationBackgroundService>();
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        // Scope handler discovery to this assembly so Benzene.Mesh.Aggregator's own
        // MeshAggregateMessageHandler (also [Message("benzene:mesh:aggregate")]) isn't discovered too.
        app.UseHttp(asp => asp
            .UseW3CTraceContext()
            .UseBenzeneEnrichment()
            .UseBenzeneMetrics()
            // This example has no login gate (see README's Security posture) - unlike AwsMesh,
            // UseMeshRefreshGuard is the ONLY thing standing in front of POST /mesh/refresh: a required
            // X-Benzene-Refresh header (CSRF - a cross-site form can't set one) plus a manifest-age
            // throttle. Zero new infra (Benzene.Mesh.Artifacts is already referenced for UseMeshArtifacts
            // below, and the throttle reads the manifest.json the aggregator already writes) - same
            // package, same wiring as AwsMesh/Mesh/Startup.cs.
            .UseMeshRefreshGuard(BuildRefreshGuardOptions())
            .UseMeshUi("/mesh-ui", "manifest.json")
            // The mesh-hosted per-service Spec UI (mesh-ui's "benzene:spec" link). Renders each service's spec
            // from the same-origin services/{name}.json snapshot, so a service only serves JSON.
            .UseMeshSpecUi("/mesh-spec-ui.html", "manifest.json")
            // Allow the AsyncAPI Studio deep-link to fetch asyncapi.json cross-origin. Uses
            // Benzene's own CORS support (Benzene.Http.Cors.CorsSettings); "*" would open it to
            // any origin, but scoping to Studio's origin keeps the example tight.
            .UseMeshArtifacts(new CorsSettings { AllowedDomains = new[] { "https://studio.asyncapi.com" } })
            .UseMessageHandlers(typeof(Startup).Assembly));
    }

    /// <summary>
    /// Builds the refresh endpoint's guard config. Only the throttle window is configurable (via
    /// <c>MESH_REFRESH_MIN_INTERVAL_SECONDS</c>); the path and the <c>X-Benzene-Refresh</c> header name
    /// are fixed contracts shared with the mesh UI, so they stay as the guard's own defaults. Mirrors
    /// <c>examples/AwsMesh/Mesh/Startup.cs</c>'s <c>BuildRefreshGuardOptions</c>. Unlike the OIDC values
    /// AwsMesh also configures, this one does NOT throw when unset - a missing throttle window is not a
    /// security hole (the guard's own 30s default applies).
    /// </summary>
    private static MeshRefreshGuardOptions BuildRefreshGuardOptions()
    {
        // MeshRefreshGuardOptions.Topic defaults to MeshAggregatorTopics.Aggregate
        // ("benzene:mesh:aggregate") - AwsMesh's MeshAggregateHandler's topic, but NOT this example's:
        // MeshRefreshHandler here (like its K8sMesh/GoogleCloudMesh/AzureFunctionsMesh siblings) is
        // "mesh:refresh". The Path match alone already guards the endpoint, but a wrong Topic would
        // leave the guard's second, route-alias-proof check inertly matching a topic nothing here ever
        // uses - so it's corrected explicitly rather than left at a default that doesn't apply.
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
