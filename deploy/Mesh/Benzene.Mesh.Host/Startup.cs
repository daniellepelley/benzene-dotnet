using System.Security.Claims;
using Amazon.S3;
using Azure.Identity;
using Azure.Storage.Blobs;
using Benzene.AspNet.Core;
using Benzene.Auth.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Http.BenzeneMessage;
using Benzene.Mesh.Aggregator;
using Benzene.Mesh.Artifacts;
using Benzene.Mesh.Aws.Lambda;
using Benzene.Mesh.Aws.S3;
using Benzene.Mesh.Azure.Blob;
using Benzene.Mesh.Collector;
using Benzene.Mesh.Contracts;
using Benzene.Mesh.Dispatch;
using Benzene.Mesh.GoogleCloud.Storage;
using Benzene.Mesh.Ui;
using Benzene.Microsoft.Dependencies;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        _registry = new MeshServiceRegistry(BuildRegistryEntries(_config));
    }

    /// <summary>
    /// Builds the registry the aggregator polls: <see cref="MeshHostConfig.RegistryDocuments"/> (if
    /// any - the discovery job's output, read back through the configured artifact store) unioned
    /// with <see cref="MeshHostConfig.Services"/>, with <c>services</c> always winning a name clash
    /// (<c>work/enterprise/slice-3-discovery.md</c>'s "discovery proposes, config disposes"). Read
    /// once, synchronously, here at startup - the registry is a fixed singleton for the lifetime of
    /// the host (see <see cref="MeshPollBackgroundService"/>), not re-read on every poll.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="MeshHostConfig.RegistryDocuments"/> is non-empty but none of the documents could be
    /// read and parsed - an operator who configured discovery and got an empty dashboard needs to be
    /// told why, rather than silently starting with only the hand-written <c>services</c> list. A
    /// document that is merely missing or unparseable does not throw by itself - it is logged and
    /// skipped, so the estate keeps working on whatever could be read.
    /// </exception>
    private static MeshServiceRegistryEntry[] BuildRegistryEntries(MeshHostConfig config)
    {
        var byName = new Dictionary<string, MeshServiceRegistryEntry>(StringComparer.OrdinalIgnoreCase);

        if (config.RegistryDocuments.Length > 0)
        {
            var store = BuildArtifactStoreForReading(config);
            var readCount = 0;

            foreach (var path in config.RegistryDocuments)
            {
                MeshServiceRegistry discovered;
                try
                {
                    // Startup's own constructor is synchronous (IConfiguration in, wired object out) -
                    // the registry has to be resolved before ConfigureServices/Configure run, so this
                    // one-time read blocks rather than threading async through the whole class.
                    var json = store.TryReadAsync(path).GetAwaiter().GetResult();
                    if (json == null)
                    {
                        Console.Error.WriteLine($"mesh: registry document '{path}' was not found - continuing with what could be read.");
                        continue;
                    }

                    discovered = MeshRegistryJson.Deserialize(json);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"mesh: failed to read/parse registry document '{path}': {ex.Message} - continuing with what could be read.");
                    continue;
                }

                readCount++;
                foreach (var entry in discovered.Services)
                {
                    // First document listed wins a clash between documents - mirrors
                    // MeshDiscoveryRunner's own earlier-provider-wins precedence, so the two behave
                    // the same way.
                    if (!byName.ContainsKey(entry.Name))
                    {
                        byName[entry.Name] = entry;
                    }
                }
            }

            if (readCount == 0)
            {
                throw new InvalidOperationException(
                    $"registryDocuments configured ({config.RegistryDocuments.Length}) but none could be read. " +
                    "Check the artifact store configuration and that the discovery job has run.");
            }
        }

        // services always wins a name clash - the "config disposes" half of the position: an operator
        // can always override a discovered entry by naming it explicitly.
        foreach (var service in config.Services)
        {
            byName[service.Name] = service.ToEntry();
        }

        return byName.Values.ToArray();
    }

    /// <summary>
    /// Constructs a throwaway <see cref="IMeshArtifactStore"/> for reading
    /// <see cref="MeshHostConfig.RegistryDocuments"/> back, matching <see cref="MeshSourceRegistrar.RegisterArtifactStore"/>'s
    /// backend selection. Kept separate from that DI-time registration: this one runs synchronously,
    /// inside the constructor, before the service container exists.
    /// </summary>
    private static IMeshArtifactStore BuildArtifactStoreForReading(MeshHostConfig config)
    {
        var store = config.ArtifactStore;
        switch (store.Type.ToLowerInvariant())
        {
            case "file":
                return new FileSystemMeshArtifactStore(config.ArtifactRootDirectory);
            case "s3":
                {
                    var bucket = RequireOption(store.Options, "bucket", "s3");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    return new S3MeshArtifactStore(new AmazonS3Client(), bucket, prefix);
                }
            case "azureblob":
                {
                    var blobServiceUri = RequireOption(store.Options, "blobServiceUri", "azureBlob");
                    var container = RequireOption(store.Options, "container", "azureBlob");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    var containerClient = new BlobServiceClient(new Uri(blobServiceUri), new DefaultAzureCredential())
                        .GetBlobContainerClient(container);
                    return new BlobMeshArtifactStore(containerClient, prefix);
                }
            case "gcs":
                {
                    var bucket = RequireOption(store.Options, "bucket", "gcs");
                    var prefix = GetOption(store.Options, "prefix") ?? string.Empty;
                    return new GcsMeshArtifactStore(StorageClient.Create(), bucket, prefix);
                }
            default:
                throw new InvalidOperationException(
                    $"Unknown artifact store type '{store.Type}'. Valid values: {string.Join(", ", MeshSourceRegistrar.ValidArtifactStoreTypes)}.");
        }
    }

    private static string RequireOption(Dictionary<string, string>? options, string key, string storeType)
    {
        if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"artifact store '{storeType}' requires option '{key}'.");
        }
        return value;
    }

    private static string? GetOption(Dictionary<string, string>? options, string key)
        => options != null && options.TryGetValue(key, out var value) ? value : null;

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
        // authentication. See MeshAuthGate's remarks: this does NOT reach the Benzene pipeline below
        // in this host (a separate, cloned IServiceProvider) - context.User, via IHttpContextAccessor,
        // is what does.
        services.TryAddScoped<AuthenticationHolder>();

        // The bridge MeshDispatchIdentity's registration (below, and see MeshAuthGate's remarks) reads
        // to cross from MeshAuthGate's HttpContext into the Benzene pipeline's own, separately-built
        // DI container - IHttpContextAccessor's backing store is a process-wide AsyncLocal, so it is
        // visible from any container that resolves it, unlike a scoped instance set on
        // HttpContext.RequestServices directly.
        services.AddHttpContextAccessor();

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
                    // WP-1(b) (#20): defaults true; MeshAuthGate.Validate (run before this ever matters,
                    // in Configure()) has already rejected a non-https authority unless this was
                    // explicitly set false, so by the time this option is read the combination is known
                    // safe.
                    options.RequireHttpsMetadata = _config.Auth.Oidc.RequireHttpsMetadata;
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

                // Populates MeshDispatchIdentity.Email from the SAME caller MeshAuthGate authenticated
                // (via context.User - see its remarks on why that, not AuthenticationHolder, is the one
                // thing that crosses into this pipeline's own separately-built container), so
                // MeshDispatchGuardMiddleware's fail-closed identity check (mounted in Configure()
                // below) and the handler's own audit record both see who is actually dispatching,
                // instead of treating every request as unattributed. Registered here (during
                // ConfigureServices, so it lands in the real root IServiceCollection AND gets copied
                // into the Benzene pipeline's clone - see MeshAuthGate's remarks) with plain AddScoped
                // rather than the TryAddScoped UseMeshDispatchGuard uses for its own fallback
                // registration below, so THIS one wins.
                x.AddScoped(resolver =>
                {
                    var user = resolver.GetService<IHttpContextAccessor>().HttpContext?.User;
                    return new MeshDispatchIdentity
                    {
                        Email = user?.FindFirst(ClaimTypes.Email)?.Value
                            ?? user?.FindFirst(ClaimTypes.Name)?.Value
                            ?? user?.Identity?.Name,
                    };
                });
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
                // work/enterprise/slice-2-auth.md task 2.5, fixed 2026-08-22 (see that file's dated
                // correction note): UseMeshDispatch alone only registers the handler DEFINITION - no
                // [HttpEndpoint] route, no envelope - so mesh:dispatch had no HTTP path reaching it at
                // all. It now gets its own UseBenzeneMessage envelope, mirroring the fleet-query plane
                // above and examples/AwsMesh/Mesh/Startup.cs's own DispatchPath pattern, with
                // UseMeshDispatchGuard mounted directly ahead of it (CSRF header, fail-closed identity,
                // payload bound, per-identity rate limit - see that middleware's remarks).
                //
                // DispatchRole is enforced in MeshAuthGate itself (see its remarks and
                // MeshAuthGate.DispatchPath), NOT here via AuthorizationExtensions.RequireRole on the
                // envelope below: that envelope's inner pipeline runs in a freshly-created DI scope
                // (MiddlewareApplication.HandleAsync's serviceResolverFactory.CreateScope()), so a
                // RequireRole placed on the `dispatch =>` callback would never see the
                // AuthenticationHolder MeshAuthGate populated on HttpContext.RequestServices - it would
                // see no principal at all and refuse every dispatch attempt outright, role or no role.
                // MeshAuthGate runs directly against HttpContext, before any of this, so it is the one
                // place that check can actually work.
                if (_config.Dispatch.Enabled)
                {
                    // Defaults only (Path "/mesh/dispatch", Topic benzene:mesh:dispatch) - one instance
                    // shared with the envelope below so the guard's path and the envelope it guards can
                    // never drift apart, and equal to MeshAuthGate.DispatchPath (see its remarks).
                    var dispatchGuardOptions = new MeshDispatchGuardOptions();
                    asp.UseMeshDispatchGuard(dispatchGuardOptions);
                    asp.UseMeshDispatch(new MeshDispatchOptions { AllowInProduction = _config.Dispatch.AllowInProduction });
                    asp.UseBenzeneMessage(
                        new BenzeneMessageHttpOptions
                        {
                            Path = dispatchGuardOptions.Path,
                            TopicFilter = topic => string.Equals(
                                topic, global::Benzene.Mesh.Dispatch.Extensions.DispatchTopic, StringComparison.OrdinalIgnoreCase),
                        },
                        dispatch => dispatch.UseMessageHandlers(typeof(MeshDispatchMessageHandler)));
                }

                asp.UseMessageHandlers();
            })
        );

        app.UseEndpoints(endpoints => { });
    }

    private bool IsFileArtifactStore => _config.ArtifactStore.Type.Equals("file", StringComparison.OrdinalIgnoreCase);
}
