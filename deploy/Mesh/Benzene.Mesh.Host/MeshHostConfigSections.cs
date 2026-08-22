namespace Benzene.Mesh.Host;

/// <summary>
/// Where the aggregator's generated catalog artifacts live (<c>manifest.json</c>, <c>services/*.json</c>,
/// <c>topology.json</c> and the rest of the artifact set - see <c>Benzene.Mesh.Artifacts</c>'s allow-list).
/// Every field here is mutable with a default, matching <see cref="MeshHostConfig"/>'s own binder-driven
/// style. Modelled as a name plus a loose <see cref="Dictionary{TKey,TValue}"/> of options (mirroring
/// <see cref="MeshHostServiceConfig.SourceOptions"/>'s existing precedent) rather than a typed class per
/// backend, so adding a fifth store later doesn't need a new config type - see
/// <see cref="MeshSourceRegistrar"/> for the valid <see cref="Type"/> values and what each one reads from
/// <see cref="Options"/>.
/// </summary>
public class MeshArtifactStoreConfig
{
    /// <summary>Which backend to use. Defaults to <c>"file"</c> - the local filesystem, rooted at <see cref="MeshHostConfig.ArtifactRootDirectory"/>.</summary>
    public string Type { get; set; } = "file";

    /// <summary>
    /// Backend-specific settings - e.g. <c>{"bucket": "...", "prefix": "..."}</c> for <c>s3</c>/<c>gcs</c>,
    /// or <c>{"blobServiceUri": "...", "container": "...", "prefix": "..."}</c> for <c>azureBlob</c>.
    /// Unused (and safely omitted) for the default <c>file</c> type, which reads
    /// <see cref="MeshHostConfig.ArtifactRootDirectory"/> instead.
    /// </summary>
    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>
/// One <c>usage[]</c> entry - an additional <c>IMeshUsageSource</c> the aggregator reads back into
/// <c>usage.json</c> each run. Unlike <see cref="MeshFleetConfig"/> this is an array:
/// <c>IMeshUsageSource</c> is resolved as <c>IEnumerable&lt;&gt;</c>, so several may be configured at once
/// (e.g. a CloudWatch feed and an Application Insights feed, on a deployment that spans both clouds).
/// </summary>
public class MeshUsageSourceConfig
{
    /// <summary>Which usage source to register. See <see cref="MeshSourceRegistrar"/> for the valid values.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Source-specific settings, e.g. <c>{"namespace": "...", "windowHours": "24"}</c> for <c>cloudwatch</c>.</summary>
    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>
/// The fleet (live traffic) view's data source. Deliberately an object, not an array:
/// <c>Benzene.Mesh.Collector.CompositeMeshFleetReadModel</c> takes a single <c>IMeshTraceSource</c>, not
/// an <c>IEnumerable&lt;&gt;</c>, so only one fleet source can be composed today - see
/// <c>work/enterprise/README.md</c>'s deferred-work note on widening this. Configuring more than one here
/// is a config-shape error (there is nowhere for a second one to plug in), not a supported "combine them"
/// request.
/// </summary>
public class MeshFleetConfig
{
    /// <summary>Which fleet source to register. Defaults to <c>"none"</c> - no live Fleet plane. See <see cref="MeshSourceRegistrar"/> for the valid values.</summary>
    public string Source { get; set; } = "none";

    /// <summary>Source-specific settings, e.g. <c>{"url": "...", "correlationLookbackHours": "24"}</c>. Unused for <c>none</c>.</summary>
    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>The service-graph topology view's data source (the tempo-sourced edges in <c>topology.json</c>, alongside the structural edges the aggregator always derives).</summary>
public class MeshTopologyConfig
{
    /// <summary>Which topology source to register. Defaults to <c>"none"</c>. See <see cref="MeshSourceRegistrar"/> for the valid values.</summary>
    public string Source { get; set; } = "none";

    /// <summary>Source-specific settings, e.g. <c>{"prometheusUrl": "...", "windowMinutes": "5"}</c>. Unused for <c>none</c>.</summary>
    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>
/// Opt-in live dispatch (the <c>mesh:dispatch</c> handler that invokes a registered service's REAL
/// handler with a chosen payload). Off by default: it fires real side-effects (DB writes, downstream
/// calls, the handler's own publishes), so it is a deliberate, non-default choice.
/// </summary>
public class MeshDispatchConfig
{
    /// <summary>Whether dispatch is wired at all. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether dispatch (when <see cref="Enabled"/>) is also permitted in a Production environment. Off
    /// by default - dispatch runs real handlers, so Production requires this explicit second opt-in (an
    /// unset environment counts as Production).
    /// </summary>
    public bool AllowInProduction { get; set; }
}

/// <summary>
/// Auth in the host (work/enterprise/slice-2-auth.md). <see cref="MeshAuthGate"/> is the single
/// place every mode below is enforced - see its remarks for why one gate covers both the
/// <c>/artifacts</c> static-file branch and the Benzene pipeline branch of <c>Startup.Configure</c>.
/// Defaults to <c>"none"</c>: the host requires no authentication, today's default and the local-dev
/// experience slice 1 shipped.
/// </summary>
public class MeshAuthConfig
{
    /// <summary>The auth mode: <c>none</c> (default), <c>proxy</c>, <c>basic</c>, or <c>oidc</c>. See <see cref="MeshAuthGate"/>.</summary>
    public string Mode { get; set; } = "none";

    /// <summary>
    /// If non-empty, an authenticated caller's email domain must be in this list (case-insensitive) or
    /// the request is <c>Forbidden</c>. Empty (the default) means any authenticated caller is
    /// permitted - domain filtering is opt-in.
    /// </summary>
    public string[] AllowedEmailDomains { get; set; } = Array.Empty<string>();

    /// <summary>
    /// If non-empty, an authenticated caller must hold at least one of these groups/roles (read from
    /// a <c>groups</c> claim or the common role claim types) or the request is <c>Forbidden</c>. Empty
    /// (the default) means any authenticated caller is permitted.
    /// </summary>
    public string[] RequiredGroups { get; set; } = Array.Empty<string>();

    /// <summary>
    /// When set (and <see cref="MeshDispatchConfig.Enabled"/> is true), <c>mesh:dispatch</c>
    /// additionally requires the caller hold this role/group - "who may fire the button that invokes
    /// real handlers" on top of "who may read the dashboard". <c>null</c> (the default) means dispatch
    /// needs no role beyond ordinary authentication.
    /// </summary>
    public string? DispatchRole { get; set; }

    /// <summary>Mode <c>proxy</c>'s settings - see <see cref="MeshAuthGate"/>.</summary>
    public MeshAuthProxyConfig Proxy { get; set; } = new();

    /// <summary>Mode <c>oidc</c>'s settings - see <see cref="MeshAuthGate"/>.</summary>
    public MeshAuthOidcConfig Oidc { get; set; } = new();

    /// <summary>The push-ingestion endpoint's (<c>/mesh/report</c>) own, separate auth - see <see cref="MeshAuthGate"/>.</summary>
    public MeshAuthIngestionConfig Ingestion { get; set; } = new();
}

/// <summary>
/// Mode <c>proxy</c>'s settings: trust an upstream front door's already-authenticated identity header
/// (oauth2-proxy, an ALB+Cognito authenticator, Azure App Proxy) instead of the host performing its
/// own login.
/// </summary>
public class MeshAuthProxyConfig
{
    /// <summary>The request header carrying the authenticated identity (e.g. an email/username).</summary>
    public string UserHeader { get; set; } = "X-Forwarded-User";

    /// <summary>
    /// The peer addresses allowed to set <see cref="UserHeader"/>. <b>Must be non-empty for mode
    /// <c>proxy</c> to start</b> - an unrestricted forwarded-identity header is a total authentication
    /// bypass, since anyone who can reach the host could set it themselves. See <see cref="MeshAuthGate"/>.
    /// </summary>
    public string[] TrustedProxies { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Optional request header carrying the caller's groups/roles as a comma-separated list (e.g.
    /// oauth2-proxy's <c>X-Forwarded-Groups</c>), trusted from the same <see cref="TrustedProxies"/>
    /// peers as <see cref="UserHeader"/>. Each value is added as a role claim, so it feeds both
    /// <see cref="MeshAuthConfig.RequiredGroups"/> and <see cref="MeshAuthConfig.DispatchRole"/>. Null
    /// (the default) means mode <c>proxy</c> establishes an identity with no roles - matching today's
    /// behaviour.
    /// </summary>
    public string? GroupsHeader { get; set; }
}

/// <summary>
/// Mode <c>oidc</c>'s settings: interactive browser login (authorization-code redirect + cookie
/// session) against any OIDC-compliant authority - the customer's own SSO and social login (Google,
/// Okta, Entra ID, Auth0, Keycloak, ...) are the same feature once the authority is configuration.
/// </summary>
public class MeshAuthOidcConfig
{
    /// <summary>The OIDC authority (issuer) URL - its <c>/.well-known/openid-configuration</c> document drives discovery.</summary>
    public string? Authority { get; set; }

    /// <summary>The OAuth2/OIDC client id registered with the authority.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The <b>name</b> of the environment variable holding the client secret - never the secret
    /// itself. <c>mesh.json</c> gets committed to customers' repositories; see
    /// <c>work/enterprise/README.md</c>'s house rule on credentials.
    /// </summary>
    public string ClientSecretEnvVar { get; set; } = "MESH_OIDC_CLIENT_SECRET";

    /// <summary>The path the authority redirects back to after login, on this host.</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>
    /// The OIDC scopes requested at login. Empty (the default) means <see cref="DefaultScopes"/> -
    /// kept empty here, not pre-populated, because <c>IConfiguration.Get&lt;T&gt;()</c> APPENDS a
    /// config-supplied array onto a non-empty default instead of replacing it (the same reason every
    /// other array-typed property in this file defaults to <see cref="Array.Empty{T}"/>) - a
    /// pre-populated default here would make <c>scopes</c> silently un-overridable from config.
    /// </summary>
    public string[] Scopes { get; set; } = Array.Empty<string>();

    /// <summary>The scopes used when <see cref="Scopes"/> is empty/unset.</summary>
    public static readonly string[] DefaultScopes = { "openid", "profile", "email" };
}

/// <summary>
/// The push-ingestion endpoint's (<c>/mesh/report</c>) own, separate auth. Cookie-based browser login
/// (every other mode above) cannot cover it - the caller is a service self-reporting, not a browser -
/// so it is controlled independently of <see cref="MeshAuthConfig.Mode"/>. See <see cref="MeshAuthGate"/>.
/// </summary>
public class MeshAuthIngestionConfig
{
    /// <summary>
    /// <c>open</c> (default): today's behaviour, no check - preserves the local-dev/demo experience.
    /// <c>sharedSecret</c>: the request must carry <see cref="MeshAuthGate.IngestSecretHeaderName"/>
    /// matching the <c>MESH_INGEST_SECRET</c> environment variable (constant-time compared).
    /// </summary>
    public string Mode { get; set; } = "open";
}
