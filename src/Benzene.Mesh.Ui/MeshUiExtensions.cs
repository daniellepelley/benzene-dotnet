using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Benzene.Http;

namespace Benzene.Mesh.Ui;

/// <summary>
/// Pipeline wiring for the Benzene Mesh Explorer - a catalog viewer for a service mesh's
/// generated <c>manifest.json</c>/<c>services/*.json</c> artifacts (see
/// <c>Benzene.Mesh.Aggregator</c>). Transport-agnostic, so it works on AWS Lambda, Azure
/// Functions, ASP.NET Core, or the self-host server alike - though the primary deployment target
/// is a plain static file host, not a Benzene pipeline at all (see <see cref="MeshUiMiddleware{TContext}"/>).
/// </summary>
public static class MeshUiExtensions
{
    /// <summary>The default path the mesh UI is served from.</summary>
    public const string DefaultPath = "/mesh-ui";

    /// <summary>
    /// The default URL the UI fetches <c>manifest.json</c> from - a relative path, since the
    /// realistic case is the HTML sitting in the same directory as the aggregator's generated
    /// artifacts (unlike <c>Benzene.Spec.Ui</c>'s default, which points at a route on the same
    /// live service).
    /// </summary>
    public const string DefaultManifestUrl = "manifest.json";

    /// <summary>
    /// Serves the Benzene Mesh Explorer page at <paramref name="path"/> on any HTTP pipeline. This
    /// is a secondary convenience - the primary deployment target is a plain static file host
    /// serving <c>mesh-ui.html</c> alongside the aggregator's published artifacts, needing no
    /// Benzene pipeline at all. Add this before the message-handler middleware.
    /// </summary>
    /// <typeparam name="TContext">The HTTP context type.</typeparam>
    /// <param name="app">The middleware pipeline builder.</param>
    /// <param name="path">The path to serve the UI from. Defaults to <see cref="DefaultPath"/>.</param>
    /// <param name="manifestUrl">
    /// The URL the UI fetches <c>manifest.json</c> from. Defaults to <see cref="DefaultManifestUrl"/>.
    /// </param>
    /// <param name="envelopeUrl">
    /// The wire-envelope endpoint the page's live Fleet plane polls for <c>mesh:query:*</c> data
    /// (same-origin path or absolute URL). When null (the default) the page serves the static catalog
    /// viewer only; when set — e.g. <see cref="DefaultEnvelopeUrl"/> on a mesh Lambda that also hosts
    /// a <c>Benzene.Mesh.Collector</c> — the catalog is enriched with live health, observed consumers,
    /// recent flows, and a Fleet landing view. This folds in what <c>UseMeshFleetUi</c> served as a
    /// separate page.
    /// </param>
    /// <param name="dispatchUrl">
    /// The wire-envelope endpoint the page's Test Console POSTs <c>mesh:dispatch</c> messages to
    /// (same-origin path or absolute URL). When null (the default) the Test Console renders read-only
    /// (compose and copy a payload, no send button); when set — e.g. <see cref="DefaultDispatchUrl"/>
    /// on a mesh host that also wires <c>Benzene.Mesh.Dispatch</c>'s <c>UseMeshDispatch()</c> — it can
    /// send a composed message and show the response. Deliberately independent of
    /// <paramref name="envelopeUrl"/>: a host that wires only <c>Benzene.Mesh.Collector</c> (read-only
    /// fleet queries) must not have this silently turn on live dispatch too, even though the two often
    /// share one endpoint in practice - pass <paramref name="dispatchUrl"/> explicitly to opt in.
    /// <c>Benzene.Mesh.Dispatch</c> itself additionally gates dispatch behind a Production check
    /// (refused unless the host sets <c>AllowInProduction</c>), independent of this parameter.
    /// </param>
    /// <param name="logoutUrl">
    /// The URL the page's Sign-out control navigates to - the host's OIDC logout route (e.g.
    /// <c>Benzene.Mesh.Auth.Oidc</c>'s <c>{BasePath}/logout</c>). When null (the default) the page
    /// renders no Sign-out control, which is the right behaviour for an ungated host: a page nobody
    /// had to log into has nothing to sign out of. There is deliberately no constant default for this
    /// one - the logout route is the auth package's configurable <c>BasePath</c> plus <c>/logout</c>,
    /// and only the host knows its <c>BasePath</c>.
    /// </param>
    /// <param name="refreshUrl">
    /// The endpoint the page's Refresh control POSTs to, to trigger a discovery/aggregation pass -
    /// e.g. <see cref="DefaultRefreshUrl"/> on a host that routes an aggregate handler over HTTP. When
    /// null (the default) the page stays read-only. Deliberately a separate opt-in, for exactly the
    /// reason <paramref name="dispatchUrl"/> is: a host that happens to have wired auth, or an
    /// aggregator, must not thereby acquire a button that fans out to every service in the mesh and
    /// rewrites the whole catalog on each click. Passing it is a statement that the host also guards
    /// that endpoint - <c>Benzene.Mesh.Artifacts</c>'s <c>UseMeshRefreshGuard()</c> is the matching
    /// server side, and the page's POST carries the <c>X-Benzene-Refresh</c> header it requires.
    /// </param>
    /// <param name="environment">
    /// Which estate this deployment looks at - <c>production</c>, <c>staging</c>, <c>dev-pr-412</c>.
    /// Free text, configured at deploy time, and never inferred from a hostname. Rendered in the
    /// page's chrome on every screen, because a dev mesh and a production mesh are otherwise
    /// identical on screen and only the address bar distinguishes them.
    /// <para>
    /// Null is the current default and is honest: nothing publishes an environment until
    /// <c>placement.environment</c> reaches the spec, and the page says so rather than guessing. An
    /// unlabelled production mesh that rendered "dev" is the outcome this exists to prevent.
    /// </para>
    /// </param>
    /// <returns>The middleware pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseMeshUi<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app,
        string path = DefaultPath,
        string manifestUrl = DefaultManifestUrl,
        string? envelopeUrl = null,
        string? dispatchUrl = null,
        string? logoutUrl = null,
        string? refreshUrl = null,
        string? environment = null)
        where TContext : IHttpContext
    {
        app.Register(x =>
            x.AddSingleton(resolver => new MeshUiMiddleware<TContext>(
                path, manifestUrl, envelopeUrl, dispatchUrl, logoutUrl, refreshUrl, environment,
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>()
            )));

        return app.Use<TContext, MeshUiMiddleware<TContext>>();
    }

    /// <summary>
    /// The default path the mesh-hosted Spec Explorer is served from. It ends in <c>.html</c> so the
    /// single relative link <c>mesh-ui.html</c> builds (<c>mesh-spec-ui.html?service=…</c>) resolves
    /// correctly whether the mesh UI is a static file next to the artifacts or served from this
    /// pipeline at <c>/mesh-ui</c> - see <see cref="MeshSpecUiMiddleware{TContext}"/>.
    /// </summary>
    public const string DefaultSpecUiPath = "/mesh-spec-ui.html";

    /// <summary>
    /// Serves the mesh-hosted Spec Explorer page (<see cref="MeshSpecUiPage"/>) at
    /// <paramref name="path"/> - the per-service spec view <c>mesh-ui.html</c>'s <em>spec</em> link
    /// opens. It renders the verbatim spec the aggregator captured into the same-origin
    /// <c>services/{name}.json</c> snapshot, so a mesh service only ever serves JSON, never HTML. Pair
    /// it with <see cref="UseMeshUi{TContext}"/> (and the artifact-serving middleware) on the same
    /// pipeline. Like <see cref="UseMeshUi{TContext}"/> this is a secondary convenience - a static file
    /// host serving <c>mesh-spec-ui.html</c> alongside the artifacts needs no Benzene pipeline at all.
    /// </summary>
    /// <typeparam name="TContext">The HTTP context type.</typeparam>
    /// <param name="app">The middleware pipeline builder.</param>
    /// <param name="path">The path to serve the spec UI from. Defaults to <see cref="DefaultSpecUiPath"/>.</param>
    /// <param name="manifestUrl">
    /// The default URL the page resolves <c>services/{name}.json</c> against when opened without a
    /// <c>?manifest=</c> query parameter. Defaults to <see cref="DefaultManifestUrl"/>.
    /// </param>
    /// <returns>The middleware pipeline builder, for chaining.</returns>
    public static IMiddlewarePipelineBuilder<TContext> UseMeshSpecUi<TContext>(
        this IMiddlewarePipelineBuilder<TContext> app,
        string path = DefaultSpecUiPath,
        string manifestUrl = DefaultManifestUrl)
        where TContext : IHttpContext
    {
        app.Register(x =>
            x.AddSingleton(resolver => new MeshSpecUiMiddleware<TContext>(
                path, manifestUrl,
                resolver.GetService<IHttpRequestAdapter<TContext>>(),
                resolver.GetService<IBenzeneResponseAdapter<TContext>>()
            )));

        return app.Use<TContext, MeshSpecUiMiddleware<TContext>>();
    }

    /// <summary>The default wire-envelope endpoint the mesh UI's live Fleet plane polls, following the
    /// default service standard's <c>/benzene/</c> prefix (docs/specification/design-principles.md §5).
    /// Pass it as <c>UseMeshUi</c>'s <c>envelopeUrl</c> on a mesh host that also serves a
    /// <c>Benzene.Mesh.Collector</c> over the wire envelope.</summary>
    public const string DefaultEnvelopeUrl = "/benzene/invoke";

    /// <summary>The default wire-envelope endpoint the mesh UI's Test Console sends
    /// <c>mesh:dispatch</c> messages to. Same value as <see cref="DefaultEnvelopeUrl"/> - dispatch
    /// typically rides the same message endpoint fleet queries already use - but named separately
    /// since the two are independent opt-ins (see <c>UseMeshUi</c>'s <c>dispatchUrl</c> remarks). Pass
    /// it as <c>UseMeshUi</c>'s <c>dispatchUrl</c> on a mesh host that also wires
    /// <c>Benzene.Mesh.Dispatch</c>'s <c>UseMeshDispatch()</c> on the same pipeline.</summary>
    public const string DefaultDispatchUrl = "/benzene/invoke";

    /// <summary>The conventional path a mesh host routes its on-demand aggregation pass on, and so the
    /// value to pass as <c>UseMeshUi</c>'s <c>refreshUrl</c> - matching
    /// <c>Benzene.Mesh.Artifacts.MeshRefreshGuardOptions.DefaultPath</c>, the guard that stands in front
    /// of it. Named rather than defaulted-on because opting in is the host's explicit decision (see
    /// <c>UseMeshUi</c>'s <c>refreshUrl</c> remarks).</summary>
    public const string DefaultRefreshUrl = "/mesh/refresh";
}
