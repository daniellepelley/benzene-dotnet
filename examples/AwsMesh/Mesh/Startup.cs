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
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Auth.Oidc;
using Benzene.Mesh.Aws.Lambda;
using Benzene.Mesh.Aws.S3;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Dispatch;
using Benzene.Mesh.Discovery.Aws;
using Benzene.Mesh.Fleet.Aws.XRay;
using Benzene.Mesh.Ui;
using Benzene.Mesh.Usage.CloudWatch;
using Benzene.Microsoft.Dependencies;
using Benzene.Examples.AwsMesh.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

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

    /// <summary>
    /// Where a composed message is sent. Its own path, not the read endpoint's — see the pipeline
    /// below for why, and note that the guard, the UI and the edge throttle all key off this one
    /// value.
    /// </summary>
    private const string DispatchPath = "/mesh/dispatch";


    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var bucket = Environment.GetEnvironmentVariable("MESH_ARTIFACT_BUCKET")
                     ?? throw new InvalidOperationException("MESH_ARTIFACT_BUCKET is required.");
        var prefix = Environment.GetEnvironmentVariable("MESH_ARTIFACT_PREFIX") ?? "";
        var extraServices = ParseExtraServices();

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
            // The SEND leg. Dispatch reuses the interrogation path's access — same Invoke permission,
            // different payload — so nothing new is granted by turning it on.
            benzene.AddMeshLambdaDispatcher();

            // THE REGISTRY THE DISPATCH HANDLER TARGETS.
            //
            // Discovery produces the real registry per run and writes it to S3; the singleton
            // registered above is permanently empty, because nothing ever puts the discovered one
            // back into the container. The aggregation path never noticed — it passes its registry as
            // a method argument — but a dispatch resolves its target from DI, so every dispatch would
            // have answered "no service named X is registered" while the UI listed X two clicks away.
            //
            // Scoped, and read per request: the registry changes when discovery runs, and a singleton
            // captured at cold start would go stale without ever saying so.
            benzene.AddScoped<MeshServiceRegistry>(resolver =>
            {
                var json = resolver.GetService<IMeshArtifactStore>().TryReadAsync("registry.json")
                    .GetAwaiter().GetResult();
                return json == null
                    ? new MeshServiceRegistry(Array.Empty<MeshServiceRegistryEntry>())
                    : MeshRegistryJson.Deserialize(json);
            });

            // Carries the signed-in caller from the login gate to the dispatch audit record.
            benzene.AddScoped(_ => new MeshDispatchIdentity());
            benzene.AddScoped<IOidcSessionSink>(resolver =>
                new OidcDispatchIdentitySink(resolver.GetService<MeshDispatchIdentity>()));
            benzene.AddMeshAwsLambdaDiscovery();    // AwsLambdaDiscoveryProvider + MeshDiscoveryRunner
            // Option B (work/mesh-external-service-discovery-scope-2026-08.md): an admin-managed seed
            // of services entirely outside this AWS account/Terraform stack, reached over plain HTTP
            // (HttpMeshServiceSource, already the default IMeshServiceSource - no new source wiring
            // needed). Its own wrapper type, not a third MeshServiceRegistry registration - see
            // MeshExtraServicesSeed's remarks for why that would collide with the two MeshServiceRegistry
            // registrations already in this container.
            benzene.AddSingleton(new MeshExtraServicesSeed(extraServices));
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
                // The same treatment for the one surface that fires a caller's payload into a real
                // handler: a required X-Benzene-Dispatch header (CSRF), an identity it fails closed
                // without, a payload bound, and a per-identity rate limit. The per-target limit and
                // the audit record are applied by the handler, where the parsed target exists.
                // NOTE what is NOT here: MeshDispatchOptions.AllowInProduction. Dispatch is off in
                // production by default and stays off — opening it needs roles on the session and a
                // way to tell a read-shaped topic from a write-shaped one, neither of which exists
                // yet (work/mesh-environments-and-access.md E4/E5). This estate gets dispatch by
                // being a non-production environment, which Terraform declares explicitly.
                .UseMeshDispatchGuard(BuildDispatchGuardOptions())
                .UseMeshDispatch()
                // The Mesh UI: the service catalog (what services declare, from manifest.json) enriched
                // in-page with the live fleet — what's actually running (X-Ray traces + CloudWatch usage) —
                // polled from the /benzene/invoke envelope below. One page: the catalog is the spine and
                // the live data merges into it (health, observed-vs-declared consumers, recent flows, a
                // Fleet landing view), rather than a disconnected second page.
                // logoutUrl/refreshUrl are explicit opt-ins (see UseMeshUi's remarks): they light up the
                // page's Sign-out and Refresh controls, which the vendored bundle feature-detects.
                .UseMeshUi(MeshUiPath, "manifest.json", "/benzene/invoke",
                    // The Test Console's Send button exists only when this is set — a deliberate,
                    // separate opt-in from the read plane above, even though both ride one endpoint.
                    dispatchUrl: DispatchPath,
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
                // DISPATCH GETS ITS OWN DOOR, deliberately, rather than widening the read endpoint's
                // filter to admit it. Three things fall out of that, all of them wanted:
                //   - the read plane's filter stays exactly as narrow as it was, so nothing about
                //     the fleet poll changes and benzene:mesh:aggregate stays as far away as ever;
                //   - the guard above matches one path that does one thing, so a refusal here can
                //     never accidentally refuse a catalogue read;
                //   - API Gateway can throttle the send route separately from the poll route, which
                //     it cannot do while both ride one path — and a rate limit that also throttles
                //     reading the estate is a rate limit nobody will leave switched on.
                .UseBenzeneMessage(new BenzeneMessageHttpOptions
                    {
                        Path = DispatchPath,
                        TopicFilter = topic => string.Equals(
                            topic, Benzene.Mesh.Dispatch.Extensions.DispatchTopic, StringComparison.OrdinalIgnoreCase),
                    },
                    dispatch => dispatch.UseMessageHandlers(typeof(MeshDispatchMessageHandler)))
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
    /// <summary>
    /// The dispatch endpoint's guard. Only the per-minute limits are configurable
    /// (<c>MESH_DISPATCH_MAX_PER_MINUTE</c>, <c>MESH_DISPATCH_MAX_PER_TARGET_PER_MINUTE</c>); the path
    /// and the <c>X-Benzene-Dispatch</c> header are fixed contracts shared with the mesh UI, so they
    /// stay as the guard's own defaults rather than becoming another thing that can drift.
    /// </summary>
    private static MeshDispatchGuardOptions BuildDispatchGuardOptions()
    {
        var options = new MeshDispatchGuardOptions { Path = DispatchPath };

        // A negative value would disable a limit by accident, so only a non-negative parse wins.
        // 0 is honoured as an explicit "no limit" escape hatch, as it is for the refresh throttle.
        if (int.TryParse(Environment.GetEnvironmentVariable("MESH_DISPATCH_MAX_PER_MINUTE"), out var perIdentity)
            && perIdentity >= 0)
        {
            options.MaxPerMinutePerIdentity = perIdentity;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("MESH_DISPATCH_MAX_PER_TARGET_PER_MINUTE"), out var perTarget)
            && perTarget >= 0)
        {
            options.MaxPerMinutePerTarget = perTarget;
        }

        return options;
    }

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
    /// Parses the optional <c>MESH_EXTRA_SERVICES</c> admin seed (Option B,
    /// work/mesh-external-service-discovery-scope-2026-08.md) - a <c>mesh.json</c>-shaped JSON blob
    /// (<c>{"services":[{"name","specUrl","healthUrl"}]}</c>) naming services entirely outside this AWS
    /// account, reused as-is via <see cref="MeshRegistryJson.Deserialize"/> rather than a second parser
    /// for the same shape. Unset or blank (Terraform's default) is NOT an error - it returns <c>null</c>,
    /// which <see cref="MeshDiscoveryRunner.DiscoverAsync"/> treats identically to "no seed passed at
    /// all", so an unconfigured mesh behaves byte-for-byte as it did before this variable existed.
    /// Unlike that lenient case, a variable that IS set but fails to parse throws - a config typo (bad
    /// JSON, a field of the wrong shape) should fail loud at cold start, not silently vanish and leave
    /// an admin wondering why their service never shows up.
    /// </summary>
    private static MeshServiceRegistry? ParseExtraServices()
    {
        var json = Environment.GetEnvironmentVariable("MESH_EXTRA_SERVICES");
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return MeshRegistryJson.Deserialize(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "MESH_EXTRA_SERVICES is set but is not valid mesh.json-shaped JSON " +
                "(expected {\"services\":[{\"name\":...,\"specUrl\":...,\"healthUrl\":...}]}).", ex);
        }
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
