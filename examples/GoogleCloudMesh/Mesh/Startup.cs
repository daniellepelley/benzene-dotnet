using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Diagnostics;
using Benzene.Http;
using Benzene.Http.Cors;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.GoogleCloud.Storage;
using Benzene.Mesh.Ui;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Benzene.Mesh.Aggregator;

namespace Benzene.Examples.GoogleCloudMesh.Mesh;

/// <summary>
/// The mesh aggregator, hosted as a Cloud Functions Gen2 HTTP function. It polls each service's HTTP
/// Cloud Service Profile (a static registry from env), writes the catalog to Google Cloud Storage, and
/// serves the mesh UI + artifacts. Aggregation is driven on demand by <c>POST /mesh/refresh</c> (Cloud
/// Scheduler hits it periodically, since Cloud Functions has no timer trigger).
/// </summary>
public class Startup : BenzeneStartUp
{

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging();

        var bucket = Environment.GetEnvironmentVariable("MESH_BUCKET")
                     ?? throw new InvalidOperationException("MESH_BUCKET is required.");
        var prefix = Environment.GetEnvironmentVariable("MESH_PREFIX") ?? "";

        services.UsingBenzene(x => x
            .AddDiagnostics()
            .AddMessageHandlers(typeof(Startup).Assembly)
            .AddHttpMessageHandlers()
            // Discovery is supplied statically (MeshRegistry.FromEnvironment); catalog persists to GCS.
            .AddMeshAggregatorWithGcs(new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>()), bucket, prefix));

        // No discovery on this host: the registry is read from configuration once per pass.
        services.AddSingleton(provider => new MeshAggregationPass(
            provider.GetRequiredService<IMeshArtifactStore>(),
            provider.GetRequiredService<MeshAggregator>(),
            _ => Task.FromResult(MeshServiceRegistry.FromEnvironment())));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        app.UseHttp(http => http
            .UseBenzeneEnrichment()
            // #41 (WP-E): this endpoint had NO guard at all - an anonymous POST could trigger a full
            // aggregation pass and a GCS write. Same package, same wiring as AzureMesh/AwsMesh: a
            // required X-Benzene-Refresh header (CSRF) plus a manifest-age throttle. See README's
            // "Security posture" for what this does and does not cover.
            .UseMeshRefreshGuard(BuildRefreshGuardOptions())
            .UseMeshUi("/mesh-ui", "manifest.json")
            .UseMeshSpecUi("/mesh-spec-ui.html", "manifest.json")
            .UseMeshArtifacts(new CorsSettings { AllowedDomains = new[] { "https://studio.asyncapi.com" } })
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
        // MeshRefreshHandler here is "mesh:refresh" (matching the K8sMesh/AzureMesh/AzureFunctionsMesh
        // siblings). The Path match alone already guards the endpoint, but a wrong Topic would leave
        // the guard's second, route-alias-proof check inertly matching a topic nothing here ever uses -
        // so it's corrected explicitly.
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
