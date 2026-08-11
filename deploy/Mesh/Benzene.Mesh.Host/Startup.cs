using Benzene.AspNet.Core;
using Benzene.Auth.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Http.BenzeneMessage;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Aws.Lambda;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Dispatch;
using Benzene.Mesh.Ui;
using Benzene.Microsoft.Dependencies;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;

namespace Benzene.Mesh.Host;

/// <summary>
/// Config-driven, dockerized Benzene Mesh Aggregator + UI - mirrors
/// <c>examples/Mesh/Benzene.Examples.Mesh.Aggregator/Startup.cs</c>'s wiring shape, but reads its
/// service registry (and, since config schema v1, its artifact store/usage/fleet/topology/dispatch
/// choices - see <see cref="MeshSourceRegistrar"/>) from <see cref="MeshHostConfig"/> (bound from
/// <c>mesh.json</c>/environment variables) instead of hardcoded wiring, and adds a background poll
/// loop since a bare Docker Compose deployment has no external scheduler.
/// </summary>
public class Startup
{
    private readonly MeshHostConfig _config;
    private readonly MeshServiceRegistry _registry;
    private bool _fleetEnabled;

    /// <summary>Initializes a new instance of the <see cref="Startup"/> class.</summary>
    /// <param name="configuration">The bound configuration (see <c>Program.cs</c> for how <c>mesh.json</c> is loaded).</param>
    public Startup(IConfiguration configuration)
    {
        _config = configuration.Get<MeshHostConfig>() ?? new MeshHostConfig();
        _registry = new MeshServiceRegistry(_config.Services.Select(s => s.ToEntry()).ToArray());
    }

    /// <summary>Registers services.</summary>
    /// <param name="services">The service collection to register with.</param>
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();

        if (IsFileArtifactStore)
        {
            Directory.CreateDirectory(_config.ArtifactRootDirectory);
        }

        services.AddSingleton(_config);
        services.AddHostedService<MeshPollBackgroundService>();

        // Registered unconditionally (a scoped holder no one reads costs one small per-request
        // allocation): MeshAuthGate sets AuthenticationHolder.Principal on every successful
        // authentication so a downstream Benzene-pipeline check can read the same caller - see
        // MeshAuthGate's remarks for why this is safe to resolve straight off HttpContext.RequestServices.
        services.TryAddScoped<AuthenticationHolder>();

        if (string.Equals(_config.Auth.Mode, "oidc", StringComparison.OrdinalIgnoreCase))
        {
            // Task 2.4 (work/enterprise/slice-2-auth.md): standard cookie + OIDC authorization-code
            // wiring, driven entirely by auth.oidc - one configurable implementation covers Google,
            // Okta, Entra ID, Auth0, Keycloak and the customer's own SSO, since social login and a
            // customer's SSO are the same feature once the authority is configuration. MeshAuthGate.
            // Validate (run before this in Configure()) has already confirmed authority/clientId/the
            // client secret env var are set, so no null-check is needed here.
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie()
                .AddOpenIdConnect(options =>
                {
                    options.Authority = _config.Auth.Oidc.Authority;
                    options.ClientId = _config.Auth.Oidc.ClientId;
                    options.ClientSecret = Environment.GetEnvironmentVariable(_config.Auth.Oidc.ClientSecretEnvVar);
                    options.CallbackPath = _config.Auth.Oidc.CallbackPath;
                    options.ResponseType = "code";
                    options.SaveTokens = true;
                    options.Scope.Clear();
                    var scopes = _config.Auth.Oidc.Scopes.Length > 0 ? _config.Auth.Oidc.Scopes : MeshAuthOidcConfig.DefaultScopes;
                    foreach (var scope in scopes)
                    {
                        options.Scope.Add(scope);
                    }
                });
        }

        services.UsingBenzene(x =>
        {
            // The artifact store (local disk by default) and the service registry it's polled
            // against - every other section below assumes this one has already run.
            MeshSourceRegistrar.RegisterArtifactStore(x, _registry, _config);
            // Optional per entry - only actually used if a service's Source is AwsLambdaInvoke.
            // Registering it unconditionally is harmless: constructing an AmazonLambdaClient
            // doesn't require valid AWS credentials up front, only an actual Invoke call would.
            x.AddMeshLambdaSource();

            MeshSourceRegistrar.RegisterUsageSources(x, _config.Usage);
            // Zero or one fleet source (see MeshFleetConfig) - remembered so Configure() knows
            // whether to also wire the read handlers and point the mesh UI at the envelope.
            _fleetEnabled = MeshSourceRegistrar.RegisterFleet(x, _config.Fleet);
            MeshSourceRegistrar.RegisterTopology(x, _config.Topology);

            // Live dispatch is OFF unless explicitly opted in (it invokes services' real handlers).
            // When enabled, the registry (the set of dispatchable services) and the AWS-Lambda
            // dispatcher are registered; the mesh:dispatch handler itself is wired in Configure().
            if (_config.Dispatch.Enabled)
            {
                x.AddSingleton(_registry);
                x.AddMeshLambdaDispatcher();
            }
        });
    }

    /// <summary>Configures the request pipeline.</summary>
    /// <param name="app">The application builder.</param>
    /// <param name="env">The hosting environment.</param>
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();

        // Fail fast on a config that would silently under-protect the host - see
        // MeshAuthGate.Validate's remarks. Run before UseAuthentication/the gate itself so a bad
        // config never gets as far as accepting a single request.
        MeshAuthGate.Validate(_config.Auth);

        if (string.Equals(_config.Auth.Mode, "oidc", StringComparison.OrdinalIgnoreCase))
        {
            // Populates HttpContext.User from the auth cookie (if present) before MeshAuthGate reads
            // it - required for ChallengeAsync/the cookie scheme MeshAuthGate's oidc branch relies on.
            app.UseAuthentication();
        }

        // The ONE gate for both artifact-serving branches below (task 2.2) - registered immediately
        // after UseRouting() and before UseStaticFiles(...), so it covers the file-artifact-store
        // branch (ASP.NET static files, entirely outside the Benzene pipeline) as well as everything
        // in the Benzene pipeline (including the non-file branch's UseMeshArtifacts()). Placing this
        // only inside UseHttp below would leave /artifacts world-readable whenever
        // artifactStore.type is "file" (the default) - see work/enterprise/slice-2-auth.md's "trap".
        app.UseMiddleware<MeshAuthGate>(_config.Auth);

        string manifestUrl;
        if (IsFileArtifactStore)
        {
            // Serves the aggregator's own generated manifest.json/services/*.json/topology.json from
            // local disk - the real, continuously-refreshed data behind the dashboard below. ASP.NET
            // static files, not the Benzene pipeline - protected by MeshAuthGate above, not by
            // anything in this branch itself.
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(Path.GetFullPath(_config.ArtifactRootDirectory)),
                RequestPath = "/artifacts",
            });
            manifestUrl = "/artifacts/manifest.json";
        }
        else
        {
            // Root-relative: Benzene.Mesh.Artifacts.UseMeshArtifacts() (below) serves the allow-listed
            // artifact keys straight off the configured request path, not under an "/artifacts" prefix -
            // matching every AwsMesh/AzureMesh/GoogleCloudMesh example's own convention.
            manifestUrl = "manifest.json";
        }

        app.UseBenzene(benzene => benzene
            .UseHttp(asp =>
            {
                if (!IsFileArtifactStore)
                {
                    // Reads manifest.json/services/*.json/topology.json/... back from whichever
                    // non-filesystem IMeshArtifactStore RegisterArtifactStore registered - the
                    // PhysicalFileProvider mount above only ever covers the local-disk case.
                    asp.UseMeshArtifacts();
                }

                asp.UseMeshUi(path: "/mesh-ui", manifestUrl: manifestUrl, envelopeUrl: _fleetEnabled ? "/benzene/invoke" : null);
                // The mesh-hosted per-service Spec UI (mesh-ui's "spec" link resolves to
                // /mesh-spec-ui.html when the dashboard is served from this pipeline). Without it,
                // every service card's "spec" drill-in 404s.
                asp.UseMeshSpecUi(path: "/mesh-spec-ui.html", manifestUrl: manifestUrl);

                if (_fleetEnabled)
                {
                    // The mesh UI's live Fleet plane: an inner benzene-message pipeline routing only
                    // the collector's read queries (mesh:query:*) over the composite read model
                    // RegisterFleet registered. Queries only - there is no push ingestion on this
                    // plane, only queries against the configured fleet source.
                    asp.UseBenzeneMessage(new BenzeneMessageHttpOptions { Path = "/benzene/invoke" },
                        fleet => fleet.UseMessageHandlers(MeshCollectorHandlers.Queries));
                }

                // Opt-in live dispatch (mesh:dispatch). Off by default; even when on it self-refuses in
                // Production unless Dispatch.AllowInProduction is also set - a real handler runs.
                //
                // STOPPED HERE (work/enterprise/slice-2-auth.md task 2.5's dispatchRole gate): as wired,
                // UseMeshDispatch only registers the handler DEFINITION - it adds no [HttpEndpoint] route
                // and isn't placed on any UseBenzeneMessage envelope (unlike the fleet-query plane above,
                // which gets its own /benzene/invoke endpoint). AspNetMessageTopicGetter resolves a
                // request's topic purely by matching [HttpEndpoint]-attributed routes
                // (ReflectionHttpEndpointFinder scans only that attribute), so mesh:dispatch has no HTTP
                // path that reaches it in this host today - a pre-existing gap, not introduced here (see
                // Benzene.Mesh.Dispatch/CLAUDE.md's "Follow-ups": the mesh UI send leg is still unbuilt).
                // AuthorizationExtensions.RequireRole<TContext> is transport-pipeline-scoped
                // (IMiddlewarePipelineBuilder<TContext>), not per-handler, so adding it here would gate
                // every request that reaches this shared outer pipeline - including /mesh/report and
                // mesh-ui/spec-ui - not just mesh:dispatch, which contradicts 2.5's own "200 on read"
                // requirement. Making dispatch reachable (its own UseBenzeneMessage envelope, mirroring
                // the fleet-query pattern) is a design decision the brief doesn't make and is arguably
                // dispatch-reachability work, not auth work - so DispatchRole is bound/validated in
                // config (MeshAuthConfig.DispatchRole) but not enforced by new pipeline wiring here.
                if (_config.Dispatch.Enabled)
                {
                    asp.UseMeshDispatch(new MeshDispatchOptions { AllowInProduction = _config.Dispatch.AllowInProduction });
                }

                asp.UseMessageHandlers();
            })
        );

        app.UseEndpoints(endpoints => { });
    }

    private bool IsFileArtifactStore => _config.ArtifactStore.Type.Equals("file", StringComparison.OrdinalIgnoreCase);
}
