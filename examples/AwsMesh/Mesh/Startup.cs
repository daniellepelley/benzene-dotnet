using Benzene.Abstractions.Hosting;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.EventBridge;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Diagnostics;
using Benzene.Http;
using Benzene.Http.Cors;
using Benzene.Http.BenzeneMessage;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Auth.Oidc;
using Benzene.Mesh.Aws.Lambda;
using Benzene.Mesh.Aws.S3;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Discovery.Aws;
using Benzene.Mesh.Fleet.Aws.XRay;
using Benzene.Mesh.Ui;
using Benzene.Mesh.Usage.CloudWatch;
using Benzene.Microsoft.Dependencies;
using Benzene.Examples.AwsMesh.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Examples.AwsMesh.Mesh;

/// <summary>
/// The mesh service, hosted as an AWS Lambda. On an EventBridge schedule it discovers the
/// benzene-tagged service Lambdas, writes the discovered registry to S3, interrogates each by
/// Lambda-Invoke, and writes the catalog artifacts to S3. Over HTTP (API Gateway) it serves the Mesh
/// UI, the catalog artifacts (read from S3), and an on-demand refresh.
/// </summary>
public class Startup : BenzeneStartUp
{
    /// <summary>
    /// Where this mesh serves its UI — and therefore what counts as "home" for the auth gate
    /// (<see cref="MeshOidcOptions.HomePath"/>). Shared by both so they cannot drift: nothing is served
    /// at "/", so a post-logout redirect there resolves no route.
    /// </summary>
    private const string MeshUiPath = "/mesh-ui";

    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var bucket = Environment.GetEnvironmentVariable("MESH_ARTIFACT_BUCKET")
                     ?? throw new InvalidOperationException("MESH_ARTIFACT_BUCKET is required.");
        var prefix = Environment.GetEnvironmentVariable("MESH_ARTIFACT_PREFIX") ?? "";

        // The AWS API Gateway (v1 payload) binding for Benzene.Mesh.Auth.Oidc's query-string
        // abstraction - see ApiGatewayOidcQueryStringReader's remarks and that package's CLAUDE.md.
        services.AddSingleton<IOidcQueryStringReader<ApiGatewayContext>, ApiGatewayOidcQueryStringReader>();

        // Full OpenTelemetry for the mesh Lambda too, so its discovery/aggregation + UI pipelines are
        // traced and metered alongside the services. Built eagerly (not via services.AddOpenTelemetry())
        // so the providers actually exist under a bare Lambda host, and force-flushed per invocation by
        // TracingLambdaHost — see LambdaTelemetry for why the usual hosting integration records nothing here.
        LambdaTelemetry.Configure(services, "benzene-mesh");

        services.UsingBenzene(benzene =>
        {
            benzene.AddDiagnostics();
            // The mesh Lambda's own spans need benzene.service too (the domain services get it via
            // UseBenzeneCloudService, which this host doesn't use): without it the trace mappers fall
            // back to backend segment names, and the fleet shows the mesh's own flows as
            // "EventBridgeLambdaHandler" (2026-07-25 live-fire finding).
            benzene.SetApplicationInfo("benzene-mesh", string.Empty, string.Empty);
            // OTel path only (→ ADOT collector → X-Ray), matching the domain services: it's the path that
            // stitches a cross-service transaction into one X-Ray trace (via the propagated W3C traceparent),
            // so running the X-Ray SDK path (AddXRayTracing) too would add a second, non-stitching
            // representation. See MeshServiceWiring for the full rationale.
            benzene.AddMessageHandlers(typeof(Startup).Assembly);
            // Discovery starts with an empty registry — discovery replaces it at runtime; artifacts live in S3.
            benzene.AddMeshAggregatorWithS3(new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>()), bucket, prefix);
            benzene.AddMeshLambdaSource();          // LambdaMeshServiceSource: interrogate a service by Invoke
            benzene.AddMeshAwsLambdaDiscovery();    // AwsLambdaDiscoveryProvider + MeshDiscoveryRunner
            // Usage feed: read the benzene.messages.processed counter (exported to CloudWatch by the ADOT
            // collector's EMF exporter — see collector.yaml) back as per-topic request counts over a
            // window, merged into usage.json each run. The window is tweakable via MESH_USAGE_WINDOW_HOURS.
            var usageWindowHours = double.TryParse(
                Environment.GetEnvironmentVariable("MESH_USAGE_WINDOW_HOURS"), out var hours) ? hours : 24;
            benzene.AddCloudWatchUsage(new CloudWatchUsageOptions(timeWindow: TimeSpan.FromHours(usageWindowHours)));

            // The Fleet view, now on the AWS plane: an IMeshFleetReadModel composed from X-Ray (trace +
            // correlation + recent flows + the anonymous-but-live service list) and the CloudWatch usage
            // feed above (per-topic stats). No push collector - the services already export traces to
            // X-Ray and the metric to CloudWatch, so the mesh reads its fleet back from those. The read
            // side is served by MeshCollectorHandlers.Queries over the wire envelope wired in Configure.
            benzene.AddHttpMessageHandlers();
            benzene.AddXRayFleetReadModel();
        });
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        // Scope handler discovery to THIS assembly. The parameterless UseMessageHandlers() scans every
        // loaded assembly, which would also discover Benzene.Mesh.Aggregator's own
        // MeshAggregateMessageHandler — a second handler for topic "benzene:mesh:aggregate" (collision). This
        // example deliberately uses its own MeshAggregateHandler (discovery + aggregate) instead.
        var handlers = typeof(Startup).Assembly;
        var oidcOptions = BuildOidcOptions();
        var refreshGuardOptions = BuildRefreshGuardOptions();

        app.UseAwsLambda(aws =>
        {
            // Scheduled aggregation: an EventBridge rule fires with detail-type "benzene:mesh:aggregate".
            // No auth gate here - this is Lambda-to-Lambda (EventBridge invoking this function directly),
            // never a browser, and out of scope for a login-gated HTTP surface.
            aws.UseEventBridge(eb => eb
                .UseW3CTraceContext()
                .UseBenzeneEnrichment()
                .UseBenzeneMetrics()
                .UseMessageHandlers(handlers));

            // Public HTTP surface: the Mesh UI, the catalog artifacts (from S3), and POST /mesh/refresh.
            // Everything below UseMeshOidcAuth is login-gated (see Benzene.Mesh.Auth.Oidc/CLAUDE.md) -
            // it owns /mesh/auth/login, /mesh/auth/callback, /mesh/auth/logout itself and requires a
            // valid session for every other route on this pipeline.
            aws.UseApiGateway(http => http
                .UseW3CTraceContext()
                .UseBenzeneEnrichment()
                .UseBenzeneMetrics()
                .UseMeshOidcAuth(oidcOptions)
                // Everything a logged-in session can do to POST /mesh/refresh is bounded here, directly
                // behind the login gate and ahead of every handler: a required X-Benzene-Refresh header
                // (CSRF — a cross-site form can't set one, a cross-origin fetch that does gets preflighted
                // and refused), then a manifest-age throttle. Deliberately NOT on the EventBridge
                // sub-pipeline above: the schedule is not a browser and has no header to send.
                .UseMeshRefreshGuard(refreshGuardOptions)
                // The Mesh UI: the service catalog (what services declare, from manifest.json) enriched
                // in-page with the live fleet — what's actually running (X-Ray traces + CloudWatch usage) —
                // polled from the /benzene/invoke envelope below. One page: the catalog is the spine and
                // the live data merges into it (health, observed-vs-declared consumers, recent flows, a
                // Fleet landing view), rather than a disconnected second page.
                // logoutUrl/refreshUrl are explicit opt-ins (see UseMeshUi's remarks): they light up the
                // page's Sign-out and Refresh controls, which the vendored bundle feature-detects.
                .UseMeshUi(MeshUiPath, "manifest.json", "/benzene/invoke",
                    dispatchUrl: null,
                    logoutUrl: oidcOptions.BasePath + "/logout",
                    refreshUrl: refreshGuardOptions.Path)
                // The mesh-hosted per-service Spec UI (mesh-ui's "benzene:spec" link). Renders each service's
                // spec from the same-origin services/{name}.json snapshot, so a service only serves JSON.
                .UseMeshSpecUi("/mesh-spec-ui.html", "manifest.json")
                // Allow the AsyncAPI Studio deep-link to fetch asyncapi.json cross-origin. Uses
                // Benzene's own CORS support (Benzene.Http.Cors.CorsSettings); "*" would open it to
                // any origin, but scoping to Studio's origin keeps the example tight.
                .UseMeshArtifacts(new CorsSettings { AllowedDomains = new[] { "https://studio.asyncapi.com" } })
                // The Mesh UI's live fleet endpoint: an inner benzene-message pipeline routing only the
                // collector's read queries (mesh:query:*) over the composite X-Ray+CloudWatch read model.
                // Queries only - there's no push ingestion here (X-Ray/CloudWatch are the feeds).
                //
                // The TopicFilter is load-bearing, not decoration. `UseMessageHandlers(types)` scopes
                // which handler types get *registered*, NOT which topics this endpoint can *reach*:
                // handler discovery is a single process-wide union of every AddMessageHandlers call (see
                // Benzene.Core.MessageHandlers.DI.Extensions.RegisterHandlerFinderInfrastructure), so
                // without this filter a POSTed envelope naming ANY topic in the app - including
                // benzene:mesh:aggregate - would dispatch here, reaching the aggregation pass down a
                // path that never goes past UseMeshRefreshGuard's header + throttle checks. Restricting
                // the endpoint to the read queries it exists to serve closes that off at the door.
                .UseBenzeneMessage(new BenzeneMessageHttpOptions
                    {
                        Path = "/benzene/invoke",
                        TopicFilter = topic => topic.StartsWith("benzene:mesh:query:", StringComparison.OrdinalIgnoreCase),
                    },
                    fleet => fleet.UseMessageHandlers(MeshCollectorHandlers.Queries))
                .UseMessageHandlers(handlers));
        });
    }

    /// <summary>
    /// Builds the refresh endpoint's guard config. Only the throttle window is configurable (via
    /// <c>MESH_REFRESH_MIN_INTERVAL_SECONDS</c>, wired by Terraform from <c>var.refresh_min_interval_seconds</c>);
    /// the path and the <c>X-Benzene-Refresh</c> header name are fixed contracts shared with the mesh UI,
    /// so they stay as the guard's own defaults rather than becoming another thing that can drift.
    /// Unlike the OIDC values this one does NOT throw when unset - a missing throttle window is not a
    /// security hole (the guard's own 30s default applies), whereas a missing client secret is.
    /// </summary>
    private static MeshRefreshGuardOptions BuildRefreshGuardOptions()
    {
        var options = new MeshRefreshGuardOptions();

        // Parse leniently but reject nonsense: a negative value would disable the throttle by accident,
        // so only a non-negative parse wins. 0 is honoured as an explicit "throttle off" escape hatch.
        if (double.TryParse(Environment.GetEnvironmentVariable("MESH_REFRESH_MIN_INTERVAL_SECONDS"),
                out var seconds) && seconds >= 0)
        {
            options.MinimumInterval = TimeSpan.FromSeconds(seconds);
        }

        return options;
    }

    /// <summary>
    /// Builds the mesh's OIDC login config from environment variables (wired by Terraform - see
    /// deploy/main.tf and deploy/variables.tf), never a hardcoded/example default. Every required value
    /// throws a clear, actionable exception if missing, matching this repo's "fail fast, no silent
    /// degraded defaults" ethos (see <c>MESH_ARTIFACT_BUCKET</c> above). <c>Benzene.Mesh.Auth.Oidc</c>
    /// itself is provider-agnostic (see its <c>CLAUDE.md</c>) - Google is this example's own choice,
    /// expressed only here as <see cref="MeshOidcOptions.Issuer"/>.
    /// </summary>
    private static MeshOidcOptions BuildOidcOptions()
    {
        var clientId = Environment.GetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_ID")
                       ?? throw new InvalidOperationException("GOOGLE_OAUTH_CLIENT_ID is required.");
        var clientSecret = Environment.GetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_SECRET")
                            ?? throw new InvalidOperationException("GOOGLE_OAUTH_CLIENT_SECRET is required.");
        var signingKey = Environment.GetEnvironmentVariable("MESH_OIDC_SIGNING_KEY")
                          ?? throw new InvalidOperationException("MESH_OIDC_SIGNING_KEY is required.");
        var allowedEmails = (Environment.GetEnvironmentVariable("MESH_ALLOWED_EMAILS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new MeshOidcOptions
        {
            Issuer = "https://accounts.google.com",
            ClientId = clientId,
            ClientSecret = clientSecret,
            SigningKey = signingKey,
            AllowedEmails = allowedEmails,
            BasePath = "/mesh/auth",
            // This mesh serves nothing at "/" — its landing page is the Mesh UI. Left at the default,
            // signing out redirects to a route that doesn't exist: the gate bounces "/" back through
            // login, Google silently re-authenticates the still-signed-in user, and the callback
            // returns them to "/" again, rendering a bare not-found problem document with no sign
            // they were ever signed out (they were).
            HomePath = MeshUiPath,
        };
    }
}

/// <summary>AWS Lambda entry point hosting <see cref="Startup"/>, force-flushing OpenTelemetry per invocation.</summary>
public class Function : TracingLambdaHost<Startup>;
